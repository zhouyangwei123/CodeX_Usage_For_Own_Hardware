using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Actions;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.UI;

namespace CodexToolsHost.Tests
{
    internal static class UiBatchOneSelfChecks
    {
        public static int Run(string outputPath)
        {
            var checks = new Dictionary<string, object>();
            string root = Path.Combine(Path.GetTempPath(), "CodexToolsHostUiBatch-" + Guid.NewGuid().ToString("N"));
            try
            {
                string exe = Path.Combine(root, "exe");
                string local = Path.Combine(root, "local");
                Directory.CreateDirectory(exe);
                Directory.CreateDirectory(local);
                string json = "{\"configVersion\":9,\"bindings\":{\"btn0_click\":{\"action\":\"toggleDeepSeek\",\"param\":\"secret\"},\"btn1_click\":{\"action\":\"hotkey\",\"param\":\"Ctrl+Shift+K\"}},\"encoderRotate\":{\"action\":\"TOGGLEDEEPSEEK\",\"param\":\"secret\"}}";
                File.WriteAllText(Path.Combine(exe, AppConfig.DefaultFileName), json, Encoding.UTF8);
                AppConfig config = AppConfig.Load(exe, local);
                checks["legacyBindingMigrated"] = config.Bindings["btn0_click"].Action == "none"
                    && config.Bindings["btn0_click"].Param == ""
                    && config.EncoderRotate.Action == "none"
                    && config.EncoderRotate.Param == "" && config.NeedsSave
                    && config.RemovedSwitchBindingMigrated
                    && config.Bindings["btn1_click"].Action == "hotkey"
                    && config.Bindings["btn1_click"].Param == "Ctrl+Shift+K";
                config.Save();
                AppConfig reloaded = AppConfig.Load(exe, local);
                checks["migrationPersisted"] = reloaded.Bindings["btn0_click"].Action == "none"
                    && reloaded.EncoderRotate.Action == "none" && !reloaded.NeedsSave;
                checks["switchBackendAbsent"] = typeof(BridgeService).GetMethod("ToggleDeepSeek") == null
                    && typeof(BridgeService).GetProperty("DeepSeekConfig") == null
                    && typeof(IHostService).GetMethod("ToggleDeepSeek") == null
                    && typeof(BridgeService).Assembly.GetType(
                        "CodexToolsHost.Core.DeepSeekConfigManager") == null;
                bool invoked = false;
                ActionDispatcher.Execute(new ActionSpec("toggleDeepSeek", ""), new TestHost(delegate { invoked = true; }));
                checks["retiredActionInert"] = !invoked;

                using (var link = new MockDeviceLink { EmitInfoOnConnect = false })
                using (var bridge = new BridgeService(reloaded, link,
                    new FakeCodexSource(), new FakeDeepSeekSource()))
                using (var form = new SettingsForm(reloaded, bridge))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    var tabs = form.Controls.OfType<TabControl>().FirstOrDefault();
                    string[] expected = { "总览", "按键与旋钮", "显示与灯效", "服务与集成", "诊断与设置" };
                    checks["fivePages"] = tabs != null && tabs.TabPages.Count == expected.Length
                        && expected.SequenceEqual(tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
                    var navigationButtons = form.Controls.OfType<Panel>()
                        .Where(panel => panel.Dock == DockStyle.Left)
                        .SelectMany(panel => panel.Controls.OfType<Panel>())
                        .SelectMany(panel => panel.Controls.OfType<Button>()).ToArray();
                    Button buttonsPage = navigationButtons.FirstOrDefault(button =>
                        button.Text == "按键与旋钮");
                    Button overviewPage = navigationButtons.FirstOrDefault(button =>
                        button.Text == "总览");
                    if (buttonsPage != null) buttonsPage.PerformClick();
                    bool selectedButtons = tabs != null && tabs.SelectedIndex == 1;
                    if (overviewPage != null) overviewPage.PerformClick();
                    checks["sideNavigationSelectsPage"] = selectedButtons && tabs != null
                        && tabs.SelectedIndex == 0;
                    checks["noSwitchPage"] = tabs != null && !tabs.TabPages.Cast<TabPage>()
                        .Any(p => p.Text.IndexOf("切换", StringComparison.Ordinal) >= 0);
                    checks["encoderAndButtonsTogether"] = tabs != null
                        && tabs.TabPages[1].Controls.Find("buttonGrid", true).Length == 1
                        && tabs.TabPages[1].Controls.Find("encoderAction", true).Length == 1;
                    Label quotaValue = tabs == null ? null : tabs.TabPages[0]
                        .Controls.Find("codexPrimaryValue", true).FirstOrDefault() as Label;
                    Label quotaStatus = tabs == null ? null : tabs.TabPages[0]
                        .Controls.Find("codexQuotaStatus", true).FirstOrDefault() as Label;
                    checks["overviewActualQuota"] = quotaValue != null
                        && quotaValue.Text.Contains("58%") && quotaStatus != null
                        && quotaStatus.Text.Contains("采样于");
                    if (tabs != null)
                    {
                        for (int i = 0; i < tabs.TabPages.Count; i++)
                        {
                            tabs.SelectedIndex = i;
                            form.PerformLayout();
                            form.Update();
                            SavePreview(form, outputPath, "ui-page-" + i);
                        }
                        tabs.SelectedIndex = 3;
                        var services = tabs.TabPages[3].Controls.OfType<TabControl>().First();
                        services.SelectedIndex = 1;
                        form.PerformLayout();
                        form.Update();
                        var executeButtons = FindTaggedButtons(services.TabPages[1], "mijia-execute");
                        checks["mijiaRowsAligned"] = executeButtons.Count == 8
                            && executeButtons.All(button => button.Height <= 35);
                        SavePreview(form, outputPath, "ui-services-mijia");
                        tabs.SelectedIndex = 4;
                        Label providerHealth = typeof(SettingsForm).GetField("_diagnosticProviderHealth",
                            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) as Label;
                        checks["unconfiguredApiIsInactive"] = providerHealth != null
                            && providerHealth.Text.Contains("API 余额：未启用")
                            && providerHealth.Text.Contains("OpenCode Go：未启用");
                        var diagnostics = tabs.TabPages[4].Controls.OfType<TableLayoutPanel>()
                            .SelectMany(p => p.Controls.OfType<TabControl>()).First();
                        diagnostics.SelectedIndex = 1;
                        SavePreview(form, outputPath, "ui-diagnostics-about");
                        form.Size = new Size(900, 650);
                        tabs.SelectedIndex = 0;
                        SavePreview(form, outputPath, "ui-min-overview");
                        tabs.SelectedIndex = 1;
                        SavePreview(form, outputPath, "ui-min-buttons");
                        tabs.SelectedIndex = 3;
                        services.SelectedIndex = 0;
                        SavePreview(form, outputPath, "ui-min-services");
                        services.SelectedIndex = 1;
                        SavePreview(form, outputPath, "ui-min-mijia");
                        tabs.SelectedIndex = 4;
                        diagnostics.SelectedIndex = 0;
                        SavePreview(form, outputPath, "ui-min-diagnostics");
                    }
                }
                checks["hiddenApiErrorRefreshesOnReturn"] = CheckHiddenApiErrorRefresh();
                bool pcSummary;
                bool refreshAction;
                CheckOverviewActions(out pcSummary, out refreshAction);
                checks["overviewPcUsesSnapshot"] = pcSummary;
                checks["overviewRefreshInvokesBridge"] = refreshAction;
            }
            catch (Exception ex) { checks["error"] = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            bool ok = checks.Count == 14 && checks.Values.All(v => v is bool && (bool)v);
            checks["ok"] = ok;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            File.WriteAllText(outputPath, new JavaScriptSerializer().Serialize(checks), new UTF8Encoding(false));
            return ok ? 0 : 1;
        }

        private static void SavePreview(Form form, string outputPath, string name)
        {
            form.PerformLayout();
            form.Update();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                string imagePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)),
                    name + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(imagePath));
                bitmap.Save(imagePath, ImageFormat.Png);
            }
        }

        private static List<Button> FindTaggedButtons(Control root, string tag)
        {
            var buttons = new List<Button>();
            Button button = root as Button;
            if (button != null && string.Equals(button.Tag as string, tag, StringComparison.Ordinal))
                buttons.Add(button);
            foreach (Control child in root.Controls)
                buttons.AddRange(FindTaggedButtons(child, tag));
            return buttons;
        }

        private static bool CheckHiddenApiErrorRefresh()
        {
            AppConfig config = AppConfig.CreateDefault();
            var provider = new MutableApiBalanceSource();
            using (var link = new MockDeviceLink { EmitInfoOnConnect = false })
            using (var bridge = new BridgeService(config, link, new FakeCodexSource(), null,
                provider, new PcMonitorService(new WindowsPcMetricsProvider(), 2000),
                null, new FakeOpenCodeGoSource()))
            using (var form = new SettingsForm(config, bridge))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.ShowInTaskbar = false;
                form.Show();
                TabControl tabs = form.Controls.OfType<TabControl>().First();
                Label status = typeof(SettingsForm).GetField("_dsTestResult",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) as Label;
                tabs.SelectedIndex = 3;
                if (status == null || status.Text != "最近查询无错误") return false;
                tabs.SelectedIndex = 0;
                provider.SetError("fixture provider outage");
                if (status.Text != "最近查询无错误") return false;
                tabs.SelectedIndex = 3;
                return status.Text.Contains("fixture provider outage");
            }
        }

        private static void CheckOverviewActions(out bool pcSummary, out bool refreshAction)
        {
            pcSummary = false;
            refreshAction = false;
            var source = new FixedPcMetricsProvider();
            var monitor = new PcMonitorService(source, 2000);
            monitor.RefreshNow();
            var api = new FakeDeepSeekSource();
            AppConfig config = AppConfig.CreateDefault();
            using (var link = new MockDeviceLink { EmitInfoOnConnect = false })
            using (var bridge = new BridgeService(config, link, new FakeCodexSource(), null,
                api, monitor, null, new FakeOpenCodeGoSource()))
            using (var form = new SettingsForm(config, bridge))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.ShowInTaskbar = false;
                form.Show();
                Label summary = form.Controls.Find("overviewPcSummary", true).FirstOrDefault() as Label;
                pcSummary = summary != null && summary.Text.Contains("CPU 37%")
                    && summary.Text.Contains("内存 50%");
                Button refresh = form.Controls.Find("refreshQuotaButton", true).FirstOrDefault()
                    as Button;
                if (refresh != null)
                {
                    refresh.PerformClick();
                    refreshAction = api.FetchCalls == 1;
                }
            }
        }

        private sealed class FixedPcMetricsProvider : IPcMetricsProvider
        {
            public PcMetricsSnapshot Read()
            {
                return new PcMetricsSnapshot
                {
                    CpuLoadPercent = 37,
                    GpuLoadPercent = 42,
                    MemoryUsedBytes = 4UL * 1024UL * 1024UL * 1024UL,
                    MemoryTotalBytes = 8UL * 1024UL * 1024UL * 1024UL,
                    SampledAtUtc = DateTimeOffset.UtcNow,
                    IsStale = false
                };
            }
        }

        private sealed class MutableApiBalanceSource : IDeepSeekSource
        {
            public event Action Changed;
            public long BalanceCents { get { return 0; } }
            public string Currency { get { return "CNY"; } }
            public bool Available { get { return false; } }
            public bool IsStale { get { return !string.IsNullOrEmpty(LastError); } }
            public bool Unlimited { get { return false; } }
            public string ProviderName { get { return "fixture"; } }
            public string LastError { get; private set; }
            public void SetError(string error)
            {
                LastError = error;
                var changed = Changed;
                if (changed != null) changed();
            }
            public void Start() { }
            public void Stop() { }
            public string Fetch() { return "fixture"; }
            public void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds) { }
            public void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds,
                string provider) { }
            public void Dispose() { }
        }

        private sealed class TestHost : IHostService
        {
            private readonly Action _action;
            public TestHost(Action action) { _action = action; }
            public void CycleOledPage() { _action(); }
            public void SetRgbMode(string mode) { _action(); }
            public void RefreshQuota() { _action(); }
            public void ShowSettings() { _action(); }
            public void RestartChatGpt() { _action(); }
            public void RunMijiaShortcut(int index) { _action(); }
            public void Toast(string message) { _action(); }
        }
    }
}
