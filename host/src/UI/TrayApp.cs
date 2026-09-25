using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Usage;
using CodexToolsHost.Updates;

namespace CodexToolsHost.UI
{
    public sealed class TrayApp : ApplicationContext
    {
        private readonly AppConfig _config;
        private readonly BridgeService _bridge;
        private readonly LocalUsageService _usage;
        private readonly ReleaseUpdateService _updates;
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _statusItem;
        private SettingsForm _settingsForm;
        private QuotaHudForm _quotaHud;
        private ToolStripMenuItem _quotaHudItem;
        private ToolStripMenuItem _quotaHudSizeItem;
        private ToolStripMenuItem _quotaHudOpacityItem;
        private ToolStripMenuItem _quotaHudTopMostItem;
        private ToolStripMenuItem _quotaHudStyleItem;
        private ToolStripMenuItem _updateItem;
        private string _lastBalloonText;
        private DateTime _lastBalloonAt;
        private bool _exiting;
        private bool _bridgeDisposed;
        private bool _servicesDisposed;

        public TrayApp(AppConfig config, BridgeService bridge)
        {
            _config = config;
            _bridge = bridge;
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            _usage = new LocalUsageService(codexHome);
            _updates = new ReleaseUpdateService(typeof(TrayApp).Assembly.GetName().Version,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexToolsHost", "cache"));
            _icon = new NotifyIcon
            {
                Icon = BuildCoffeeIcon(),
                Text = "CodeX Tools",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem(_bridge.BuildSummaryText()) { Enabled = false };
            ToolStripMenuItem migrationItem = _config.RemovedSwitchBindingMigrated
                ? new ToolStripMenuItem("旧版账号切换绑定已停用；请在按键与旋钮中检查")
                    { Enabled = false }
                : null;
            var settingsItem = new ToolStripMenuItem("打开设置…");
            _updateItem = new ToolStripMenuItem("软件更新…");
            var refreshItem = new ToolStripMenuItem("刷新额度");
            _quotaHudItem = new ToolStripMenuItem("显示额度与 API 余额");
            _quotaHudSizeItem = new ToolStripMenuItem("血条尺寸");
            AddScaleOption(_quotaHudSizeItem, "60%", 60);
            AddScaleOption(_quotaHudSizeItem, "75%", 75);
            AddScaleOption(_quotaHudSizeItem, "90%", 90);
            AddScaleOption(_quotaHudSizeItem, "100%", 100);
            _quotaHudOpacityItem = new ToolStripMenuItem("血条透明度");
            AddOpacityOption(_quotaHudOpacityItem, "35%", 0.35d);
            AddOpacityOption(_quotaHudOpacityItem, "50%", 0.50d);
            AddOpacityOption(_quotaHudOpacityItem, "70%", 0.70d);
            AddOpacityOption(_quotaHudOpacityItem, "90%", 0.90d);
            AddOpacityOption(_quotaHudOpacityItem, "100%", 1.0d);
            _quotaHudTopMostItem = new ToolStripMenuItem("始终置顶");
            _quotaHudStyleItem = new ToolStripMenuItem("浮窗外观");
            AddStyleOption(_quotaHudStyleItem, "柔光玻璃", "glass");
            AddStyleOption(_quotaHudStyleItem, "极简清晰", "minimal");
            AddStyleOption(_quotaHudStyleItem, "经典双色", "classic");
            var oledItem = new ToolStripMenuItem("循环 OLED 页面");
            var oledAutoItem = new ToolStripMenuItem("开启 OLED 自动轮播");
            var rgbItem = new ToolStripMenuItem("RGB 模式");
            var rgbStatus = new ToolStripMenuItem("状态色");
            var rgbSolid = new ToolStripMenuItem("常亮");
            var rgbBreath = new ToolStripMenuItem("呼吸");
            var rgbRainbow = new ToolStripMenuItem("彩虹");
            var rgbWave = new ToolStripMenuItem("波动");
            var rgbBlink = new ToolStripMenuItem("闪烁");
            var rgbOff = new ToolStripMenuItem("关闭");
            var exitItem = new ToolStripMenuItem("退出");

            rgbItem.DropDownItems.AddRange(new ToolStripItem[] {
                rgbStatus, rgbSolid, rgbBreath, rgbRainbow, rgbWave, rgbBlink, rgbOff });
            menu.Items.AddRange(new ToolStripItem[] {
                _statusItem,
                new ToolStripSeparator(),
                settingsItem,
                _updateItem,
                refreshItem,
                _quotaHudItem,
                _quotaHudStyleItem,
                _quotaHudSizeItem,
                _quotaHudOpacityItem,
                _quotaHudTopMostItem,
                oledItem,
                oledAutoItem,
                rgbItem,
                new ToolStripSeparator(),
                exitItem });
            if (migrationItem != null) menu.Items.Insert(1, migrationItem);
            _icon.ContextMenuStrip = menu;

            menu.Opening += delegate { UpdateHudMenuChecks(); };
            _icon.DoubleClick += delegate { SafeInvoke(OpenSettings); };

            settingsItem.Click += delegate { OpenSettings(); };
            _updateItem.Click += delegate { OpenSettings(); _settingsForm.ShowUpdates(); };
            refreshItem.Click += delegate { _bridge.RefreshQuota(); };
            _quotaHudItem.Click += delegate { ToggleQuotaHud(); };
            _quotaHudTopMostItem.Click += delegate
            {
                _config.QuotaHudTopMost = !_config.QuotaHudTopMost;
                if (_quotaHud != null) _quotaHud.ApplyDisplaySettings();
                SyncSettingsHud();
                SaveConfigQuietly();
                UpdateHudMenuChecks();
            };
            oledItem.Click += delegate { _bridge.CycleOledPage(); };
            oledAutoItem.Click += delegate { _bridge.SetOledPage(2); };
            rgbStatus.Click += delegate { _bridge.SetRgbMode("status"); };
            rgbSolid.Click += delegate { _bridge.SetRgbMode("solid"); };
            rgbBreath.Click += delegate { _bridge.SetRgbMode("breath"); };
            rgbRainbow.Click += delegate { _bridge.SetRgbMode("rainbow"); };
            rgbWave.Click += delegate { _bridge.SetRgbMode("wave"); };
            rgbBlink.Click += delegate { _bridge.SetRgbMode("blink"); };
            rgbOff.Click += delegate { _bridge.SetRgbMode("off"); };
            exitItem.Click += delegate { ExitApp(); };

            _bridge.StatusChanged += delegate(string text) { SafeInvoke(delegate { UpdateStatus(text); }); };
            _bridge.Balloon += delegate(string message) { SafeInvoke(delegate { ShowBalloon(message); }); };
            _bridge.ShowSettingsRequested += delegate { SafeInvoke(OpenSettings); };

            _bridge.Start();
            _quotaHud = new QuotaHudForm(_config, _bridge.Codex, _bridge.DeepSeek,
                delegate { _bridge.RefreshQuota(); }, _bridge);
            if (_config.QuotaHudVisible) _quotaHud.ShowFromTray();
            _usage.Start();
            _updates.Changed += OnUpdateChanged;
            _updates.Start(_config.UpdateChecksEnabled);
            UpdateHudMenuChecks();
        }

        private void SafeInvoke(Action action)
        {
            try
            {
                Form host = Program.MainFormHandle;
                if (host == null)
                {
                    action();
                }
                else if (host.InvokeRequired)
                {
                    /* 确保句柄存在后再封送到 UI 线程，避免在串口/定时器线程上创建窗体 */
                    if (!host.IsHandleCreated)
                        host.CreateControl();
                    if (host.IsHandleCreated)
                        host.BeginInvoke(action);
                    else
                        action();
                }
                else
                {
                    action();
                }
            }
            catch (Exception) { }
        }

        private void UpdateStatus(string text)
        {
            _statusItem.Text = text ?? "";
            string tray = text ?? "CodeX Tools";
            if (tray.Length > 62) tray = tray.Substring(0, 62);
            _icon.Text = tray;
        }

        private void ShowBalloon(string message)
        {
            DateTime now = DateTime.Now;
            if (message == _lastBalloonText && (now - _lastBalloonAt).TotalSeconds < 5) return;
            _lastBalloonText = message;
            _lastBalloonAt = now;
            _icon.BalloonTipTitle = "CodeX Tools";
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(2500);
        }

        private void OpenSettings()
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(_config, _bridge, _usage, _updates);
                _settingsForm.HudDisplaySettingsChanged += ApplyHudSettings;
                _settingsForm.FormClosed += delegate { _settingsForm = null; };
            }
            _settingsForm.Show();
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.BringToFront();
        }

