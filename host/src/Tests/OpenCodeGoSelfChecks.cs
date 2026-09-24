using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Monitor;
using CodexToolsHost.Model;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.UI;

namespace CodexToolsHost.Tests
{
    internal static class OpenCodeGoSelfChecks
    {
        public static Dictionary<string, object> Run()
        {
            var result = new Dictionary<string, object>();
            DateTimeOffset observed = new DateTimeOffset(
                2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
            string complete = "{\"usage\":{"
                + "\"rolling\":{\"status\":\"ok\",\"percent\":42,\"resetsAt\":\"2026-09-03T02:14:36Z\"},"
                + "\"weekly\":{\"status\":\"ok\",\"percent\":18,\"resetsAt\":\"2026-09-07T00:00:00Z\"},"
                + "\"monthly\":{\"status\":\"rate-limited\",\"percent\":120,\"resetsAt\":\"2026-10-01T00:00:00Z\"}"
                + "}}";

            bool completeOk = false;
            try
            {
                OpenCodeGoQuotaSnapshot snapshot =
                    OpenCodeGoQuotaProvider.ParseUsageJson(complete, observed);
                completeOk = snapshot.Rolling.UsedPercent == 42
                    && snapshot.Rolling.RemainingPercent == 58
                    && snapshot.Rolling.ResetsAt.HasValue
                    && snapshot.Rolling.ResetsAt.Value == new DateTimeOffset(
                        2026, 9, 3, 2, 14, 36, TimeSpan.Zero)
                    && snapshot.Rolling.Status == "ok"
                    && snapshot.Weekly.UsedPercent == 18
                    && snapshot.Weekly.RemainingPercent == 82
                    && snapshot.Monthly.UsedPercent == 100
                    && snapshot.Monthly.RemainingPercent == 0
                    && snapshot.Monthly.IsRateLimited;
            }
            catch (Exception)
            {
                completeOk = false;
            }
            result["openCodeGoCompleteParseOk"] = completeOk;

            bool missingWindowOk = false;
            try
            {
                OpenCodeGoQuotaSnapshot snapshot =
                    OpenCodeGoQuotaProvider.ParseUsageJson(
                        "{\"usage\":{\"rolling\":{\"status\":\"ok\",\"percent\":0},"
                        + "\"weekly\":{\"status\":\"ok\",\"percent\":7}}}", observed);
                missingWindowOk = snapshot.Rolling.RemainingPercent == 100
                    && snapshot.Weekly.RemainingPercent == 93
                    && snapshot.Monthly.UsedPercent == null
                    && snapshot.Monthly.RemainingPercent == null;
            }
            catch (Exception)
            {
                missingWindowOk = false;
            }
            result["openCodeGoMissingWindowOk"] = missingWindowOk;

            bool malformedOk = false;
            try
            {
                OpenCodeGoQuotaProvider.ParseUsageJson("{\"hello\":1}", observed);
            }
            catch (InvalidDataException)
            {
                malformedOk = true;
            }
            catch (Exception)
            {
                malformedOk = false;
            }
            result["openCodeGoMalformedRejectedOk"] = malformedOk;
            result["openCodeGoParseOk"] = completeOk && missingWindowOk && malformedOk;

            bool configRoundtripOk = false;
            bool legacyMigrationOk = false;
            string root = Path.Combine(Path.GetTempPath(),
                "CodexToolsHostOpenCodeGoConfig-" + Guid.NewGuid().ToString("N"));
            try
            {
                AppConfig config = AppConfig.Load(root, root);
                bool defaultsOk = config.ConfigVersion == 9
                    && config.OpenCodeGoApiKey == ""
                    && config.OpenCodeGoRefreshSeconds == 300
                    && config.QuotaDisplaySource == "deepseek";
                config.OpenCodeGoApiKey = "go-test-key";
                config.OpenCodeGoRefreshSeconds = 120;
                config.QuotaDisplaySource = "opencodego";
                config.Save();
                AppConfig loaded = AppConfig.Load(root, root);
                configRoundtripOk = defaultsOk
                    && loaded.ConfigVersion == 9
                    && loaded.OpenCodeGoApiKey == "go-test-key"
                    && loaded.OpenCodeGoRefreshSeconds == 120
                    && loaded.QuotaDisplaySource == "opencodego";

                string path = Path.Combine(root, AppConfig.DefaultFileName);
                var legacy = new Dictionary<string, object>();
                legacy["configVersion"] = 8;
                legacy["deepSeekApiKey"] = "deepseek-test-key";
                legacy["apiBalanceProvider"] = "deepseek";
                Directory.CreateDirectory(root);
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(legacy),
                    new UTF8Encoding(false));
                AppConfig migrated = AppConfig.Load(root, root);
                legacyMigrationOk = migrated.ConfigVersion == 9
                    && migrated.DeepSeekApiKey == "deepseek-test-key"
                    && migrated.OpenCodeGoApiKey == ""
                    && migrated.QuotaDisplaySource == "deepseek"
                    && migrated.NeedsSave;
            }
            catch (Exception)
            {
                configRoundtripOk = false;
                legacyMigrationOk = false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (Exception) { }
            }
            result["openCodeGoConfigRoundtripOk"] = configRoundtripOk;
            result["openCodeGoLegacyMigrationOk"] = legacyMigrationOk;
            result["openCodeGoConfigOk"] = configRoundtripOk && legacyMigrationOk;

            bool selectedSourceLifecycleOk = CheckSelectedSourceLifecycle();
            result["openCodeGoSelectedSourceLifecycleOk"] = selectedSourceLifecycleOk;
            bool presentationOk = CheckPresentation();
            result["openCodeGoPresentationOk"] = presentationOk;
            bool settingsOk = CheckSettingsContract();
            result["openCodeGoSettingsContractOk"] = settingsOk;
            return result;
        }

