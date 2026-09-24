using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexToolsHost.Actions;
using CodexToolsHost.Core;
using CodexToolsHost.Mijia;
using CodexToolsHost.Model;
using CodexToolsHost.Protocol;
using CodexToolsHost.UI;

namespace CodexToolsHost.Tests
{
    internal static class MijiaSelfChecks
    {
        private const string Fixture = "家庭中的场景:\n  - 全屋关灯\n    ID: 2032112121379033088\n\n  - 回家\n    ID: 2032825238332522497\n";
        private const string RestoredFixture = "家庭中的场景:\n  - 已恢复\n    ID: 999\n";
        private static string _configCheckDetails;

        public static Dictionary<string, object> Run()
        {
            var result = new Dictionary<string, object>();
            MijiaScene[] scenes = MijiaCliParser.ParseScenes(Fixture);
            result["mijiaSceneParserOk"] = scenes.Length == 2
                && scenes[0].Name == "全屋关灯"
                && scenes[1].Id == "2032825238332522497";
            result["mijiaRefreshIntervalsOk"] = MijiaSettings.NormalizeRefreshMinutes(5) == 5
                && MijiaSettings.NormalizeRefreshMinutes(7) == 15
                && MijiaSettings.NormalizeRefreshMinutes(15) == 15
                && MijiaSettings.NormalizeRefreshMinutes(30) == 30
                && MijiaSettings.NormalizeRefreshMinutes(60) == 60
                && MijiaSettings.RefreshIntervalMinutes.Length == 4;
            result["mijiaConfigV8RoundTripOk"] = CheckConfigRoundTrip();
            result["mijiaConfigV8Details"] = _configCheckDetails;
            result["mijiaCliContractOk"] = CheckCliContract();
            result["mijiaShortcutAvailabilityOk"] = CheckShortcutAvailability();
            result["mijiaCommandGateOk"] = CheckCommandGate();
            result["mijiaEnvironmentValidationOk"] = CheckEnvironmentValidation();
            result["mijiaManualRefreshEntryOk"] = CheckManualRefreshEntry();
            result["mijiaRefreshSnapshotOk"] = CheckRefreshSnapshot();
            bool loginSerialization = CheckLoginCommandSerialization();
            result["mijiaLoginCommandSerializationOk"] = loginSerialization;
            result["mijiaLoginVerificationGateOk"] = loginSerialization;
            result["mijiaHardwareBusyFeedbackOk"] = CheckHardwareBusyFeedback();
            result["mijiaDisposeInFlightOk"] = CheckDisposeInFlight();
            result["mijiaLoginLateSubscriptionOk"] = CheckLateLoginSubscription();
            result["mijiaActionDispatchOk"] = CheckActionDispatch();
            result["mijiaQrOutputContractOk"] = CheckQrOutputContract();
            result["mijiaSettingsUiContractOk"] = CheckSettingsUiContract();
            result["mijiaExistingQrPanelOk"] = CheckExistingQrPanel();
            result["mijiaAutoRefreshToggleOk"] = CheckAutoRefreshToggle();
            result["pcMetricsProtocolTextV3Ok"] = CheckProtocolTextV3();
            return result;
        }

        private static bool CheckActionDispatch()
        {
            var host = new RecordingHostService();
            ActionDispatcher.Execute(new ActionSpec("mijia3", ""), host);
            return host.LastMijiaShortcut == 2;
        }

        private static bool CheckQrOutputContract()
        {
            EventInfo qrEvent = typeof(MijiaLoginSession).GetEvent("QrCodeUrlReceived");
            MethodInfo extractor = typeof(MijiaLoginSession).GetMethod(
                "ExtractFirstHttpsUrl", BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic);
            if (qrEvent == null || extractor == null) return false;
            string output = "准备登录\n请扫描 https://qr.example/first\nhttps://qr.example/second";
            string url = extractor.Invoke(null, new object[] { output }) as string;
            return url == "https://qr.example/first";
        }