        private void ToggleQuotaHud()
        {
            if (_quotaHud == null) return;
            if (_quotaHud.Visible)
            {
                _quotaHud.HideFromTray();
                _config.QuotaHudVisible = false;
            }
            else
            {
                _quotaHud.ShowFromTray();
                _config.QuotaHudVisible = true;
            }
            UpdateHudMenuChecks();
            SaveConfigQuietly();
            SyncSettingsHud();
        }

        private void UpdateHudMenuChecks()
        {
            if (_quotaHudItem != null)
                _quotaHudItem.Checked = _quotaHud != null && _quotaHud.Visible;
            if (_quotaHudTopMostItem != null)
                _quotaHudTopMostItem.Checked = _config.QuotaHudTopMost;
            UpdateScaleMenuChecks();
            UpdateOpacityMenuChecks();
            if (_quotaHudStyleItem != null)
                foreach (ToolStripMenuItem option in _quotaHudStyleItem.DropDownItems)
                    option.Checked = (string)option.Tag == AppConfig.NormalizeQuotaHudStyle(_config.QuotaHudStyle);
        }

        private void AddStyleOption(ToolStripMenuItem parent, string label, string style)
        {
            var option = new ToolStripMenuItem(label) { Tag = style };
            option.Click += delegate
            {
                _config.QuotaHudStyle = style;
                if (_quotaHud != null) _quotaHud.ApplyDisplaySettings();
                SyncSettingsHud();
                SaveConfigQuietly(); UpdateHudMenuChecks();
            };
            parent.DropDownItems.Add(option);
        }

