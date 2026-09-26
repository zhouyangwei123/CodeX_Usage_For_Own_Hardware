using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Protocol;
using CodexToolsHost.UI;
using CodexToolsHost.Updates;
using CodexToolsHost.Usage;

namespace CodexToolsHost.Tests
{
    internal static class PhaseTwoSelfChecks
    {
        public static int Run(string outputPath)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var checks = new Dictionary<string, object>();
            string output = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            Directory.CreateDirectory(output);
            string fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N"));
            string codex = Path.Combine(fixture, "codex");
            Directory.CreateDirectory(Path.Combine(codex, "sessions"));
            var json = new JavaScriptSerializer();
            string now = DateTimeOffset.UtcNow.ToString("o");
            string[] names = { "上位机维护", "界面设计", "代码检查" };
            long[] input = { 420000, 350000, 510000 }, cached = { 310000, 260000, 370000 }, result = { 24000, 19000, 43000 };
            for (int i = 0; i < names.Length; i++)
            {
                string id = "demo-usage-" + i;
                string meta = json.Serialize(new { timestamp = now, type = "session_meta", payload = new { id = id, timestamp = now, cwd = "D:/演示/" + names[i] } });
                string model = json.Serialize(new { timestamp = now, type = "turn_context", payload = new { model = "gpt-6-sol" } });
                var tokens = new { input_tokens = input[i], cached_input_tokens = cached[i], output_tokens = result[i], reasoning_output_tokens = 1000, total_tokens = input[i] + result[i] };
                string count = json.Serialize(new { timestamp = now, type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = tokens, last_token_usage = tokens } } });
                File.WriteAllLines(Path.Combine(codex, "sessions", "rollout-" + id + ".jsonl"), new[] { meta, model, count });
            }
            try
            {
                var config = AppConfig.Load(fixture, fixture); config.Save();
                using (var usage = new LocalUsageService(codex))
                using (var updates = new ReleaseUpdateService(new Version(0, 3, 0), Path.Combine(fixture, "cache")))
                using (var link = new MockDeviceLink { EmitInfoOnConnect = false })
                using (var bridge = new BridgeService(config, link, new FakeCodexSource(), new FakeDeepSeekSource()))
                {
                    usage.RefreshAsync().GetAwaiter().GetResult();
                    using (var form = new SettingsForm(config, bridge, usage, updates))
                    {
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                        form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
                        var tabs = form.Controls.OfType<TabControl>().Single();
                        checks["usageNavigationAttached"] = tabs.TabPages.Count == 6 && tabs.TabPages[5].Text == "用量清单";
                        checks["updatePanelAttached"] = Descendants(form).OfType<UpdateSettingsPanel>().Count() == 1;
                        int changed = 0; form.HudDisplaySettingsChanged += delegate { changed++; };
                        tabs.SelectedIndex = 2; Application.DoEvents();
                        var style = form.Controls.Find("hudStyle", true).Single() as ComboBox;
                        var apply = form.Controls.Find("applyHudAppearance", true).Single() as Button;
                        style.SelectedIndex = 1; apply.PerformClick();
                        checks["hudStyleAppliedAndSaved"] = changed == 1 && config.QuotaHudStyle == "minimal"
                            && AppConfig.Load(fixture, fixture).QuotaHudStyle == "minimal";
                        checks["hudAppearanceDoesNotSendHardwareSettings"] = link.RgbSetCount == 0 && link.OledPageCount == 0 && link.CfgReqCount == 0;
                        config.QuotaHudStyle = "classic"; config.QuotaHudScalePercent = 100; config.QuotaHudOpacity = 0.6;
                        apply.PerformClick();
                        checks["hudUneditedControlsPreserveExternalChanges"] = config.QuotaHudStyle == "classic"
                            && config.QuotaHudScalePercent == 100 && Math.Abs(config.QuotaHudOpacity - 0.6) < 0.001;
                        style.SelectedIndex = 1; config.QuotaHudScalePercent = 90;
                        form.SyncHudSettingsFromConfig();
                        checks["traySyncPreservesUnappliedOtherFields"] = style.SelectedIndex == 1
                            && ((ComboBox)form.Controls.Find("hudScale", true).Single()).SelectedIndex == 2;
                        config.QuotaHudStyle = "glass"; config.Save();
                        style.SelectedIndex = 0; style.SelectedIndex = 1;
                        int notificationsBeforeFailure = changed;
                        using (var locked = new FileStream(config.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            apply.PerformClick();
                        checks["hudFailedSaveRollsBackSharedConfig"] = config.QuotaHudStyle == "glass"
                            && AppConfig.Load(fixture, fixture).QuotaHudStyle == "glass" && changed == notificationsBeforeFailure;
                        style.SelectedIndex = 0; apply.PerformClick();
                        foreach (Size size in new[] { new Size(1020, 760), new Size(900, 650) })
                        {
                            form.Size = size;
                            foreach (int index in new[] { 2, 5, 4 })
                            {
                                tabs.SelectedIndex = index;
                                if (index == 4)
                                {
                                    var nested = tabs.TabPages[index].Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
                                    nested.SelectedIndex = nested.TabPages.Count - 1;
                                }
                                Application.DoEvents();
                                using (var bitmap = new Bitmap(form.Width, form.Height))
                                {
                                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                                    bitmap.Save(Path.Combine(output, "phase-two-page-" + index + "-" + size.Width + ".png"), ImageFormat.Png);
                                }
                            }
                        }
                        form.Close();
                        checks["panelsDisposedWithSettings"] = form.IsDisposed;
                    }
                    // Closing a settings window must not dispose tray-owned services.
                    usage.RefreshAsync().GetAwaiter().GetResult();
                    checks["usageServiceSurvivesSettingsClose"] = usage.Report != null;
                    checks["configRoundTripStillValid"] = AppConfig.Load(fixture, fixture).ConfigVersion == 11;
                }
            }
            catch (Exception error) { checks["exception"] = error.GetType().Name + ": " + error.Message; }
            File.WriteAllText(outputPath, json.Serialize(checks));
            return checks.ContainsKey("exception") || checks.Values.OfType<bool>().Any(value => !value) ? 1 : 0;
        }

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control control in root.Controls)
            {
                yield return control;
                foreach (Control child in Descendants(control)) yield return child;
            }
        }
    }
}
