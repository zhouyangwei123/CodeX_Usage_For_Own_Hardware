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
        private Process _process;
        private Stream _stdin;
        private StreamReader _stdoutReader;
        private Thread _readThread;
        private bool _stopped;

        public ProcessJsonLineTransport(string executable, string arguments)
        {
            _executable = executable;
            _arguments = arguments;
        }

        public event Action<byte[]> MessageReceived;
        public event Action<Exception> Faulted;

        public void Start()
        {
            ProcessStartInfo psi = new ProcessStartInfo(_executable, _arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            _process = Process.Start(psi);
            _stdin = _process.StandardInput.BaseStream;
            _stdoutReader = _process.StandardOutput;
            _readThread = new Thread(ReadLoop);
            _readThread.IsBackground = true;
            _readThread.Start();
        }

        private void ReadLoop()
        {
            try
            {
                while (!_stopped)
                {
                    string line = _stdoutReader.ReadLine();
                    if (line == null) break;
                    byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                    var handler = MessageReceived;
                    if (handler != null) handler(data);
                }
                if (!_stopped) RaiseFaulted(new EndOfStreamException("Codex app-server 已退出"));
            }
            catch (Exception ex)
            {
                if (!_stopped) RaiseFaulted(ex);
            }
        }

        public void Send(byte[] message)
        {
            if (_stdin == null) throw new InvalidOperationException("Transport is not started.");
            _stdin.Write(message, 0, message.Length);
            _stdin.Flush();
        }

        public void Dispose()
        {
            _stopped = true;
            try { if (_stdin != null) _stdin.Dispose(); } catch (Exception) { }
            try { if (_process != null && !_process.HasExited) _process.Kill(); } catch (Exception) { }
            try { if (_process != null) _process.Dispose(); } catch (Exception) { }
            _stdin = null;
            _process = null;
        }

        private void RaiseFaulted(Exception exception)
        {
            var handler = Faulted;
            if (handler != null) handler(exception);
        }
    }
}