        private void ApplyHudSettings()
        {
            if (_quotaHud == null) return;
            _quotaHud.ApplyDisplaySettings();
            if (_config.QuotaHudVisible) _quotaHud.ShowFromTray(); else _quotaHud.HideFromTray();
            UpdateHudMenuChecks();
        }

        private void SyncSettingsHud()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.SyncHudSettingsFromConfig();
        }

        private void DisposeServices()
        {
            if (_servicesDisposed) return;
            _servicesDisposed = true;
            if (_settingsForm != null) { _settingsForm.Dispose(); _settingsForm = null; }
            _usage.Dispose();
            _updates.Changed -= OnUpdateChanged;
            _updates.Dispose();
        }

        private void OnUpdateChanged()
        {
            SafeInvoke(delegate
            {
                if (_exiting) return;
                UpdateSnapshot snapshot = _updates.Snapshot;
                _updateItem.Text = snapshot.State == UpdateCheckState.UpdateAvailable
                    ? "发现新版本 " + snapshot.LatestTag + "…" : "软件更新…";
            });
        }

        private void UpdateScaleMenuChecks()
        {
            if (_quotaHudSizeItem == null) return;
            foreach (ToolStripItem item in _quotaHudSizeItem.DropDownItems)
            {
                ToolStripMenuItem option = item as ToolStripMenuItem;
                if (option == null) continue;
                option.Checked = Convert.ToInt32(option.Tag) == _config.QuotaHudScalePercent;
            }
        }

