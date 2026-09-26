using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using CodexToolsHost.Actions;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Mijia;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.Tests;
using CodexToolsHost.UI;

namespace CodexToolsHost
{
    internal static class Program
    {
        public static Form MainFormHandle;

        [STAThread]
        private static int Main(string[] args)
        {
            string probeOutput = ReadProbeOutput(args);
            if (probeOutput != null) return RunProbe(probeOutput);
            string selfCheckOutput = ReadArg(args, "--selfcheck-output");
            if (selfCheckOutput != null) return RunSelfCheck(selfCheckOutput);
            string uiBatchCheckOutput = ReadArg(args, "--ui-batch-selfcheck-output");
            if (uiBatchCheckOutput != null) return UiBatchOneSelfChecks.Run(uiBatchCheckOutput);
            string phaseTwoOutput = ReadArg(args, "--phase-two-selfcheck-output");
            if (phaseTwoOutput != null) return PhaseTwoSelfChecks.Run(phaseTwoOutput);
            string chatGptRestartSelfCheckOutput = ReadArg(args,
                "--chatgpt-restart-selfcheck-output");
            if (chatGptRestartSelfCheckOutput != null)
                return RunChatGptRestartSelfCheck(chatGptRestartSelfCheckOutput);
            string mockTestOutput = ReadArg(args, "--mock-test-output");
            if (mockTestOutput != null) return RunMockTest(mockTestOutput);
            string deviceProbeOutput = ReadArg(args, "--device-probe-output");
            if (deviceProbeOutput != null)
                return RunDeviceProbe(deviceProbeOutput, ReadArg(args, "--device-port"));
            string deepSeekProbeOutput = ReadArg(args, "--deepseek-probe-output");
            if (deepSeekProbeOutput != null) return RunDeepSeekProbe(deepSeekProbeOutput);
            string pcMetricsOutput = ReadArg(args, "--pc-metrics-output");
            if (pcMetricsOutput != null) return RunPcMetricsProbe(pcMetricsOutput);
            string acpiFallbackOutput = ReadArg(args, "--acpi-fallback-test-output");
            if (acpiFallbackOutput != null) return RunAcpiFallbackTest(acpiFallbackOutput);
            string embeddedDependenciesOutput = ReadArg(args,
                "--embedded-deps-test-output");
            if (embeddedDependenciesOutput != null)
                return RunEmbeddedDependenciesTest(embeddedDependenciesOutput,
                    ReadArg(args, "--dependency-cache-root"));

            bool owned;
            using (Mutex mutex = new Mutex(true, "CodexToolsHost_SingleInstance", out owned))
            {
                if (!owned) return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                AppConfig config = AppConfig.Load(exeDir, localAppData);
                if (!File.Exists(config.ConfigPath) || config.NeedsSave) config.Save();

                MainFormHandle = new Form
                {
                    ShowInTaskbar = false,
                    Opacity = 0,
                    WindowState = FormWindowState.Minimized
                };
                MainFormHandle.CreateControl();
                /* 强制立即创建句柄：否则串口线程触发 UI 事件时 InvokeRequired 不可靠 */
                IntPtr forceHandle = MainFormHandle.Handle;
                GC.KeepAlive(forceHandle);

                using (BridgeService bridge = new BridgeService(config))
                using (TrayApp app = new TrayApp(config, bridge))
                {
                    Application.Run(app);
                }
            }
            return 0;
        }

        private static string ReadProbeOutput(string[] args)
        {
            return ReadArg(args, "--probe-output");
        }