        private static bool CheckSettingsUiContract()
        {
            bool contractOk = false;
            Exception failure = null;
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    Type panelType = typeof(MijiaSelfChecks).Assembly.GetType(
                        "CodexToolsHost.UI.MijiaSettingsPanel", false);
                    if (panelType == null) return;

                    MijiaSettings settings = NewSettings();
                    var runner = new RecordingRunner(Fixture);
                    using (var service = new MijiaService(settings, runner))
                    using (Control panel = Activator.CreateInstance(panelType,
                        new object[] { settings, service }) as Control)
                    {
                        if (panel == null || CountExecuteButtons(panel) != 8) return;
                    }

                    var unavailableSettings = NewSettings();
                    unavailableSettings.AuthPath = Path.Combine(Path.GetTempPath(),
                        "missing-mijia-auth-" + Guid.NewGuid().ToString("N") + ".json");
                    unavailableSettings.Shortcuts[0] = new MijiaShortcutConfig("旧场景",
                        "999", "已删除场景");
                    bool unavailableEnvironmentContract;
                    using (var unavailableService = new MijiaService(unavailableSettings, runner))
                    using (Control unavailablePanel = Activator.CreateInstance(panelType,
                        new object[] { unavailableSettings, unavailableService }) as Control)
                    {
                        MijiaSettingsPanel typedPanel = unavailablePanel as MijiaSettingsPanel;
                        if (typedPanel != null) typedPanel.ApplySettings();
                        FieldInfo loginButtonField = panelType.GetField("_loginButton",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Button storedLoginButton = loginButtonField == null ? null
                            : loginButtonField.GetValue(unavailablePanel) as Button;
                        unavailableEnvironmentContract = unavailablePanel != null
                            && !FindTaggedButton(unavailablePanel, "mijia-execute").Enabled
                            && !FindTaggedButton(unavailablePanel, "mijia-refresh-scenes").Enabled
                            && FindTaggedButton(unavailablePanel, "mijia-login").Enabled
                            && ReferenceEquals(storedLoginButton,
                                FindTaggedButton(unavailablePanel, "mijia-login"))
                            && unavailableSettings.Shortcuts[0].SceneId == "999";
                    }
                    if (!unavailableEnvironmentContract) return;

                    AppConfig config = AppConfig.CreateDefault();
                    using (var link = new MockDeviceLink { EmitInfoOnConnect = false })
                    using (var bridge = new BridgeService(config, link,
                        new FakeCodexSource(), new FakeDeepSeekSource()))
                    using (var form = new SettingsForm(config, bridge))
                    {
                        FieldInfo tabsField = typeof(SettingsForm).GetField("_tabs",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        TabControl tabs = tabsField == null ? null
                            : tabsField.GetValue(form) as TabControl;
                        if (tabs == null) return;
                        foreach (TabPage tab in tabs.TabPages[3].Controls
                            .OfType<TabControl>().SelectMany(section => section.TabPages.Cast<TabPage>()))
                        {
                            if (tab.Text == "米家控制")
                            {
                                contractOk = CountExecuteButtons(tab) == 8;
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool completed = thread.Join(5000);
            return completed && failure == null && contractOk;
        }

        private static int CountExecuteButtons(Control root)
        {
            int count = root is Button && Convert.ToString(root.Tag) == "mijia-execute"
                ? 1 : 0;
            foreach (Control child in root.Controls) count += CountExecuteButtons(child);
            return count;
        }

        private static Button FindTaggedButton(Control root, string tag)
        {
            return FindTaggedControl(root, tag) as Button;
        }

        private static Control FindTaggedControl(Control root, string tag)
        {
            if (root != null && Convert.ToString(root.Tag) == tag) return root;
            foreach (Control child in root.Controls)
            {
                Control found = FindTaggedControl(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        private static bool CheckConfigRoundTrip()
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexToolsHostMijiaSelfCheck-"
                + Guid.NewGuid().ToString("N"));
            try
            {
                AppConfig config = AppConfig.Load(root, root);
                bool defaultSlots = HasDefaultSlots(config.Mijia.Shortcuts);
                config.Mijia.ExecutablePath = "mijiaAPI.exe";
                config.Mijia.AuthPath = "auth.json";
                config.Mijia.AutoRefresh = false;
                config.Mijia.RefreshMinutes = 30;
                config.Mijia.Shortcuts = new[]
                {
                    new MijiaShortcutConfig("回家", "2032825238332522497", "回家")
                };
                config.Save();
                AppConfig reloaded = AppConfig.Load(root, root);
                string path = Path.Combine(root, AppConfig.DefaultFileName);
                File.WriteAllText(path, "{\"configVersion\":8,\"mijia\":{\"shortcuts\":[{\"name\":\"旧槽\",\"sceneId\":\"1\",\"sceneName\":\"旧场景\"}]}}");
                AppConfig migrated = AppConfig.Load(root, root);
                bool version = reloaded.ConfigVersion == 9;
                bool fields = reloaded.Mijia.ExecutablePath == "mijiaAPI.exe"
                    && reloaded.Mijia.AuthPath == "auth.json" && !reloaded.Mijia.AutoRefresh
                    && reloaded.Mijia.RefreshMinutes == 30;
                bool savedSlots = defaultSlots && HasEightSlots(reloaded.Mijia.Shortcuts)
                    && reloaded.Mijia.Shortcuts[0].SceneId == "2032825238332522497";
                bool migratedSlots = HasEightSlots(migrated.Mijia.Shortcuts)
                    && migrated.Mijia.Shortcuts[0].Name == "旧槽"
                    && migrated.Mijia.Shortcuts[7].Name == "快捷8";
                _configCheckDetails = "version=" + version + ",fields=" + fields
                    + ",savedSlots=" + savedSlots + ",migratedSlots=" + migratedSlots;
                return version && fields && savedSlots && migratedSlots;
            }
            catch (Exception)
            {
                _configCheckDetails = "exception";
                return false;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception) { }
            }
        }

        private static bool CheckCliContract()
        {
            MijiaSettings settings = NewSettings();
            settings.AuthPath = "auth-path-for-selfcheck";
            settings.Shortcuts[0] = new MijiaShortcutConfig("回家",
                "2032825238332522497", "回家");
            var runner = new RecordingRunner(Fixture);
            var service = new MijiaService(settings, runner);
            try
            {
                bool refresh = service.RefreshScenesAsync().GetAwaiter().GetResult();
                bool execute = service.ExecuteShortcutAsync(0).GetAwaiter().GetResult();
                string expectedList = "\"--list_scenes\" \"-p\" \"auth-path-for-selfcheck\"";
                string expectedRun = "\"--run_scene\" \"2032825238332522497\" \"-p\" \"auth-path-for-selfcheck\"";
                bool withAuth = refresh && execute && runner.CallCount == 2
                    && runner.Arguments[0] == expectedList && runner.Timeouts[0] == 15000
                    && runner.Arguments[1] == expectedRun && runner.Timeouts[1] == 15000;

                settings.AuthPath = "";
                bool standardAuth = service.RefreshScenesAsync().GetAwaiter().GetResult()
                    && runner.Arguments[2] == "\"--list_scenes\"";
                int callsBeforeInvalid = runner.CallCount;
                settings.Shortcuts[0].SceneId = "not-a-number";
                bool invalidRejected = !service.ExecuteShortcutAsync(0).GetAwaiter().GetResult()
                    && runner.CallCount == callsBeforeInvalid;
                return withAuth && standardAuth && invalidRejected;
            }
            finally
            {
                service.Dispose();
            }
        }

        private static bool CheckShortcutAvailability()
        {
            MijiaSettings settings = NewSettings();
            settings.Shortcuts[0] = new MijiaShortcutConfig("有效场景",
                "2032112121379033088", "旧名称");
            settings.Shortcuts[1] = new MijiaShortcutConfig("缺失场景", "999", "已删除");
            var runner = new RecordingRunner(Fixture, RestoredFixture);
            var service = new MijiaService(settings, runner);
            try
            {
                bool firstRefresh = service.RefreshScenesAsync().GetAwaiter().GetResult();
                bool missingRetained = firstRefresh && service.IsShortcutAvailable(0)
                    && !service.IsShortcutAvailable(1)
                    && settings.Shortcuts[1].SceneId == "999"
                    && settings.Shortcuts[1].SceneName == "已删除";
                bool secondRefresh = service.RefreshScenesAsync().GetAwaiter().GetResult();
                return missingRetained && secondRefresh && service.IsShortcutAvailable(1)
                    && settings.Shortcuts[1].SceneName == "已恢复";
            }
            finally
            {
                service.Dispose();
            }
        }

        private static bool CheckCommandGate()
        {
            MijiaSettings settings = NewSettings();
            settings.Shortcuts[0] = new MijiaShortcutConfig("回家",
                "2032825238332522497", "回家");
            var runner = new RecordingRunner(Fixture);
            runner.BlockNextCall();
            var service = new MijiaService(settings, runner);
            try
            {
                Task<bool> first = service.RefreshScenesAsync();
                bool secondRejected = !service.ExecuteShortcutAsync(0).GetAwaiter().GetResult()
                    && runner.CallCount == 1;
                runner.CompleteBlockedCall();
                return first.GetAwaiter().GetResult() && secondRejected
                    && string.IsNullOrEmpty(service.LastError);
            }
            finally
            {
                service.Dispose();
            }
        }

        private static bool CheckEnvironmentValidation()
        {
            MijiaSettings settings = NewSettings();
            settings.AuthPath = "";
            settings.Shortcuts[0] = new MijiaShortcutConfig("回家",
                "2032825238332522497", "回家");
            var runner = new RecordingRunner(Fixture);
            using (var service = new MijiaService(settings, runner))
            {
                bool initiallyUnverified = !service.EnvironmentReady
                    && !service.IsShortcutAvailable(0);
                bool verified = service.RefreshScenesAsync().GetAwaiter().GetResult()
                    && service.EnvironmentReady && service.IsShortcutAvailable(0);
                settings.AuthPath = "different-auth-path";
                service.ApplySettings();
                bool changedPathRevoked = !service.EnvironmentReady
                    && !service.IsShortcutAvailable(0)
                    && settings.Shortcuts[0].SceneId == "2032825238332522497";
                runner.EnqueueFailure();
                bool failedRefreshRevoked = !service.RefreshScenesAsync().GetAwaiter().GetResult()
                    && !service.EnvironmentReady
                    && settings.Shortcuts[0].SceneId == "2032825238332522497";
                return initiallyUnverified && verified && changedPathRevoked
                    && failedRefreshRevoked;
            }
        }

        private static bool CheckManualRefreshEntry()
        {
            bool entryAvailable = false;
            Exception failure = null;
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    Type panelType = typeof(MijiaSelfChecks).Assembly.GetType(
                        "CodexToolsHost.UI.MijiaSettingsPanel", false);
                    if (panelType == null) return;
                    MijiaSettings settings = NewSettings();
                    settings.AuthPath = "";
                    using (var service = new MijiaService(settings,
                        new RecordingRunner(Fixture)))
                    using (Control panel = Activator.CreateInstance(panelType,
                        new object[] { settings, service }) as Control)
                    {
                        if (panel == null || service.EnvironmentReady) return;
                        entryAvailable = FindTaggedButton(panel, "mijia-refresh-scenes").Enabled
                            && !FindTaggedButton(panel, "mijia-execute").Enabled;
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(5000) && failure == null && entryAvailable;
        }

        private static bool CheckRefreshSnapshot()
        {
            MijiaSettings settings = NewSettings();
            settings.AuthPath = "auth-A";
            var runner = new RecordingRunner(Fixture);
            runner.BlockNextCall();
            using (var service = new MijiaService(settings, runner))
            {
                Task<bool> refresh = service.RefreshScenesAsync();
                if (runner.CallCount != 1) return false;
                settings.AuthPath = "auth-B";
                service.ApplySettings();
                runner.CompleteBlockedCall();
                bool accepted = refresh.GetAwaiter().GetResult();
                return !accepted && !service.EnvironmentReady && service.Scenes.Length == 0
                    && runner.Arguments[0].IndexOf(MijiaCliRunner.QuoteArgument("auth-A"),
                        StringComparison.Ordinal) >= 0
                    && service.LastError != null
                    && service.LastError.IndexOf("配置已变化", StringComparison.Ordinal) >= 0;
            }
        }

        private static bool CheckLoginCommandSerialization()
        {
            MijiaSettings settings = NewSettings();
            var runner = new RecordingRunner(Fixture, Fixture);
            runner.BlockNextCall();
            using (var service = new MijiaService(settings, runner))
            {
                Task<bool> occupiedRefresh = service.RefreshScenesAsync();
                if (runner.CallCount != 1) return false;
                service.StartLogin();
                bool rejectedWhileCommandBusy = runner.LoginStartCount == 0;
                runner.CompleteBlockedCall();
                bool occupiedCompleted = occupiedRefresh.GetAwaiter().GetResult();
                if (!rejectedWhileCommandBusy)
                {
                    service.CancelLogin();
                    return false;
                }

                service.StartLogin();
                MijiaLoginSession firstLogin = runner.LastLogin;
                bool firstStarted = runner.LoginStartCount == 1 && firstLogin != null
                    && service.LoginInProgress;
                int callsBeforeBusyAttempts = runner.CallCount;
                bool refreshRejected = !service.RefreshScenesAsync().GetAwaiter().GetResult();
                bool executeRejected = !service.ExecuteShortcutAsync(0).GetAwaiter().GetResult();
                bool noRunnerOverlap = runner.CallCount == callsBeforeBusyAttempts;

                service.StartLogin();
                MijiaLoginSession replacement = runner.LastLogin;
                bool replacementStarted = runner.LoginStartCount == 2
                    && replacement != null && !ReferenceEquals(firstLogin, replacement)
                    && !firstLogin.IsRunning && service.LoginInProgress;

                runner.BlockNextCall();
                replacement.Cancel();
                bool verificationRan = WaitUntil(delegate
                {
                    return runner.CallCount == callsBeforeBusyAttempts + 1;
                }, 3000);
                bool verificationState = verificationRan && service.LoginInProgress
                    && !ReadBoolProperty(service, "CanStartLogin")
                    && !ReadBoolProperty(service, "CanRefreshLogin");
                int loginStartsBeforeRejectedVerification = runner.LoginStartCount;
                service.StartLogin();
                bool loginRejectedDuringVerification = runner.LoginStartCount
                    == loginStartsBeforeRejectedVerification;
                bool refreshRejectedDuringVerification = !service.RefreshScenesAsync()
                    .GetAwaiter().GetResult();
                bool executeRejectedDuringVerification = !service.ExecuteShortcutAsync(0)
                    .GetAwaiter().GetResult();
                bool verificationStillSoleRunner = runner.CallCount
                    == callsBeforeBusyAttempts + 1;
                if (!loginRejectedDuringVerification) service.CancelLogin();
                runner.CompleteBlockedCall();
                bool verificationCompleted = WaitUntil(delegate
                {
                    return !service.LoginInProgress && !service.IsBusy;
                }, 3000);
                return occupiedCompleted && firstStarted && refreshRejected
                    && executeRejected && noRunnerOverlap && replacementStarted
                    && verificationState && loginRejectedDuringVerification
                    && refreshRejectedDuringVerification
                    && executeRejectedDuringVerification && verificationStillSoleRunner
                    && verificationCompleted && service.EnvironmentReady;
            }
        }

        private static bool CheckHardwareBusyFeedback()
        {
            MijiaSettings settings = NewSettings();
            settings.Shortcuts[0] = new MijiaShortcutConfig("回家",
                "2032825238332522497", "回家");
            var runner = new RecordingRunner(Fixture, Fixture);
            var service = new MijiaService(settings, runner);
            BridgeService bridge = null;
            MijiaService originalService = null;
            bool serviceInjected = false;
            try
            {
                if (!service.RefreshScenesAsync().GetAwaiter().GetResult()) return false;
                runner.BlockNextCall();
                AppConfig config = AppConfig.CreateDefault();
                bridge = new BridgeService(config,
                    new MockDeviceLink { EmitInfoOnConnect = false },
                    new FakeCodexSource(), new FakeDeepSeekSource());
                originalService = bridge.Mijia;
                FieldInfo mijiaField = typeof(BridgeService).GetField("_mijia",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (mijiaField == null) return false;
                mijiaField.SetValue(bridge, service);
                serviceInjected = true;
                var balloon = new TaskCompletionSource<string>();
                bridge.Balloon += delegate(string message) { balloon.TrySetResult(message); };

                int beforeExecution = runner.CallCount;
                bridge.RunMijiaShortcut(0);
                if (!WaitUntil(delegate { return runner.CallCount == beforeExecution + 1; },
                    3000)) return false;
                bridge.RunMijiaShortcut(0);
                bool busyToast = balloon.Task.Wait(3000)
                    && balloon.Task.Result == "米家正在执行其他操作，请稍后重试";
                bool secondDidNotEnterRunner = runner.CallCount == beforeExecution + 1;
                bool firstErrorNotPolluted = string.IsNullOrEmpty(service.LastError);
                runner.CompleteBlockedCall();
                bool firstCompleted = WaitUntil(delegate { return !service.IsBusy; }, 3000)
                    && string.IsNullOrEmpty(service.LastError);
                return busyToast && secondDidNotEnterRunner && firstErrorNotPolluted
                    && firstCompleted;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (runner.HasBlockedCall) runner.CompleteBlockedCall();
                if (bridge != null) bridge.Dispose();
                else service.Dispose();
                if (originalService != null) originalService.Dispose();
                if (!serviceInjected && bridge != null) service.Dispose();
            }
        }

        private static bool CheckProtocolTextV3()
        {
            bool ok = false;
            Exception failure = null;
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    AppConfig config = AppConfig.CreateDefault();
                    using (var bridge = new BridgeService(config,
                        new MockDeviceLink { EmitInfoOnConnect = false },
                        new FakeCodexSource(), new FakeDeepSeekSource()))
                    using (var form = new SettingsForm(config, bridge))
                    {
                        ok = ContainsControlText(form, "PC_METRICS v3")
                            && ContainsControlText(form, "28 字节");
                    }
                }
                catch (Exception ex) { failure = ex; }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(5000) && failure == null && ok;
        }

        private static bool ContainsControlText(Control root, string value)
        {
            if (root != null && (root.Text ?? "").IndexOf(value,
                StringComparison.Ordinal) >= 0) return true;
            foreach (Control child in root.Controls)
                if (ContainsControlText(child, value)) return true;
            return false;
        }

        private static bool ReadBoolProperty(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            return property != null && (bool)property.GetValue(target, null);
        }

        private static bool CheckDisposeInFlight()
        {
            MijiaSettings settings = NewSettings();
            var runner = new RecordingRunner(Fixture);
            runner.BlockNextCall();
            var service = new MijiaService(settings, runner);
            Task<bool> refresh = service.RefreshScenesAsync();
            service.Dispose();
            runner.CompleteBlockedCall();
            try
            {
                refresh.GetAwaiter().GetResult();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static MijiaSettings NewSettings()
        {
            var settings = new MijiaSettings();
            settings.ExecutablePath = System.Diagnostics.Process.GetCurrentProcess()
                .MainModule.FileName;
            return settings;
        }

        private static bool CheckLateLoginSubscription()
        {
            string command = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(command)) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var process = Process.Start(new ProcessStartInfo(command, "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process.WaitForExit();
                var session = new MijiaLoginSession(process);
                int notified = 0;
                session.Exited += delegate { notified++; };
                session.Dispose();
                if (notified != 1) return false;
            }
            return true;
        }

        private static bool CheckExistingQrPanel()
        {
            bool ok = false;
            Exception failure = null;
            string imagePath = Path.GetTempFileName();
            try
            {
                using (var image = new Bitmap(1, 1)) image.Save(imagePath, ImageFormat.Png);
                Thread thread = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        MijiaSettings settings = NewSettings();
                        using (var service = new MijiaService(settings, new RecordingRunner(Fixture)))
                        {
                            MethodInfo setQr = typeof(MijiaService).GetMethod("SetQrCodeUrl",
                                BindingFlags.Instance | BindingFlags.NonPublic);
                            if (setQr == null) return;
                            setQr.Invoke(service, new object[] { new Uri(imagePath).AbsoluteUri });
                            using (var panel = new MijiaSettingsPanel(settings, service))
                            {
                                panel.CreateControl();
                                PictureBox qr = FindTaggedControl(panel, "mijia-qr-image")
                                    as PictureBox;
                                ok = qr != null && WaitUntil(delegate
                                {
                                    Application.DoEvents();
                                    return qr.Image != null;
                                }, 3000);
                            }
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                }));
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return thread.Join(5000) && failure == null && ok;
            }
            finally
            {
                try { File.Delete(imagePath); }
                catch (Exception) { }
            }
        }

        private static bool CheckAutoRefreshToggle()
        {
            bool ok = false;
            Exception failure = null;
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    MijiaSettings settings = NewSettings();
                    using (var service = new MijiaService(settings, new RecordingRunner(Fixture)))
                    {
                        if (!service.RefreshScenesAsync().GetAwaiter().GetResult()) return;
                        using (var panel = new MijiaSettingsPanel(settings, service))
                        {
                            CheckBox autoRefresh = FindTaggedControl(panel, "mijia-auto-refresh")
                                as CheckBox;
                            ComboBox minutes = FindTaggedControl(panel, "mijia-refresh-minutes")
                                as ComboBox;
                            if (autoRefresh == null || minutes == null || !minutes.Enabled) return;
                            autoRefresh.Checked = false;
                            bool disabled = !minutes.Enabled;
                            autoRefresh.Checked = true;
                            ok = disabled && minutes.Enabled;
                        }
                    }
                }
                catch (Exception ex) { failure = ex; }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(5000) && failure == null && ok;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        private static bool HasEightSlots(MijiaShortcutConfig[] shortcuts)
        {
            return shortcuts != null && shortcuts.Length == 8
                && shortcuts[0] != null && shortcuts[7] != null;
        }

        private static bool HasDefaultSlots(MijiaShortcutConfig[] shortcuts)
        {
            return HasEightSlots(shortcuts) && shortcuts[0].Name == "快捷1"
                && shortcuts[7].Name == "快捷8";
        }

        private sealed class RecordingRunner : IMijiaCliRunner
        {
            private readonly Queue<string> _outputs;
            private readonly Queue<MijiaCommandResult> _results =
                new Queue<MijiaCommandResult>();
            private TaskCompletionSource<MijiaCommandResult> _blockedCall;

            public RecordingRunner(params string[] outputs)
            {
                _outputs = new Queue<string>(outputs);
                Arguments = new List<string>();
                Timeouts = new List<int>();
            }

            public List<string> Arguments { get; private set; }
            public List<int> Timeouts { get; private set; }
            public int CallCount { get { return Arguments.Count; } }
            public int LoginStartCount { get; private set; }
            public MijiaLoginSession LastLogin { get; private set; }
            public bool HasBlockedCall { get { return _blockedCall != null; } }

            public void BlockNextCall()
            {
                _blockedCall = new TaskCompletionSource<MijiaCommandResult>();
            }

            public void CompleteBlockedCall()
            {
                _blockedCall.SetResult(NextResult());
                _blockedCall = null;
            }

            public void EnqueueFailure()
            {
                _results.Enqueue(new MijiaCommandResult(1, "", "failure", false));
            }

            public Task<MijiaCommandResult> RunAsync(string exe, string arguments, int timeoutMs)
            {
                Arguments.Add(arguments);
                Timeouts.Add(timeoutMs);
                if (_blockedCall != null) return _blockedCall.Task;
                return Task.FromResult(NextResult());
            }

            public MijiaLoginSession StartLogin(string exe, string authPath)
            {
                string command = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrEmpty(command)) throw new InvalidOperationException();
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(command, "/c ping 127.0.0.1 -n 30 > nul")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                LoginStartCount++;
                LastLogin = new MijiaLoginSession(process, true);
                return LastLogin;
            }

            private MijiaCommandResult NextResult()
            {
                if (_results.Count > 0) return _results.Dequeue();
                string output = _outputs.Count == 0 ? Fixture : _outputs.Dequeue();
                return new MijiaCommandResult(0, output, "", false);
            }
        }

        private sealed class RecordingHostService : IHostService
        {
            public RecordingHostService() { LastMijiaShortcut = -1; }
            public int LastMijiaShortcut { get; private set; }

            public void CycleOledPage() { }
            public void SetRgbMode(string mode) { }
            public void RefreshQuota() { }
            public void ShowSettings() { }
            public void RestartChatGpt() { }
            public void Toast(string message) { }
            public void RunMijiaShortcut(int index) { LastMijiaShortcut = index; }
        }
    }
}
