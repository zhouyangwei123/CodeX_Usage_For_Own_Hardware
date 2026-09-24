using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexToolsHost.Mijia
{
    public interface IMijiaCliRunner
    {
        Task<MijiaCommandResult> RunAsync(string exe, string arguments, int timeoutMs);
        MijiaLoginSession StartLogin(string exe, string authPath);
    }

    public sealed class MijiaLoginSession : IDisposable
    {
        private readonly Process _process;
        private readonly Timer _timeout;
        private readonly object _eventLock = new object();
        private EventHandler _exited;
        private Action<string> _qrCodeUrlReceived;
        private bool _hasExited;
        private string _qrCodeUrl;

        internal MijiaLoginSession(Process process)
            : this(process, false)
        {
        }

        internal MijiaLoginSession(Process process, bool startProcess)
        {
            if (process == null) throw new ArgumentNullException("process");
            _process = process;
            _timeout = new Timer(delegate { Cancel(); }, null, 125000, Timeout.Infinite);
            _process.OutputDataReceived += ProcessOutputReceived;
            _process.ErrorDataReceived += ProcessOutputReceived;
            _process.Exited += delegate { NotifyExitedIfNeeded(); };
            _process.EnableRaisingEvents = true;
            if (startProcess)
            {
                try
                {
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }
                catch
                {
                    _timeout.Dispose();
                    _process.Dispose();
                    throw;
                }
            }
            NotifyExitedIfNeeded();
        }

        public event EventHandler Exited
        {
            add
            {
                bool invokeNow;
                lock (_eventLock)
                {
                    _exited += value;
                    invokeNow = _hasExited;
                }
                if (invokeNow && value != null) value(this, EventArgs.Empty);
            }
            remove
            {
                lock (_eventLock) { _exited -= value; }
            }
        }
        public event Action<string> QrCodeUrlReceived
        {
            add
            {
                string url;
                lock (_eventLock)
                {
                    _qrCodeUrlReceived += value;
                    url = _qrCodeUrl;
                }
                if (url != null && value != null) value(url);
            }
            remove
            {
                lock (_eventLock) { _qrCodeUrlReceived -= value; }
            }
        }
        public bool IsRunning
        {
            get
            {
                try { return !_process.HasExited; }
                catch (Exception) { return false; }
            }
        }

        public void Cancel()
        {
            try
            {
                if (_process.HasExited) return;
                _process.Kill();
                _process.WaitForExit(2000);
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _timeout.Dispose();
            Cancel();
            _process.OutputDataReceived -= ProcessOutputReceived;
            _process.ErrorDataReceived -= ProcessOutputReceived;
            _process.Dispose();
        }

        internal static string ExtractFirstHttpsUrl(string output)
        {
            Match match = Regex.Match(output ?? "", @"https://[^\s]+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success
                ? match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '\"', '\'')
                : null;
        }

        private void ProcessOutputReceived(object sender, DataReceivedEventArgs args)
        {
            string url = ExtractFirstHttpsUrl(args == null ? null : args.Data);
            if (url == null) return;
            Action<string> handler;
            lock (_eventLock)
            {
                if (_qrCodeUrl != null) return;
                _qrCodeUrl = url;
                handler = _qrCodeUrlReceived;
            }
            if (handler != null) handler(url);
        }

        private void NotifyExitedIfNeeded()
        {
            EventHandler handler;
            lock (_eventLock)
            {
                try { if (!_process.HasExited || _hasExited) return; }
                catch (InvalidOperationException) { return; }
                _hasExited = true;
                handler = _exited;
            }
            _timeout.Dispose();
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    public sealed class MijiaCliRunner : IMijiaCliRunner
    {
        private const int CommandTimeoutMs = 15000;

        public Task<MijiaCommandResult> RunAsync(string exe, string arguments, int timeoutMs)
        {
            int cappedTimeout = IsSceneCommand(arguments)
                ? Math.Min(CommandTimeoutMs, timeoutMs <= 0 ? CommandTimeoutMs : timeoutMs)
                : timeoutMs;
            return Task.Factory.StartNew(delegate
            {
                return Run(exe, arguments, cappedTimeout);
            });
        }

        public MijiaLoginSession StartLogin(string exe, string authPath)
        {
            string arguments = QuoteArgument("login");
            if (!string.IsNullOrEmpty(authPath)) arguments += " " + QuoteArgument("-p")
                + " " + QuoteArgument(authPath);
            var process = new Process { StartInfo = CreateStartInfo(exe, arguments, true) };
            return new MijiaLoginSession(process, true);
        }

        public static string ResolveExecutable(string configuredPath)
        {
            string resolved = ResolveFile(configuredPath);
            if (resolved != null) return resolved;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                resolved = ResolveFile(Path.Combine(entry.Trim(), "mijiaAPI.exe"));
                if (resolved != null) return resolved;
            }

            string root = AppDomain.CurrentDomain.BaseDirectory;
            string[] commonVirtualEnvironments =
            {
                Path.Combine(root, ".venv", "Scripts", "mijiaAPI.exe"),
                Path.Combine(root, "venv", "Scripts", "mijiaAPI.exe"),
                Path.Combine(Environment.CurrentDirectory, ".venv", "Scripts", "mijiaAPI.exe"),
                Path.Combine(Environment.CurrentDirectory, "venv", "Scripts", "mijiaAPI.exe")
            };
            foreach (string candidate in commonVirtualEnvironments)
            {
                resolved = ResolveFile(candidate);
                if (resolved != null) return resolved;
            }
            return null;
        }

        public static string QuoteArgument(string value)
        {
            value = value ?? "";
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }
                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(character);
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private static MijiaCommandResult Run(string exe, string arguments, int timeoutMs)
        {
            using (Process process = new Process())
            {
                process.StartInfo = CreateStartInfo(exe, arguments, true);
                var output = new StringBuilder();
                var error = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null) output.AppendLine(args.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null) error.AppendLine(args.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                bool timedOut = !process.WaitForExit(timeoutMs);
                if (timedOut) process.Kill();
                process.WaitForExit();
                return new MijiaCommandResult(process.ExitCode, output.ToString(),
                    error.ToString(), timedOut);
            }
        }

        private static ProcessStartInfo CreateStartInfo(string exe, string arguments,
            bool redirectOutput)
        {
            var startInfo = new ProcessStartInfo(exe, arguments ?? "");
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = redirectOutput;
            startInfo.RedirectStandardError = redirectOutput;
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            return startInfo;
        }

        private static bool IsSceneCommand(string arguments)
        {
            return (arguments ?? "").IndexOf("--list_scenes", StringComparison.Ordinal) >= 0
                || (arguments ?? "").IndexOf("--run_scene", StringComparison.Ordinal) >= 0;
        }

        private static string ResolveFile(string candidate)
        {
            try { return !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
                ? Path.GetFullPath(candidate) : null; }
            catch (Exception) { return null; }
        }

    }
}