        private void UpdateOpacityMenuChecks()
        {
            if (_quotaHudOpacityItem == null) return;
            foreach (ToolStripItem item in _quotaHudOpacityItem.DropDownItems)
            {
                ToolStripMenuItem option = item as ToolStripMenuItem;
                if (option == null) continue;
                double value = Convert.ToDouble(option.Tag);
                option.Checked = Math.Abs(value - _config.QuotaHudOpacity) < 0.001d;
            }
        }

        private void AddScaleOption(ToolStripMenuItem parent, string text, int percent)
        {
            ToolStripMenuItem option = new ToolStripMenuItem(text);
            option.Tag = percent;
            option.Click += delegate
            {
                _config.QuotaHudScalePercent = percent;
                if (_quotaHud != null) _quotaHud.ApplyDisplaySettings();
                SyncSettingsHud();
                SaveConfigQuietly();
                UpdateHudMenuChecks();
            };
            parent.DropDownItems.Add(option);
        }

        private void AddOpacityOption(ToolStripMenuItem parent, string text, double opacity)
        {
            ToolStripMenuItem option = new ToolStripMenuItem(text);
            option.Tag = opacity;
            option.Click += delegate
            {
                _config.QuotaHudOpacity = opacity;
                if (_quotaHud != null) _quotaHud.ApplyDisplaySettings();
                SyncSettingsHud();
                SaveConfigQuietly();
                UpdateHudMenuChecks();
            };
            parent.DropDownItems.Add(option);
        }

        private void SaveConfigQuietly()
        {
            try { _config.Save(); }
            catch (Exception) { }
        }

        private void DisposeHudWindows()
        {
            if (_quotaHud != null)
            {
                _quotaHud.BeginShutdown();
                _quotaHud.Close();
                _quotaHud.Dispose();
                _quotaHud = null;
            }
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _config.QuotaHudVisible = _quotaHud != null && _quotaHud.Visible;
            DisposeHudWindows();
            DisposeServices();
            SaveConfigQuietly();
            _icon.Visible = false;
            _icon.Dispose();
            if (!_bridgeDisposed)
            {
                _bridge.Dispose();
                _bridgeDisposed = true;
            }
            ExitThread();
        }

        private static Icon BuildCoffeeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    /* 咖啡杯身 */
                    using (SolidBrush cup = new SolidBrush(Color.FromArgb(255, 139, 90, 43)))
                    using (SolidBrush dark = new SolidBrush(Color.FromArgb(255, 62, 39, 35)))
                    {
                        g.FillRectangle(cup, 5, 11, 17, 13);
                        g.FillEllipse(dark, 6, 9, 15, 6);   /* 咖啡液面 */
                    }
                    using (Pen brown = new Pen(Color.FromArgb(255, 139, 90, 43), 2))
                    {
                        g.DrawArc(brown, 20, 12, 8, 10, -75, 150); /* 杯把 */
                    }
                    /* 杯碟 */
                    using (SolidBrush saucer = new SolidBrush(Color.FromArgb(255, 200, 170, 130)))
                    {
                        g.FillEllipse(saucer, 2, 24, 25, 5);
                    }
                    /* 蒸汽 */
                    using (Pen steam = new Pen(Color.FromArgb(255, 180, 200, 255), 2))
                    {
                        g.DrawBezier(steam, 10, 4, 8, 7, 12, 7, 10, 10);
                        g.DrawBezier(steam, 18, 2, 16, 6, 20, 6, 18, 10);
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    try { NativeMethods.DestroyIcon(hIcon); } catch (Exception) { }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_exiting)
                {
                    _exiting = true;
                    _config.QuotaHudVisible = _quotaHud != null && _quotaHud.Visible;
                    DisposeHudWindows();
                    SaveConfigQuietly();
                }
                try { _icon.Visible = false; _icon.Dispose(); } catch (Exception) { }
                DisposeServices();
                if (!_bridgeDisposed)
                {
                    try { _bridge.Dispose(); } catch (Exception) { }
                    _bridgeDisposed = true;
                }
            }
            base.Dispose(disposing);
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