        private static string ReadArg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static int RunSelfCheck(string outputPath)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 0;
            try
            {
                /* CRC8 已知向量: poly 0x07, "123456789" -> 0xF4 */
                byte[] vector = Encoding.ASCII.GetBytes("123456789");
                byte crc = FrameCodec.Crc8(vector, 0, vector.Length);
                result["crcExpected"] = "F4";
                result["crcActual"] = crc.ToString("X2");
                result["crcOk"] = crc == 0xF4;
                if (crc != 0xF4) exitCode = 2;

                /* 帧编解码往返 */
                byte[] payload = { 1, 2, 3, 4, 0x7F };
                byte[] frame = FrameCodec.Encode(FrameCodec.MsgPing, payload);
                byte[] buffer = new byte[frame.Length + 3];
                buffer[0] = 0x11; buffer[1] = 0x22; buffer[2] = 0x33; /* 干扰前缀 */
                Array.Copy(frame, 0, buffer, 3, frame.Length);
                int offset = 0;
                byte type;
                byte[] decoded;
                bool ok = FrameCodec.TryExtract(buffer, ref offset, out type, out decoded);
                result["roundtripOk"] = ok && type == FrameCodec.MsgPing
                    && decoded.Length == payload.Length;
                if (!ok || type != FrameCodec.MsgPing || decoded.Length != payload.Length) exitCode = 3;

                /* 配置保存/读取往返 */
                string dir = Path.Combine(Path.GetTempPath(), "CodexToolsHostSelfCheck");
                Directory.CreateDirectory(dir);
                AppConfig cfg = AppConfig.Load(dir, dir);
                cfg.SerialPort = "auto";
                cfg.DeepSeekApiKey = "sk-test";
                cfg.ApiBalanceProvider = "openrouter";
                cfg.DefaultOledPage = 2;
                cfg.Save();
                AppConfig reloaded = AppConfig.Load(dir, dir);
                result["configRoundtripOk"] = reloaded.SerialPort == "auto"
                    && reloaded.DeepSeekApiKey == "sk-test"
                    && reloaded.ApiBalanceProvider == "openrouter"
                    && reloaded.ConfigVersion == 11
                    && reloaded.DefaultOledPage == 2;
                if (reloaded.SerialPort != "auto" || reloaded.DeepSeekApiKey != "sk-test"
                    || reloaded.ApiBalanceProvider != "openrouter"
                    || reloaded.ConfigVersion != 11 || reloaded.DefaultOledPage != 2) exitCode = 4;

                /* clean release 只含 EXE 时，用户配置应落在 LocalAppData。 */
                bool releaseUsesLocalConfig = CheckCleanReleaseConfigPath();
                result["releaseUsesLocalConfig"] = releaseUsesLocalConfig;
                if (!releaseUsesLocalConfig) exitCode = 4;

                bool serialPortSelectionOk = SerialPortSelection.Normalize(" com07 ") == "COM7"
                    && SerialPortSelection.Normalize(" AUTO ") == SerialPortSelection.Auto
                    && SerialPortSelection.Normalize("") == SerialPortSelection.Auto;
                string[] orderedPorts = SerialPortSelection.OrderCandidates(
                    new[] { "COM10", "com5", "COM7", "COM4", "COM5" }, "com7");
                bool serialCandidateOrderingOk = orderedPorts.Length == 4
                    && orderedPorts[0] == "COM7"
                    && orderedPorts[1] == "COM4"
                    && orderedPorts[2] == "COM5"
                    && orderedPorts[3] == "COM10";
                string matchedPort;
                bool serialManualMatchOk = SerialPortSelection.TryFindPort(
                    new[] { "COM1", "COM7" }, " com7 ", out matchedPort)
                    && matchedPort == "COM7"
                    && !SerialPortSelection.TryFindPort(
                        new[] { "COM1", "COM7" }, "COM99", out matchedPort);
                byte[] expectedPong = { 0x00, 0x00, 0x00, 0x01 };
                bool serialPongMatchOk = SerialLink.IsMatchingPong(
                    expectedPong, (byte[])expectedPong.Clone())
                    && !SerialLink.IsMatchingPong(
                        expectedPong, new byte[] { 0x00, 0x00, 0x00, 0x02 })
                    && !SerialLink.IsMatchingPong(
                        expectedPong, new byte[] { 0x00, 0x00, 0x01 });
                result["serialPortSelectionOk"] = serialPortSelectionOk;
                result["serialCandidateOrderingOk"] = serialCandidateOrderingOk;
                result["serialManualMatchOk"] = serialManualMatchOk;
                result["serialPongMatchOk"] = serialPongMatchOk;
                if (!serialPortSelectionOk || !serialCandidateOrderingOk
                    || !serialManualMatchOk || !serialPongMatchOk) exitCode = 4;

                Dictionary<string, object> reliability = HostReliabilitySelfChecks.Run();
                foreach (KeyValuePair<string, object> item in reliability)
                {
                    result[item.Key] = item.Value;
                    if (item.Value is bool && !(bool)item.Value) exitCode = 4;
                }

                /* v3 页面迁移：旧动画/PC 合并到新 PC MON，旧自动页迁移到 2。 */
                string migrationDir = Path.Combine(Path.GetTempPath(), "CodexToolsHostMigrationSelfCheck");
                Directory.CreateDirectory(migrationDir);
                string migrationPath = Path.Combine(migrationDir, AppConfig.DefaultFileName);
                File.WriteAllText(migrationPath, "{\"configVersion\":3,\"defaultOledPage\":2}", new UTF8Encoding(false));
                AppConfig migratedPc = AppConfig.Load(migrationDir, migrationDir);
                File.WriteAllText(migrationPath, "{\"configVersion\":3,\"defaultOledPage\":3}", new UTF8Encoding(false));
                AppConfig migratedAuto = AppConfig.Load(migrationDir, migrationDir);
                bool pageMigrationOk = migratedPc.ConfigVersion == 11 && migratedPc.DefaultOledPage == 1
                    && migratedAuto.ConfigVersion == 11 && migratedAuto.DefaultOledPage == 2;
                result["pageMigrationOk"] = pageMigrationOk;
                if (!pageMigrationOk) exitCode = 4;

                /* 状态终态必须覆盖残留活动计数；纯元数据事件不得把 DONE 改回 RUN。 */
                var processEvent = typeof(DesktopLogStatusMonitor).GetMethod("ProcessEvent",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var staleCounterMonitor = new DesktopLogStatusMonitor();
                processEvent.Invoke(staleCounterMonitor, new object[] { "app-server event: item/started targeted_connections=1" });
                processEvent.Invoke(staleCounterMonitor, new object[] { "app-server event: turn/completed targeted_connections=1" });
                bool terminalOverridesCounter = staleCounterMonitor.State == DesktopLogStatusMonitor.CodexStateComplete;
                var metadataMonitor = new DesktopLogStatusMonitor();
                processEvent.Invoke(metadataMonitor, new object[] { "app-server event: item/started targeted_connections=1" });
                processEvent.Invoke(metadataMonitor, new object[] { "app-server event: item/completed targeted_connections=1" });
                processEvent.Invoke(metadataMonitor, new object[] { "app-server event: thread/status/changed targeted_connections=0" });
                bool metadataIgnored = metadataMonitor.State == DesktopLogStatusMonitor.CodexStateComplete;
                result["statusTerminalOverrideOk"] = terminalOverridesCounter;
                result["statusMetadataIgnoredOk"] = metadataIgnored;
                var sourceFilterMonitor = new DesktopLogStatusMonitor();
                processEvent.Invoke(sourceFilterMonitor, new object[] {
                    "tool output contains app-server event: item/started but is not an outgoing event"
                });
                bool statusSourceFilterOk = sourceFilterMonitor.State == DesktopLogStatusMonitor.CodexStateOffline;
                var timeoutMonitor = new DesktopLogStatusMonitor();
                processEvent.Invoke(timeoutMonitor, new object[] { "app-server event: item/started targeted_connections=1" });
                var lastEventField = typeof(DesktopLogStatusMonitor).GetField("_lastEventUtc",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var applyTimeout = typeof(DesktopLogStatusMonitor).GetMethod("ApplyTimeout",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                lastEventField.SetValue(timeoutMonitor, DateTime.UtcNow.AddMinutes(-2));
                processEvent.Invoke(timeoutMonitor, new object[] {
                    "app-server event: account/rateLimits/updated targeted_connections=1"
                });
                applyTimeout.Invoke(timeoutMonitor, null);
                bool metadataDoesNotExtendRun = timeoutMonitor.State == DesktopLogStatusMonitor.CodexStateIdle;
                result["statusSourceFilterOk"] = statusSourceFilterOk;
                result["statusMetadataTimeoutOk"] = metadataDoesNotExtendRun;
                if (!terminalOverridesCounter || !metadataIgnored
                    || !statusSourceFilterOk || !metadataDoesNotExtendRun) exitCode = 4;

                /* 新安装默认值应复现当前已联调配置；API 密钥绝不固化。 */
                AppConfig defaults = AppConfig.CreateDefault();
                bool defaultsMatchCurrent = defaults.SerialPort == "COM7"
                    && defaults.CodexRefreshSeconds == 30
                    && defaults.DeepSeekRefreshSeconds == 300
                    && defaults.ApiBalanceProvider == "deepseek"
                    && defaults.DefaultOledPage == 2
                    && defaults.Rgb.Mode == "status"
                    && defaults.Rgb.Count == 3
                    && defaults.Rgb.Brightness == 60
                    && defaults.Rgb.PeriodMs == 1500
                    && defaults.Rgb.Color1 == "#FF8000"
                    && defaults.Rgb.StatusRunning == "#0080FF"
                    && defaults.Rgb.StatusComplete == "#0080FF"
                    && defaults.Bindings["btn4_click"].Action == "hotkey"
                    && defaults.Bindings["btn4_click"].Param == "Alt+D"
                    && defaults.Bindings["btn5_click"].Action == "rgbWave"
                    && defaults.EncoderRotate.Action == "cycleOled"
                    && defaults.EncoderRotate.Param == "1"
                    && string.IsNullOrEmpty(defaults.DeepSeekApiKey);
                result["defaultsMatchCurrent"] = defaultsMatchCurrent;
                if (!defaultsMatchCurrent) exitCode = 4;

                /* 多厂商余额解析桩（官方文档示例响应） */
                var parseSamples = new Dictionary<string, KeyValuePair<string, bool>>();
                parseSamples["deepseek"] = new KeyValuePair<string, bool>(
                    "{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"110.00\",\"granted_balance\":\"10.00\"}]}",
                    false);
                parseSamples["siliconflow"] = new KeyValuePair<string, bool>(
                    "{\"code\":20000,\"message\":\"OK\",\"status\":true,\"data\":{\"balance\":\"0.88\",\"chargeBalance\":\"88.00\",\"totalBalance\":\"88.88\"}}",
                    false);
                parseSamples["openrouter_limited"] = new KeyValuePair<string, bool>(
                    "{\"data\":{\"label\":\"x\",\"limit\":100,\"limit_remaining\":12.34,\"usage\":87.66}}",
                    false);
                parseSamples["openrouter_unlimited"] = new KeyValuePair<string, bool>(
                    "{\"data\":{\"label\":\"x\",\"limit\":null,\"limit_remaining\":null,\"usage\":1.2}}",
                    true);
                parseSamples["custom"] = new KeyValuePair<string, bool>(
                    "{\"data\":{\"available_balance\":\"5.50\",\"currency\":\"CNY\"}}",
                    false);
                var parseResult = new Dictionary<string, object>();
                bool parseOk = true;
                foreach (var sample in parseSamples)
                {
                    string provider = sample.Key;
                    bool unlimitedExpected = sample.Value.Value;
                    var one = new Dictionary<string, object>();
                    try
                    {
                        bool available; long cents; string currency; bool unlimited;
                        ApiBalanceProvider.ParseBalanceJson(provider, sample.Value.Key,
                            out available, out cents, out currency, out unlimited);
                        one["available"] = available;
                        one["cents"] = cents;
                        one["currency"] = currency;
                        one["unlimited"] = unlimited;
                        bool parseItemOk = available && unlimited == unlimitedExpected
                            && (unlimited || cents > 0) && !string.IsNullOrEmpty(currency);
                        one["ok"] = parseItemOk;
                        if (!parseItemOk) parseOk = false;
                    }
                    catch (Exception ex)
                    {
                        one["ok"] = false;
                        one["error"] = ex.Message;
                        parseOk = false;
                    }
                    parseResult[provider] = one;
                }
                try
                {
                    bool available; long cents; string currency; bool unlimited;
                    ApiBalanceProvider.ParseBalanceJson("custom", "{\"hello\":1}",
                        out available, out cents, out currency, out unlimited);
                    parseOk = false;
                    parseResult["bad_custom"] = new Dictionary<string, object> { { "ok", false }, { "error", "应抛出异常但未抛出" } };
                }
                catch (InvalidDataException)
                {
                    parseResult["bad_custom"] = new Dictionary<string, object> { { "ok", true } };
                }
                result["apiParse"] = parseResult;
                if (!parseOk) exitCode = 8;

                Dictionary<string, object> openCodeGoChecks = OpenCodeGoSelfChecks.Run();
                foreach (KeyValuePair<string, object> check in openCodeGoChecks)
                {
                    result[check.Key] = check.Value;
                    if (check.Value is bool && !(bool)check.Value) exitCode = 9;
                }

                var quotaHudChecks = QuotaHudSelfChecks.Run(dir);
                foreach (var check in quotaHudChecks)
                    result[check.Key] = check.Value;
                if (!(bool)quotaHudChecks["quotaHudDataContractOk"]) exitCode = 12;
                if (!(bool)quotaHudChecks["quotaHudDisplayConfigContractOk"]) exitCode = 12;
                if (!(bool)quotaHudChecks["quotaHudLayoutOk"]) exitCode = 13;
                if (!(bool)quotaHudChecks["quotaHudTwoLineHeightOk"]) exitCode = 13;
                if (!(bool)quotaHudChecks["quotaHudGoTextFitOk"]) exitCode = 13;
                if (!(bool)quotaHudChecks["quotaHudWindowContractOk"]
                    || !(bool)quotaHudChecks["apiBalanceWindowContractOk"]) exitCode = 14;
                if (!(bool)quotaHudChecks["trayQuotaHudContractOk"]) exitCode = 15;
                bool quotaHudContractOk = (bool)quotaHudChecks["quotaHudDataContractOk"]
                    && (bool)quotaHudChecks["quotaHudDisplayConfigContractOk"]
                    && (bool)quotaHudChecks["quotaHudLayoutOk"]
                    && (bool)quotaHudChecks["quotaHudTwoLineHeightOk"]
                    && (bool)quotaHudChecks["quotaHudGoTextFitOk"]
                    && (bool)quotaHudChecks["quotaHudWindowContractOk"]
                    && (bool)quotaHudChecks["apiBalanceWindowContractOk"]
                    && (bool)quotaHudChecks["quotaHudRefreshClickContractOk"]
                    && (bool)quotaHudChecks["trayQuotaHudContractOk"];
                result["quotaHudContractOk"] = quotaHudContractOk;
                if (!quotaHudContractOk) exitCode = 15;

                Dictionary<string, object> mijiaChecks = MijiaSelfChecks.Run();
                foreach (KeyValuePair<string, object> check in mijiaChecks)
                {
                    result[check.Key] = check.Value;
                    if (check.Value is bool && !(bool)check.Value) exitCode = 16;
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                exitCode = 5;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static bool CheckCleanReleaseConfigPath()
        {
            string releaseConfigDir = Path.Combine(Path.GetTempPath(),
                "CodexToolsHostReleaseConfigSelfCheck-" + Guid.NewGuid().ToString("N"));
            string releaseLocalRoot = Path.Combine(Path.GetTempPath(),
                "CodexToolsHostReleaseLocalSelfCheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                string releaseLocalDir = Path.Combine(releaseLocalRoot, "CodexToolsHost");
                Directory.CreateDirectory(releaseLocalDir);
                string localConfigPath = Path.Combine(releaseLocalDir, AppConfig.DefaultFileName);
                File.WriteAllText(localConfigPath, "{\"serialPort\":\"COM12\"}",
                    new UTF8Encoding(false));
                AppConfig releaseConfig = AppConfig.Load(releaseConfigDir, releaseLocalRoot);
                return releaseConfig.UsesFallbackPath
                    && string.Equals(releaseConfig.ConfigPath, localConfigPath,
                        StringComparison.OrdinalIgnoreCase)
                    && releaseConfig.SerialPort == "COM12";
            }
            finally
            {
                if (Directory.Exists(releaseConfigDir))
                {
                    try { Directory.Delete(releaseConfigDir, true); }
                    catch (Exception) { }
                }
                if (Directory.Exists(releaseLocalRoot))
                {
                    try { Directory.Delete(releaseLocalRoot, true); }
                    catch (Exception) { }
                }
            }
        }

        private static int RunChatGptRestartSelfCheck(string outputPath)
        {
            var result = new Dictionary<string, object>();
            result["releaseUsesLocalConfig"] = CheckCleanReleaseConfigPath();
            result["idleAllowed"] = !ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateIdle, true);
            result["completeAllowed"] = !ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateComplete, true);
            result["runningBlocked"] = ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateRunning, true);
            result["waitingBlocked"] = ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateWaiting, true);
            result["errorBlocked"] = ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateError, true);
            result["unknownRunningBlocked"] = ChatGptRestartPolicy.ShouldBlock(99, true);
            result["offlineNoProcessAllowed"] = !ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateOffline, false);
            result["offlineRunningBlocked"] = ChatGptRestartPolicy.ShouldBlock(
                ChatGptRestartPolicy.StateOffline, true);