        private static bool CheckSettingsContract()
        {
            Type settings = typeof(SettingsForm);
            bool fields = settings.GetField("_quotaDisplaySource",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null
                && settings.GetField("_goKey",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null
                && settings.GetField("_goRefresh",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null
                && settings.GetField("_goTestResult",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null
                && settings.GetField("_goRollingValue",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null
                && settings.GetField("_goWeeklyValue",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null
                && settings.GetField("_goMonthlyValue",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null;
            if (!fields) return false;

            BridgeService bridge = null;
            SettingsForm form = null;
            try
            {
                AppConfig config = AppConfig.CreateDefault();
                bridge = new BridgeService(config,
                    new MockDeviceLink { EmitInfoOnConnect = false },
                    new FakeCodexSource(), null, new FakeDeepSeekSource(),
                    new PcMonitorService(new WindowsPcMetricsProvider(), 2000),
                    null, new FakeOpenCodeGoSource());
                form = new SettingsForm(config, bridge);
                ComboBox source = settings.GetField("_quotaDisplaySource",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) as ComboBox;
                TextBox key = settings.GetField("_goKey",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) as TextBox;
                bool sourceItems = source != null && source.Items.Count == 2
                    && Convert.ToString(source.Items[0]) == "DeepSeek 官方余额"
                    && Convert.ToString(source.Items[1]) == "OpenCode Go 额度";
                bool keyContract = key != null && key.UseSystemPasswordChar;
                bool endpoint = ContainsControlText(form, OpenCodeGoQuotaProvider.UsageEndpoint);
                bool windows = ContainsControlText(form, "5h：")
                    && ContainsControlText(form, "7d：")
                    && ContainsControlText(form, "月：");
                return sourceItems && keyContract && endpoint && windows;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (form != null)
                {
                    try { form.Close(); } catch (Exception) { }
                    try { form.Dispose(); } catch (Exception) { }
                }
                if (bridge != null) bridge.Dispose();
            }
        }

        private static bool ContainsControlText(Control root, string expected)
        {
            if (root == null) return false;
            if (string.Equals(root.Text, expected, StringComparison.Ordinal)) return true;
            foreach (Control child in root.Controls)
                if (ContainsControlText(child, expected)) return true;
            return false;
        }

        private static bool CheckPresentation()
        {
            DateTimeOffset now = new DateTimeOffset(
                2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
            OpenCodeGoQuotaSnapshot snapshot = new OpenCodeGoQuotaSnapshot(
                new OpenCodeGoQuotaWindow("ok", 42, now.AddHours(2).AddMinutes(14).AddSeconds(36)),
                new OpenCodeGoQuotaWindow("ok", 18, null),
                new OpenCodeGoQuotaWindow("ok", 9, now.AddDays(20)), now, false);
            string expected = "GO 5h余58%  7d余82%\r\n   月余91%  5h→02:14:36";
            bool summary = QuotaHudPresentation.FormatOpenCodeGoSummary(snapshot, now) == expected;
            bool countdown = QuotaHudPresentation.FormatResetCountdown(
                snapshot.Rolling.ResetsAt, now) == "02:14:36"
                && QuotaHudPresentation.FormatResetCountdown(null, now) == "--:--:--";
            bool stale = QuotaHudPresentation.FormatOpenCodeGoSummary(
                OpenCodeGoQuotaSnapshot.EmptyStale(), now)
                == "GO 5h余--%  7d余--%\r\n   月余--%  5h→--:--:--";
            OpenCodeGoQuotaSnapshot limited = new OpenCodeGoQuotaSnapshot(
                new OpenCodeGoQuotaWindow("rate-limited", 100, now.AddHours(1)),
                OpenCodeGoQuotaWindow.Empty(), OpenCodeGoQuotaWindow.Empty(), now, false);
            bool rateLimited = QuotaHudPresentation.FormatOpenCodeGoSummary(limited, now)
                .StartsWith("GO 5h已达上限  7d余--%", StringComparison.Ordinal);
            string deepSeek = QuotaHudPresentation.FormatDeepSeekSummary(
                "DeepSeek", "CNY", 1234, true, false, false, now);
            bool deepSeekSummary = deepSeek == "DeepSeek · CNY 12.34\r\n   已更新 "
                + now.ToLocalTime().ToString("HH:mm:ss");
            return summary && countdown && stale && rateLimited && deepSeekSummary;
        }

        private static bool CheckSelectedSourceLifecycle()
        {
            FakeDeepSeekSource deepSeek = new FakeDeepSeekSource();
            FakeOpenCodeGoSource openCodeGo = new FakeOpenCodeGoSource();
            BridgeService bridge = null;
            try
            {
                AppConfig config = AppConfig.CreateDefault();
                config.QuotaDisplaySource = "deepseek";
                bridge = new BridgeService(config,
                    new MockDeviceLink { EmitInfoOnConnect = false },
                    new FakeCodexSource(), null, deepSeek,
                    new PcMonitorService(new WindowsPcMetricsProvider(), 2000),
                    null, openCodeGo);
                bridge.Start();
                bridge.RefreshQuota();
                bool deepSeekSelected = deepSeek.StartCalls == 1
                    && deepSeek.FetchCalls == 1 && openCodeGo.StartCalls == 0
                    && openCodeGo.FetchCalls == 0;
                config.QuotaDisplaySource = "opencodego";
                bridge.ApplyDeviceConfig();
                bridge.RefreshQuota();
                return deepSeekSelected && deepSeek.StopCalls >= 1
                    && openCodeGo.StartCalls == 1 && openCodeGo.FetchCalls == 1
                    && bridge.OpenCodeGo == openCodeGo;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (bridge != null) bridge.Dispose();
            }
        }
    }
}
