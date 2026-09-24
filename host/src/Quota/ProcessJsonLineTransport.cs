using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexToolsHost.Quota
{
    public sealed class ProcessJsonLineTransport : IJsonLineTransport
    {
        private readonly string _executable;
        private readonly string _arguments;
        private readonly object _sync = new object();
        private Process _process;
        private Thread _stdoutThread;
        private Thread _stderrThread;
        private volatile bool _stopped;
        private int _faulted;
        private long _stderrCharacters;
        public long StderrCharacters { get { return Interlocked.Read(ref _stderrCharacters); } }
        public ProcessJsonLineTransport(string executable, string arguments) { _executable = executable; _arguments = arguments; }
        public event Action<byte[]> MessageReceived;
        public event Action<Exception> Faulted;
        public void Start()
        {
            lock (_sync)
            {
                if (_stopped) throw new ObjectDisposedException("ProcessJsonLineTransport");
                if (_process != null) throw new InvalidOperationException("Transport already started.");
                _process = Process.Start(new ProcessStartInfo(_executable, _arguments) {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 });
                StreamReader output = _process.StandardOutput, error = _process.StandardError;
                _stdoutThread = new Thread(delegate() { ReadOutput(output); }) { IsBackground = true, Name = "Codex stdout" };
                _stderrThread = new Thread(delegate() { DrainError(error); }) { IsBackground = true, Name = "Codex stderr" };
                _stderrThread.Start(); _stdoutThread.Start();
            }
        }
        private void ReadOutput(StreamReader reader)
        {
            try
            {
                string line;
                while (!_stopped && (line = reader.ReadLine()) != null)
                {
                    var handler = MessageReceived;
                    if (handler != null) handler(Encoding.UTF8.GetBytes(line));
                }
                if (!_stopped) RaiseFaulted(new EndOfStreamException("Codex app-server exited."));
            }
            catch (Exception) { if (!_stopped) RaiseFaulted(new IOException("Codex output stream failed.")); }
        }
        private void DrainError(StreamReader reader)
        {
            // Fixed-size buffer: no unbounded retention and no credentials in diagnostics.
            char[] buffer = new char[2048];
            try { int count; while (!_stopped && (count = reader.Read(buffer, 0, buffer.Length)) > 0) Interlocked.Add(ref _stderrCharacters, count); }
            catch (Exception) { if (!_stopped) RaiseFaulted(new IOException("Codex error stream failed.")); }
        }
        public void Send(byte[] message)
        {
            Process process;
            lock (_sync) { if (_stopped || _process == null) throw new ObjectDisposedException("ProcessJsonLineTransport"); process = _process; }
            process.StandardInput.BaseStream.Write(message, 0, message.Length);
            process.StandardInput.BaseStream.Flush();
        }
        public void Dispose()
        {
            Process process;
            lock (_sync) { if (_stopped) return; _stopped = true; process = _process; _process = null; }
            if (process == null) return;
            // Terminate only our child; do not wait for locks held by a blocked writer.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { process.WaitForExit(1000); } catch (InvalidOperationException) { }
            if (_stdoutThread != null && Thread.CurrentThread != _stdoutThread) _stdoutThread.Join(1000);
            if (_stderrThread != null && Thread.CurrentThread != _stderrThread) _stderrThread.Join(1000);
            process.Dispose();
        }
        private void RaiseFaulted(Exception error)
        {
            if (_stopped || Interlocked.Exchange(ref _faulted, 1) != 0) return;
            var handler = Faulted; if (handler != null) handler(error);
        }
    }
}