            var idleRuntime = new FakeChatGptAppRuntime(true);
            string idleMessage = new ChatGptRestartService(idleRuntime)
                .RestartCore(ChatGptRestartPolicy.StateIdle);
            result["idleRuntimeRestarted"] = idleRuntime.CloseCalls == 1
                && idleRuntime.WaitExitCalls == 1
                && idleRuntime.TerminateCalls == 0
                && idleRuntime.LaunchCalls == 1
                && idleRuntime.WaitWindowCalls == 1
                && idleMessage.IndexOf("已重启", StringComparison.Ordinal) >= 0;

            var forceExitRuntime = new FakeChatGptAppRuntime(true)
            {
                WaitExitResult = false,
                WaitExitAfterTerminateResult = true
            };
            string forceExitMessage = new ChatGptRestartService(forceExitRuntime)
                .RestartCore(ChatGptRestartPolicy.StateIdle);
            result["idleForceExitFallback"] = forceExitRuntime.CloseCalls == 1
                && forceExitRuntime.WaitExitCalls == 2
                && forceExitRuntime.TerminateCalls == 1
                && forceExitRuntime.LaunchCalls == 1
                && forceExitMessage.IndexOf("已重启", StringComparison.Ordinal) >= 0;

            var forceFailureRuntime = new FakeChatGptAppRuntime(true)
            {
                WaitExitResult = false,
                TerminateResult = false
            };
            string forceFailureMessage = new ChatGptRestartService(forceFailureRuntime)
                .RestartCore(ChatGptRestartPolicy.StateIdle);
            result["forceExitFailureReported"] = forceFailureMessage.IndexOf("完全退出",
                    StringComparison.Ordinal) >= 0
                && forceFailureRuntime.TerminateCalls == 1
                && forceFailureRuntime.LaunchCalls == 0;

