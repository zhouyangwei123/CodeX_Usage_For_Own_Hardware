using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodexToolsHost.Mijia
{
    public sealed class MijiaService : IDisposable
    {
        private readonly MijiaSettings _settings;
        private readonly IMijiaCliRunner _runner;
        private MijiaLoginSession _login;
        private readonly object _commandStateSync = new object();
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private readonly Timer _refreshTimer;
        private bool _loginOwnsCommandGate;
        private bool _loginVerificationPending;
        private bool _environmentValidated;
        private string _validatedExecutable;
        private string _validatedAuthPath;
        private bool _started;
        private bool _disposed;

        public MijiaService(MijiaSettings settings)
            : this(settings, new MijiaCliRunner()) { }

        public MijiaService(MijiaSettings settings, IMijiaCliRunner runner)
        {
            _settings = settings ?? new MijiaSettings();
            _settings.Shortcuts = MijiaSettings.NormalizeShortcuts(_settings.Shortcuts);
            _settings.RefreshMinutes = MijiaSettings.NormalizeRefreshMinutes(
                _settings.RefreshMinutes);
            _runner = runner ?? new MijiaCliRunner();
            Scenes = new MijiaScene[0];
            _refreshTimer = new Timer(delegate { SafeAutoRefresh(); }, null,
                Timeout.Infinite, Timeout.Infinite);
        }

        /* 这些事件可能从 CLI continuation 或 Process.Exited 的后台线程触发；
         * WinForms 消费者（Task 2）必须在订阅端封送到 UI 线程。 */
        public event EventHandler ScenesChanged;
        public event EventHandler StateChanged;
        public event EventHandler LoginChanged;
        public event EventHandler QrCodeUrlChanged;
        public MijiaScene[] Scenes { get; private set; }
        public string LastError { get; private set; }
        public string QrCodeUrl { get; private set; }
        public bool IsRefreshing { get; private set; }
        public bool IsBusy { get; private set; }
        public bool LoginInProgress
        {
            get { lock (_commandStateSync) return _login != null; }
        }
        public bool CanStartLogin
        {
            get
            {
                lock (_commandStateSync)
                {
                    return !_disposed && !_loginVerificationPending && _login == null
                        && _commandGate.CurrentCount > 0
                        && !string.IsNullOrEmpty(ResolveConfiguredExecutable());
                }
            }
        }
        public bool CanRefreshLogin
        {
            get
            {
                lock (_commandStateSync)
                {
                    return !_disposed && !_loginVerificationPending && _login != null
                        && _login.IsRunning;
                }
            }
        }
        public bool EnvironmentReady
        {
            get
            {
                return !_disposed && _environmentValidated
                    && IsCurrentValidation();
            }
        }
        public bool CanRefreshScenes
        {
            get
            {
                if (_disposed || string.IsNullOrEmpty(ResolveConfiguredExecutable()))
                    return false;
                string authPath = CurrentAuthPath();
                return string.IsNullOrEmpty(authPath) || File.Exists(authPath);
            }
        }
        public string EnvironmentStatus
        {
            get
            {
                if (string.IsNullOrEmpty(ResolveConfiguredExecutable()))
                    return "未找到 mijiaAPI.exe";
                if (!string.IsNullOrWhiteSpace(_settings.AuthPath)
                    && !File.Exists(_settings.AuthPath)) return "认证文件尚未生成（可扫码登录）";
                return EnvironmentReady ? "环境已验证" : "路径可尝试，等待刷新场景验证";
            }
        }

        public void Start()
        {
            if (_disposed || _started) return;
            _started = true;
            ConfigureAutoRefresh(true);
        }

        public void ApplySettings()
        {
            _settings.Shortcuts = MijiaSettings.NormalizeShortcuts(_settings.Shortcuts);
            _settings.RefreshMinutes = MijiaSettings.NormalizeRefreshMinutes(
                _settings.RefreshMinutes);
            RevokeValidationIfConfigurationChanged();
            ConfigureAutoRefresh(false);
            OnStateChanged();
        }

        public Task<bool> RefreshScenesAsync()
        {
            return RefreshScenesAsync(false, false);
        }

        private async Task<bool> RefreshScenesAsync(bool waitForGate, bool loginVerification)
        {
            bool gateEntered;
            if (waitForGate)
            {
                await _commandGate.WaitAsync().ConfigureAwait(false);
                gateEntered = true;
            }
            else gateEntered = _commandGate.Wait(0);
            if (!gateEntered) return false;
            lock (_commandStateSync)
            {
                bool blockedByLogin = !loginVerification
                    && (_login != null || _loginVerificationPending);
                bool invalidVerification = loginVerification && !_loginVerificationPending;
                if (_disposed || blockedByLogin || invalidVerification)
                {
                    _commandGate.Release();
                    return false;
                }
                IsRefreshing = true;
                IsBusy = true;
                LastError = null;
            }
            OnStateChanged();
            try
            {
                CommandSnapshot snapshot = CaptureCommandSnapshot();
                if (string.IsNullOrEmpty(snapshot.Executable))
                {
                    RevokeValidation();
                    return Fail("未找到 mijiaAPI.exe");
                }
                MijiaCommandResult result = await _runner.RunAsync(snapshot.Executable,
                    BuildCommandArguments("--list_scenes", null, snapshot.AuthPath),
                    15000).ConfigureAwait(false);
                if (_disposed) return false;
                if (result.TimedOut || result.ExitCode != 0)
                {
                    RevokeValidation();
                    return Fail("获取米家场景失败");
                }
                if (!IsCurrentSnapshot(snapshot))
                {
                    RevokeValidation();
                    return Fail("配置已变化，请重新刷新场景验证");
                }
                Scenes = MijiaCliParser.ParseScenes(result.StandardOutput);
                InvalidateMissingShortcuts();
                MarkEnvironmentValidated(snapshot);
                LastError = null;
                EventHandler changed = ScenesChanged;
                if (changed != null) changed(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                if (_disposed) return false;
                RevokeValidation();
                return Fail(ex.Message);
            }
            finally
            {
                lock (_commandStateSync)
                {
                    if (!_disposed)
                    {
                        IsRefreshing = false;
                        IsBusy = false;
                    }
                }
                _commandGate.Release();
                OnStateChanged();
            }
        }

        public async Task<bool> ExecuteShortcutAsync(int shortcutIndex)
        {
            MijiaShortcutExecutionResult result = await ExecuteShortcutWithResultAsync(
                shortcutIndex).ConfigureAwait(false);
            return result.Succeeded;
        }

        public async Task<MijiaShortcutExecutionResult> ExecuteShortcutWithResultAsync(
            int shortcutIndex)
        {
            if (_disposed || !_commandGate.Wait(0)) return BusyExecutionResult();
            lock (_commandStateSync)
            {
                if (_disposed)
                {
                    _commandGate.Release();
                    return new MijiaShortcutExecutionResult(false, false, "米家服务不可用");
                }
                if (_login != null || _loginVerificationPending)
                {
                    _commandGate.Release();
                    return BusyExecutionResult();
                }
                IsBusy = true;
                LastError = null;
            }
            OnStateChanged();
            try
            {
                if (shortcutIndex < 0 || shortcutIndex >= _settings.Shortcuts.Length)
                    return FailExecution("米家快捷方式不存在");
                string sceneId = _settings.Shortcuts[shortcutIndex].SceneId;
                if (!MijiaCliParser.IsSceneId(sceneId))
                    return FailExecution("米家场景 ID 无效");
                if (!IsShortcutAvailable(shortcutIndex))
                    return FailExecution("绑定的米家场景当前不可用");
                CommandSnapshot snapshot = CaptureCommandSnapshot();
                if (string.IsNullOrEmpty(snapshot.Executable))
                    return FailExecution("未找到 mijiaAPI.exe");
                MijiaCommandResult result = await _runner.RunAsync(snapshot.Executable,
                    BuildCommandArguments("--run_scene", sceneId, snapshot.AuthPath),
                    15000).ConfigureAwait(false);
                if (_disposed)
                    return new MijiaShortcutExecutionResult(false, false, "米家服务不可用");
                if (result.TimedOut || result.ExitCode != 0)
                    return FailExecution("执行米家场景失败");
                LastError = null;
                OnStateChanged();
                return new MijiaShortcutExecutionResult(true, false, "");
            }
            catch (Exception ex)
            {
                if (_disposed)
                    return new MijiaShortcutExecutionResult(false, false, "米家服务不可用");
                return FailExecution(ex.Message);
            }
            finally
            {
                lock (_commandStateSync)
                {
                    if (!_disposed) IsBusy = false;
                }
                _commandGate.Release();
                OnStateChanged();
            }
        }

        public void StartLogin()
        {
            MijiaLoginSession replacedSession = null;
            lock (_commandStateSync)
            {
                if (_disposed || _loginVerificationPending) return;
                if (_login != null)
                {
                    if (!_login.IsRunning) return;
                    replacedSession = _login;
                    _login = null;
                }
                else
                {
                    if (!_commandGate.Wait(0)) return;
                    _loginOwnsCommandGate = true;
                }
            }

            SetQrCodeUrl(null);
            if (replacedSession != null) replacedSession.Dispose();
            try
            {
                CommandSnapshot snapshot = CaptureCommandSnapshot();
                if (string.IsNullOrEmpty(snapshot.Executable))
                    throw new InvalidOperationException("未找到 mijiaAPI.exe");
                MijiaLoginSession session = _runner.StartLogin(snapshot.Executable,
                    snapshot.AuthPath);
                lock (_commandStateSync)
                {
                    if (_disposed)
                    {
                        session.Dispose();
                        ReleaseLoginCommandGateLocked();
                        return;
                    }
                    _login = session;
                    LastError = null;
                    session.QrCodeUrlReceived += delegate(string url)
                    {
                        LoginQrCodeUrlReceived(session, url);
                    };
                    session.Exited += delegate(object sender, EventArgs args)
                    {
                        ObserveBackground(LoginExitedAsync(session));
                    };
                }
            }
            catch (Exception ex)
            {
                lock (_commandStateSync)
                {
                    _login = null;
                    ReleaseLoginCommandGateLocked();
                    if (!_disposed) LastError = ex.Message;
                }
            }
            OnLoginChanged();
            OnStateChanged();
        }

        public void CancelLogin()
        {
            MijiaLoginSession session;
            lock (_commandStateSync)
            {
                if (_disposed || _login == null || _loginVerificationPending) return;
                session = _login;
                _login = null;
            }
            session.Dispose();
            lock (_commandStateSync) ReleaseLoginCommandGateLocked();
            SetQrCodeUrl(null);
            OnLoginChanged();
            OnStateChanged();
        }

        public bool IsShortcutAvailable(int shortcutIndex)
        {
            if (shortcutIndex < 0 || shortcutIndex >= _settings.Shortcuts.Length) return false;
            string sceneId = _settings.Shortcuts[shortcutIndex].SceneId;
            if (!MijiaCliParser.IsSceneId(sceneId)) return false;
            if (!EnvironmentReady) return false;
            foreach (MijiaScene scene in Scenes)
                if (scene.Id == sceneId) return true;
            return false;
        }

        public void Dispose()
        {
            MijiaLoginSession session;
            lock (_commandStateSync)
            {
                if (_disposed) return;
                _disposed = true;
                session = _login;
                _login = null;
                _loginVerificationPending = false;
            }
            _refreshTimer.Dispose();
            if (session != null) session.Dispose();
            lock (_commandStateSync) ReleaseLoginCommandGateLocked();
            QrCodeUrl = null;
        }

        private bool Fail(string message)
        {
            if (_disposed) return false;
            LastError = message;
            OnStateChanged();
            return false;
        }

        private MijiaShortcutExecutionResult FailExecution(string message)
        {
            Fail(message);
            return new MijiaShortcutExecutionResult(false, false, message);
        }

        private static MijiaShortcutExecutionResult BusyExecutionResult()
        {
            return new MijiaShortcutExecutionResult(false, true,
                "米家正在执行其他操作，请稍后重试");
        }

        private string ResolveConfiguredExecutable()
        {
            return MijiaCliRunner.ResolveExecutable(_settings.ExecutablePath);
        }

        private string CurrentAuthPath()
        {
            return (_settings.AuthPath ?? "").Trim();
        }

        private CommandSnapshot CaptureCommandSnapshot()
        {
            return new CommandSnapshot(ResolveConfiguredExecutable(), CurrentAuthPath());
        }

        private bool IsCurrentSnapshot(CommandSnapshot snapshot)
        {
            return snapshot != null
                && string.Equals(snapshot.Executable, ResolveConfiguredExecutable(),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.AuthPath, CurrentAuthPath(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCurrentValidation()
        {
            if (!_environmentValidated) return false;
            return string.Equals(_validatedExecutable, ResolveConfiguredExecutable(),
                StringComparison.OrdinalIgnoreCase)
                && string.Equals(_validatedAuthPath, CurrentAuthPath(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private void MarkEnvironmentValidated(CommandSnapshot snapshot)
        {
            _validatedExecutable = snapshot.Executable;
            _validatedAuthPath = snapshot.AuthPath;
            _environmentValidated = true;
            ConfigureAutoRefresh(false);
        }

        private void RevokeValidationIfConfigurationChanged()
        {
            if (_environmentValidated && !IsCurrentValidation()) RevokeValidation();
        }

        private void RevokeValidation()
        {
            _environmentValidated = false;
            _validatedExecutable = null;
            _validatedAuthPath = null;
            ConfigureAutoRefresh(false);
        }

        private void InvalidateMissingShortcuts()
        {
            var ids = new HashSet<string>();
            foreach (MijiaScene scene in Scenes) ids.Add(scene.Id);
            foreach (MijiaShortcutConfig shortcut in _settings.Shortcuts)
            {
                if (shortcut == null) continue;
                if (!ids.Contains(shortcut.SceneId)) continue;
                foreach (MijiaScene scene in Scenes)
                    if (scene.Id == shortcut.SceneId) shortcut.SceneName = scene.Name;
            }
        }

        private void LoginQrCodeUrlReceived(MijiaLoginSession session, string url)
        {
            if (_disposed || !ReferenceEquals(_login, session)) return;
            SetQrCodeUrl(url);
            OnLoginChanged();
        }

        private async Task LoginExitedAsync(MijiaLoginSession session)
        {
            try
            {
                lock (_commandStateSync)
                {
                    if (_disposed || !ReferenceEquals(_login, session)
                        || _loginVerificationPending) return;
                    _loginVerificationPending = true;
                    ReleaseLoginCommandGateLocked();
                }
                OnLoginChanged();
                OnStateChanged();
                bool valid = await RefreshScenesAsync(true, true).ConfigureAwait(false);
                lock (_commandStateSync)
                {
                    if (_disposed || !ReferenceEquals(_login, session)) return;
                    _login = null;
                    _loginVerificationPending = false;
                }
                session.Dispose();
                if (valid)
                {
                    LastError = null;
                    SetQrCodeUrl(null);
                    ConfigureAutoRefresh(false);
                }
                else if (string.IsNullOrEmpty(LastError))
                {
                    LastError = "二维码已过期或登录验证失败，请手动刷新二维码";
                }
                OnLoginChanged();
                OnStateChanged();
            }
            catch (Exception ex)
            {
                lock (_commandStateSync)
                {
                    if (_disposed || !ReferenceEquals(_login, session)) return;
                    _login = null;
                    _loginVerificationPending = false;
                    ReleaseLoginCommandGateLocked();
                }
                session.Dispose();
                LastError = ex.Message;
                OnLoginChanged();
                OnStateChanged();
            }
        }

        private void SetQrCodeUrl(string url)
        {
            if (_disposed) return;
            if (string.Equals(QrCodeUrl, url, StringComparison.Ordinal)) return;
            QrCodeUrl = url;
            EventHandler changed = QrCodeUrlChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private void ConfigureAutoRefresh(bool immediate)
        {
            if (_disposed) return;
            if (!_started || !_settings.AutoRefresh || !EnvironmentReady)
            {
                _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            int interval = MijiaSettings.NormalizeRefreshMinutes(
                _settings.RefreshMinutes) * 60 * 1000;
            _refreshTimer.Change(immediate ? 0 : interval, interval);
        }

        private void SafeAutoRefresh()
        {
            if (_disposed || !EnvironmentReady) return;
            Task refresh = RefreshScenesAsync();
            ObserveBackground(refresh);
        }

        private void ReleaseLoginCommandGateLocked()
        {
            if (!_loginOwnsCommandGate) return;
            _loginOwnsCommandGate = false;
            _commandGate.Release();
        }

        private static void ObserveBackground(Task task)
        {
            if (task == null) return;
            task.ContinueWith(delegate(Task failed)
            {
                if (failed.Exception != null) failed.Exception.Handle(delegate(Exception error)
                {
                    return true;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void OnStateChanged()
        {
            if (_disposed) return;
            EventHandler changed = StateChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private void OnLoginChanged()
        {
            if (_disposed) return;
            EventHandler changed = LoginChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private string BuildCommandArguments(string command, string sceneId, string authPath)
        {
            string arguments = MijiaCliRunner.QuoteArgument(command);
            if (sceneId != null) arguments += " " + MijiaCliRunner.QuoteArgument(sceneId);
            if (!string.IsNullOrEmpty(authPath)) arguments += " "
                + MijiaCliRunner.QuoteArgument("-p") + " "
                + MijiaCliRunner.QuoteArgument(authPath);
            return arguments;
        }

        private sealed class CommandSnapshot
        {
            public CommandSnapshot(string executable, string authPath)
            {
                Executable = executable;
                AuthPath = authPath;
            }

            public string Executable { get; private set; }
            public string AuthPath { get; private set; }
        }
    }
}
