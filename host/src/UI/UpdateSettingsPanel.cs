using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using CodexToolsHost.Updates;

namespace CodexToolsHost.UI
{
    /// <summary>Only the tray owns the service. This panel subscribes while alive and opens pages on explicit clicks.</summary>
    public sealed class UpdateSettingsPanel : UserControl
    {
        private readonly ReleaseUpdateService _service;
        private readonly Action<bool> _saveEnabled;
        private readonly Action<Uri> _openPage;
        private readonly CheckBox _enabled = new CheckBox();
        private readonly Label _current = new Label();
        private readonly Label _latest = new Label();
        private readonly Label _status = new Label();
        private readonly Label _times = new Label();
        private readonly Button _check = new Button();
        private readonly Button _release = new Button();
        private bool _updating;

        public UpdateSettingsPanel(ReleaseUpdateService service, bool enabled, Action<bool> saveEnabled)
            : this(service, enabled, saveEnabled, OpenPage) { }

        internal UpdateSettingsPanel(ReleaseUpdateService service, bool enabled, Action<bool> saveEnabled, Action<Uri> openPage)
        {
            if (service == null) throw new ArgumentNullException("service");
            if (openPage == null) throw new ArgumentNullException("openPage");
            _service = service; _saveEnabled = saveEnabled; _openPage = openPage;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BuildLayout(enabled);
            _service.Changed += OnServiceChanged;
            UpdateView();
        }

        private void BuildLayout(bool enabled)
        {
            var root = new TableLayoutPanel {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, Padding = new Padding(10)
            };
            var heading = new Label { Text = "应用更新", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            root.Controls.Add(heading);
            foreach (Label label in new[] { _current, _latest, _status, _times })
            {
                label.AutoSize = true;
                label.MaximumSize = new Size(640, 0);
                label.Margin = new Padding(0, 0, 0, 7);
                root.Controls.Add(label);
            }
            _status.Tag = "updates-status";
            _times.ForeColor = Color.DimGray;
            _enabled.Text = "自动检查正式版本（启动后 30 秒，每 12 小时）";
            _enabled.AutoSize = true; _enabled.Checked = enabled; _enabled.Tag = "updates-enabled";
            _enabled.Margin = new Padding(0, 4, 0, 8);
            _enabled.CheckedChanged += PreferenceChanged;
            root.Controls.Add(_enabled);
            var buttons = new FlowLayoutPanel {
                AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0)
            };
            _check.Text = "立即检查"; _check.AutoSize = true; _check.Tag = "updates-check";
            _check.Click += async delegate
            {
                _check.Enabled = false;
                try { await _service.CheckAsync(); }
                catch (Exception) { if (!IsDisposed) _status.Text = "检查失败，请稍后重试"; }
                finally { if (!IsDisposed && !Disposing) UpdateView(); }
            };
            _release.Text = "打开 GitHub 发布页"; _release.AutoSize = true; _release.Tag = "updates-release";
            _release.Click += delegate
            {
                UpdateSnapshot snapshot = _service.Snapshot;
                Uri page;
                if (!ReleaseLink.TryGet(snapshot.ReleaseUrl, snapshot.LatestTag, out page)) return;
                try { _openPage(page); }
                catch (Exception) { _status.Text = "无法打开默认浏览器，请稍后重试"; }
            };
            buttons.Controls.Add(_check); buttons.Controls.Add(_release); root.Controls.Add(buttons);
            root.Controls.Add(new Label {
                Text = "公开 GitHub 免登录查询；下载和安装由你手动完成。",
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(640, 0)
            });
            Controls.Add(root);
        }

        private void PreferenceChanged(object sender, EventArgs args)
        {
            if (_updating) return;
            bool requested = _enabled.Checked;
            try
            {
                if (_saveEnabled != null) _saveEnabled(requested);
                _service.SetEnabled(requested);
                UpdateView();
            }
            catch (Exception)
            {
                _updating = true; _enabled.Checked = !requested; _updating = false;
                _status.Text = "更新偏好保存失败，请重试";
            }
        }

        protected override void OnHandleCreated(EventArgs args)
        {
            base.OnHandleCreated(args);
            UpdateView();
        }

        private void OnServiceChanged()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(UpdateView));
                else UpdateView();
            }
            catch (InvalidOperationException) { /* The window may close while the request completes. */ }
        }

        private void UpdateView()
        {
            if (IsDisposed || Disposing) return;
            UpdateSnapshot snapshot = _service.Snapshot;
            Version current = snapshot.CurrentVersion;
            _current.Text = "当前版本：" + current.Major + "." + current.Minor + "." + Math.Max(0, current.Build);
            bool fresh = snapshot.State == UpdateCheckState.UpToDate || snapshot.State == UpdateCheckState.UpdateAvailable;
            _latest.Text = snapshot.LatestTag == null ? "最新正式版本：尚无有效结果"
                : (fresh ? "最新正式版本：" : "上次成功记录：") + snapshot.LatestTag;
            _status.Text = snapshot.StatusMessage;
            _status.ForeColor = snapshot.State == UpdateCheckState.Failed ? Color.DarkRed : Color.DarkSlateGray;
            _times.Text = "最近检查：" + FormatTime(snapshot.LastAttemptUtc)
                + "    最近成功：" + FormatTime(snapshot.LastSuccessUtc)
                + (snapshot.NextCheckUtc.HasValue ? "\r\n下次自动检查：" + FormatTime(snapshot.NextCheckUtc) : "");
            _check.Enabled = snapshot.State != UpdateCheckState.Checking;
            Uri page;
            _release.Enabled = ReleaseLink.TryGet(snapshot.ReleaseUrl, snapshot.LatestTag, out page);
        }

        private static string FormatTime(DateTimeOffset? time)
        { return time.HasValue ? time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—"; }

        private static void OpenPage(Uri page)
        { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _service.Changed -= OnServiceChanged;
            base.Dispose(disposing);
        }
    }
}