            var runningRuntime = new FakeChatGptAppRuntime(true);
            new ChatGptRestartService(runningRuntime)
                .RestartCore(ChatGptRestartPolicy.StateRunning);
            result["runningRuntimeNotTouched"] = runningRuntime.TotalCalls == 0;
            result["runningForceExitNotTouched"] = runningRuntime.TerminateCalls == 0;

            var unknownRuntime = new FakeChatGptAppRuntime(true);
            new ChatGptRestartService(unknownRuntime).RestartCore(99);
            result["unknownRuntimeNotTouched"] = unknownRuntime.TotalCalls == 0;
            result["unknownForceExitNotTouched"] = unknownRuntime.TerminateCalls == 0;

            var offlineRuntime = new FakeChatGptAppRuntime(false);
            string offlineMessage = new ChatGptRestartService(offlineRuntime)
                .RestartCore(ChatGptRestartPolicy.StateOffline);
            result["offlineRuntimeLaunched"] = offlineRuntime.CloseCalls == 0
                && offlineRuntime.WaitExitCalls == 0
                && offlineRuntime.LaunchCalls == 1
                && offlineRuntime.WaitWindowCalls == 1
                && offlineMessage.IndexOf("已重启", StringComparison.Ordinal) >= 0;

            var closeFailureRuntime = new FakeChatGptAppRuntime(true)
            {
                CloseResult = false
            };
            string closeFailureMessage = new ChatGptRestartService(closeFailureRuntime)
                .RestartCore(ChatGptRestartPolicy.StateIdle);
            result["closeFailureReported"] = closeFailureMessage.IndexOf("关闭", StringComparison.Ordinal) >= 0
                && closeFailureRuntime.LaunchCalls == 0;

            var launchFailureRuntime = new FakeChatGptAppRuntime(false)
            {
                LaunchResult = false
            };
            string launchFailureMessage = new ChatGptRestartService(launchFailureRuntime)
                .RestartCore(ChatGptRestartPolicy.StateOffline);
            result["launchFailureReported"] = launchFailureMessage.IndexOf("启动", StringComparison.Ordinal) >= 0
                && launchFailureRuntime.WaitWindowCalls == 0;

            var windowTimeoutRuntime = new FakeChatGptAppRuntime(false)
            {
                WaitWindowResult = false
            };
            string windowTimeoutMessage = new ChatGptRestartService(windowTimeoutRuntime)
                .RestartCore(ChatGptRestartPolicy.StateOffline);
            result["windowTimeoutReported"] = windowTimeoutMessage.IndexOf("窗口", StringComparison.Ordinal) >= 0
                && windowTimeoutRuntime.LaunchCalls == 1;

            var bridgeRestart = new FakeChatGptRestartService();
            AppConfig restartConfig = AppConfig.CreateDefault();
            using (MockDeviceLink restartLink = new MockDeviceLink { EmitInfoOnConnect = false })
            using (BridgeService restartBridge = new BridgeService(
                restartConfig, restartLink, new FakeCodexSource(), null,
                new FakeDeepSeekSource(),
                new PcMonitorService(new WindowsPcMetricsProvider(), 2000),
                bridgeRestart))
            {
                restartBridge.RestartChatGpt();
            }
            result["bridgeForwardedState"] = bridgeRestart.RestartCalls == 1
                && bridgeRestart.LastState == CodexStatusProvider.CodexStateRunning;

            var fakeHost = new FakeHostService();
            ActionDispatcher.Execute(new ActionSpec("restartChatgpt", ""), fakeHost);
            result["shortcutActionPresent"] = fakeHost.RestartCalls == 1;

            string configFixture = Path.Combine(Path.GetTempPath(),
                "CodexToolsHostChatGptRestartConfig-" + Guid.NewGuid().ToString("N"));
            try
            {
                AppConfig persisted = AppConfig.Load(configFixture, configFixture);
                persisted.Bindings["btn0_click"] = new ActionSpec("restartChatgpt", "");
                persisted.Save();
                AppConfig reloaded = AppConfig.Load(configFixture, configFixture);
                ActionSpec savedAction;
                result["shortcutConfigRoundtrip"] = reloaded.Bindings.TryGetValue(
                    "btn0_click", out savedAction)
                    && savedAction != null
                    && savedAction.Action == "restartChatgpt";
            }
            finally
            {
                if (Directory.Exists(configFixture))
                {
                    try { Directory.Delete(configFixture, true); }
                    catch (Exception) { }
                }
            }
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result),
                new UTF8Encoding(false));
            bool ok = true;
            foreach (object value in result.Values)
                if (value is bool && !(bool)value) ok = false;
            Environment.Exit(ok ? 0 : 1);
            return ok ? 0 : 1;
        }

        private static int RunMockTest(string outputPath)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 7;
            try
            {
                AppConfig config = AppConfig.CreateDefault();
                foreach (string key in AppConfig.BindingKeys)
                    config.Bindings[key] = new ActionSpec("none", "");

                using (MockDeviceLink mock = new MockDeviceLink
                {
                    FirmwareMajor = 0,
                    FirmwareMinor = 4,
                    EmitInfoOnConnect = false
                })
                using (BridgeService bridge = new BridgeService(
                    config, mock, new FakeCodexSource(), new FakeDeepSeekSource()))
                {
                    var statusSeen = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    mock.StatusReceived += delegate { statusSeen.TrySetResult(true); };

                    bridge.Start();
                    bool statusOk = statusSeen.Task.Wait(TimeSpan.FromSeconds(6));

                    config.SerialPort = "com5";
                    bridge.ApplyDeviceConfig();
                    bool serialPortApplyOk = mock.ConfiguredPort == "COM5";

                    mock.EmitButton(0, 3);          /* btn0 单击（绑定 none） */
                    mock.EmitButton(6, 5);          /* 编码器长按（绑定 none） */
                    mock.EmitEncoder(1);            /* 右旋一格（volume none） */
                    bridge.SetOledPage(2);          /* 固件托管的 5 秒自动轮播 */
                    System.Threading.Thread.Sleep(400);

                    result["connected"] = mock.IsConnected;
                    result["statusFrames"] = mock.StatusCount;
                    result["statusReceived"] = statusOk;
                    result["statusPayloadValid"] = mock.LastStatusValid;
                    result["rgbSet"] = mock.RgbSetCount;
                    result["oledPage"] = mock.OledPageCount;
                    result["oledAutoPage"] = mock.LastOledPage;
                    result["oledText"] = mock.OledTextCount;
                    result["cfgReq"] = mock.CfgReqCount;
                    result["pcMetrics"] = mock.PcMetricsCount;
                    result["serialPortApplyOk"] = serialPortApplyOk;

                    exitCode = (statusOk && mock.StatusCount >= 1
                                && mock.LastStatusValid
                                && mock.RgbSetCount >= 1
                                && mock.OledPageCount >= 1
                                && mock.LastOledPage == 2
                                && mock.OledTextCount >= 2
                                && mock.CfgReqCount >= 1
                                && mock.PcMetricsCount >= 1
                                && serialPortApplyOk) ? 0 : 6;
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                exitCode = 7;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static int RunDeviceProbe(string outputPath, string portArg)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 1;
            try
            {
                using (SerialLink link = new SerialLink(string.IsNullOrWhiteSpace(portArg) ? null : portArg))
                {
                    var infoList = new List<Dictionary<string, object>>();
                    int buttonEvents = 0;
                    var buttonList = new List<string>();
                    link.FrameReceived += delegate(byte type, byte[] payload)
                    {
                        if (type == FrameCodec.MsgInfo && payload != null && payload.Length >= 8)
                        {
                            var d = new Dictionary<string, object>();
                            d["fw"] = payload[0] + "." + payload[1];
                            d["buttons"] = payload[2];
                            d["encoder"] = payload[3];
                            d["oledStatus"] = payload[4];
                            d["rgbCount"] = payload[5];
                            d["model"] = Encoding.ASCII.GetString(payload, 8,
                                Math.Min(16, payload.Length - 8)).TrimEnd('\0', ' ');
                            lock (infoList) { if (infoList.Count < 4) infoList.Add(d); }
                        }
                        else if (type == FrameCodec.MsgEvtButton && payload != null && payload.Length >= 3)
                        {
                            System.Threading.Interlocked.Increment(ref buttonEvents);
                            lock (buttonList)
                            {
                                if (buttonList.Count < 8)
                                    buttonList.Add(payload[0] + ":" + payload[1]);
                            }
                        }
                    };

                    var connectedTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    link.Connected += delegate { connectedTcs.TrySetResult(true); };
                    link.Start();

                    bool connected = connectedTcs.Task.Wait(TimeSpan.FromSeconds(15));
                    result["connected"] = connected;
                    if (connected)
                    {
                        link.Send(FrameCodec.MsgCfgReq, new byte[0]);
                        Thread.Sleep(700);
                        link.Send(FrameCodec.MsgOledCfg, new byte[] { 0, 0 }); /* 自动探测 */
                        Thread.Sleep(700);
                        link.Send(FrameCodec.MsgCfgReq, new byte[0]);
                        Thread.Sleep(800);
                        /* RGB 自检：红/绿/蓝/白/灭循环 */
                        link.Send(FrameCodec.MsgRgbSet,
                            new byte[] { 7, 3, 128, 0xE8, 0x03, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00 });
                        Thread.Sleep(400);
                        /* 按键注入闭环：btn0 单击 + 编码器单击，设备应回传 MSG_EVT_BUTTON */
                        link.Send(FrameCodec.MsgBtnInject, new byte[] { 0, 3 });
                        Thread.Sleep(250);
                        link.Send(FrameCodec.MsgBtnInject, new byte[] { 8, 3 });
                        Thread.Sleep(300);
                        result["cfgReqSent"] = true;
                        result["oledCfgSent"] = true;
                        result["rgbTestSent"] = true;
                        result["buttonEvents"] = buttonEvents;
                        lock (buttonList) { result["buttonEventsList"] = buttonList.ToArray(); }
                        exitCode = 0;
                    }
                    lock (infoList) { result["infos"] = infoList; }
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                exitCode = 3;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static int RunDeepSeekProbe(string outputPath)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 1;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                AppConfig config = AppConfig.Load(exeDir, localAppData);
                using (ApiBalanceProvider provider = new ApiBalanceProvider(
                    config.DeepSeekApiKey, config.DeepSeekBaseUrl, config.DeepSeekRefreshSeconds,
                    config.ApiBalanceProvider))
                {
                    string msg = provider.Fetch();
                    result["configured"] = !string.IsNullOrWhiteSpace(config.DeepSeekApiKey);
                    result["provider"] = config.ApiBalanceProvider;
                    result["message"] = msg;
                    result["available"] = provider.Available;
                    result["balance"] = provider.BalanceCents;
                    result["currency"] = provider.Currency;
                    result["stale"] = provider.IsStale;
                    result["lastError"] = provider.LastError;
                    exitCode = (provider.Available && !provider.IsStale) ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                exitCode = 3;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static int RunPcMetricsProbe(string outputPath)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 1;
            try
            {
                using (var provider = new WindowsPcMetricsProvider())
                {
                    /* PerformanceCounter 首次样本可能为 0，稍候再取一次稳定样本。 */
                    provider.Read();
                    Thread.Sleep(350);
                    PcMetricsSnapshot snapshot = provider.Read();
                    result["cpuLoadPercent"] = snapshot.CpuLoadPercent;
                    result["memoryUsedPercent"] = snapshot.MemoryUsedPercent;
                    result["gpuLoadPercent"] = snapshot.GpuLoadPercent;
                    result["cpuTemperatureC"] = snapshot.CpuTemperatureC;
                    result["gpuTemperatureC"] = snapshot.GpuTemperatureC;
                    result["motherboardTemperatureC"] = snapshot.MotherboardTemperatureC;
                    result["cpuTemperatureSource"] = snapshot.TemperatureSource;
                    result["gpuSource"] = snapshot.GpuSource;
                    result["motherboardTemperatureSource"] =
                        snapshot.MotherboardTemperatureSource;
                    result["networkSpeedAvailable"] = snapshot.NetworkSpeedAvailable;
                    result["networkDownloadKiBPerSecond"] =
                        snapshot.NetworkDownloadKiBPerSecond;
                    result["networkUploadKiBPerSecond"] =
                        snapshot.NetworkUploadKiBPerSecond;
                    result["errorText"] = snapshot.ErrorText;
                    exitCode = snapshot.CpuLoadPercent >= 0
                        && snapshot.MemoryUsedPercent >= 0 ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                exitCode = 3;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result),
                new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static int RunAcpiFallbackTest(string outputPath)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 9;
            try
            {
                var method = typeof(WindowsPcMetricsProvider).GetMethod(
                    "ApplyAcpiMotherboardFallback",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                result["methodFound"] = method != null;
                if (method != null)
                {
                    var missingBoard = PcMetricsSnapshot.CreateUnavailable("", DateTimeOffset.UtcNow);
                    method.Invoke(null, new object[] { missingBoard, (double?)42.5 });
                    bool filledBoard = missingBoard.MotherboardTemperatureC == 42.5
                        && missingBoard.MotherboardTemperatureSource ==
                            PcMetricsSnapshot.SourceAcpiThermalZone
                        && !missingBoard.CpuTemperatureC.HasValue;

                    var realBoard = PcMetricsSnapshot.CreateUnavailable("", DateTimeOffset.UtcNow);
                    realBoard.MotherboardTemperatureC = 39.0;
                    realBoard.MotherboardTemperatureSource =
                        PcMetricsSnapshot.SourceLibreHardwareMonitor;
                    method.Invoke(null, new object[] { realBoard, (double?)42.5 });
                    bool preservedBoard = realBoard.MotherboardTemperatureC == 39.0
                        && realBoard.MotherboardTemperatureSource ==
                            PcMetricsSnapshot.SourceLibreHardwareMonitor;

                    result["fillsMissingBoard"] = filledBoard;
                    result["preservesRealBoard"] = preservedBoard;
                    result["cpuUnaffected"] = !missingBoard.CpuTemperatureC.HasValue;
                    exitCode = filledBoard && preservedBoard ? 0 : 9;
                }
            }
            catch (Exception ex)
            {
                result["error"] = ex.GetBaseException().GetType().Name + ": "
                    + ex.GetBaseException().Message;
                exitCode = 9;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result),
                new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static int RunEmbeddedDependenciesTest(string outputPath, string cacheRoot)
        {
            var result = new Dictionary<string, object>();
            int exitCode = 9;
            try
            {
                EmbeddedDependencyPreparation prepared =
                    EmbeddedDependencyStore.Prepare(cacheRoot);
                bool libraryLoadOk;
                using (var provider = new LibreHardwareMonitorProvider(cacheRoot))
                    libraryLoadOk = provider.Available;

                int cachedDllCount = Directory.GetFiles(prepared.DirectoryPath,
                    "*.dll", SearchOption.TopDirectoryOnly).Length;
                bool ok = prepared.ResourceCount > 0
                    && prepared.ResourceCount == prepared.FileNames.Length
                    && cachedDllCount == prepared.ResourceCount
                    && prepared.AllHashesValid
                    && libraryLoadOk;

                result["ok"] = ok;
                result["resourceCount"] = prepared.ResourceCount;
                result["extractedCount"] = cachedDllCount;
                result["allHashesValid"] = prepared.AllHashesValid;
                result["libraryLoadOk"] = libraryLoadOk;
                result["cacheDirectory"] = prepared.DirectoryPath;
                exitCode = ok ? 0 : 9;
            }
            catch (Exception ex)
            {
                Exception actual = ex.GetBaseException();
                result["ok"] = false;
                result["error"] = actual.GetType().Name + ": " + actual.Message;
                exitCode = 9;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result),
                new UTF8Encoding(false));
            Environment.Exit(exitCode);
            return exitCode;
        }

        private static int RunProbe(string outputPath)
        {
            var result = new Dictionary<string, object>();
            try
            {
                using (CodexStatusProvider provider = CodexStatusProvider.CreateDefault(30))
                {
                    System.Threading.Tasks.Task start = provider.StartAsync();
                    if (!start.Wait(TimeSpan.FromSeconds(75))) throw new TimeoutException("额度探测超时");
                    QuotaSnapshot snapshot = provider.Quota;
                    result["primaryRemainingPercent"] = snapshot.PrimaryRemainingPercent;
                    result["secondaryRemainingPercent"] = snapshot.SecondaryRemainingPercent;
                    result["primaryResetsAt"] = snapshot.PrimaryResetsAt.HasValue ? (object)snapshot.PrimaryResetsAt.Value.ToUnixTimeSeconds() : null;
                    result["secondaryResetsAt"] = snapshot.SecondaryResetsAt.HasValue ? (object)snapshot.SecondaryResetsAt.Value.ToUnixTimeSeconds() : null;
                    result["observedAt"] = snapshot.ObservedAt.ToUnixTimeSeconds();
                    result["isStale"] = snapshot.IsStale;
                    provider.Stop();
                }
            }
            catch (Exception ex)
            {
                Exception actual = ex as AggregateException != null ? ((AggregateException)ex).GetBaseException() : ex;
                result["error"] = actual.GetType().Name;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            Environment.Exit(result.ContainsKey("error") ? 1 : 0);
            return result.ContainsKey("error") ? 1 : 0;
        }

        private sealed class FakeChatGptAppRuntime : IChatGptAppRuntime
        {
            public FakeChatGptAppRuntime(bool running)
            {
                IsRunning = running;
                CloseResult = true;
                WaitExitResult = true;
                WaitExitAfterTerminateResult = true;
                TerminateResult = true;
                LaunchResult = true;
                WaitWindowResult = true;
            }

            public bool IsRunning { get; private set; }
            public bool CloseResult { get; set; }
            public bool WaitExitResult { get; set; }
            public bool WaitExitAfterTerminateResult { get; set; }
            public bool TerminateResult { get; set; }
            public bool LaunchResult { get; set; }
            public bool WaitWindowResult { get; set; }
            public int CloseCalls { get; private set; }
            public int WaitExitCalls { get; private set; }
            public int TerminateCalls { get; private set; }
            public int LaunchCalls { get; private set; }
            public int WaitWindowCalls { get; private set; }
            public int TotalCalls
            {
                get
                {
                    return CloseCalls + WaitExitCalls + TerminateCalls
                        + LaunchCalls + WaitWindowCalls;
                }
            }

            public bool TryClose(out string error)
            {
                CloseCalls++;
                error = CloseResult ? "" : "fake close failure";
                return CloseResult;
            }

            public bool WaitForExit(int timeoutMs)
            {
                WaitExitCalls++;
                return WaitExitCalls == 1 ? WaitExitResult : WaitExitAfterTerminateResult;
            }

            public bool TryTerminate(out string error)
            {
                TerminateCalls++;
                error = TerminateResult ? "" : "fake terminate failure";
                return TerminateResult;
            }

            public bool TryLaunch(out string error)
            {
                LaunchCalls++;
                error = LaunchResult ? "" : "fake launch failure";
                return LaunchResult;
            }

            public bool WaitForWindow(int timeoutMs)
            {
                WaitWindowCalls++;
                return WaitWindowResult;
            }
        }

        private sealed class FakeHostService : IHostService
        {
            public int RestartCalls { get; private set; }

            public void CycleOledPage() { }
            public void SetRgbMode(string mode) { }
            public void RefreshQuota() { }
            public void ShowSettings() { }
            public void RestartChatGpt() { RestartCalls++; }
            public void RunMijiaShortcut(int index) { }
            public void Toast(string message) { }
        }
    }
}
