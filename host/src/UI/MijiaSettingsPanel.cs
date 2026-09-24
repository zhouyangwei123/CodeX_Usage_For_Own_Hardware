using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexToolsHost.Mijia;

namespace CodexToolsHost.UI
{
    /// <summary>米家 CLI 配置、二维码登录和八个场景快捷方式。</summary>
    public sealed class MijiaSettingsPanel : UserControl
    {
        private readonly MijiaSettings _settings;
        private readonly MijiaService _service;
        private readonly TextBox _executablePath = new TextBox();
        private readonly TextBox _authPath = new TextBox();
        private readonly Label _environment = new Label();
        private readonly Label _status = new Label();
        private readonly PictureBox _qrCode = new PictureBox();
        private readonly Label _qrStatus = new Label();
        private readonly CheckBox _autoRefresh = new CheckBox();
        private readonly ComboBox _refreshMinutes = new ComboBox();
        private Button _loginButton;
        private Button _refreshScenesButton;
        private readonly TextBox[] _shortcutNames = new TextBox[MijiaSettings.ShortcutCount];
        private readonly ComboBox[] _shortcutScenes = new ComboBox[MijiaSettings.ShortcutCount];
        private readonly Button[] _executeButtons = new Button[MijiaSettings.ShortcutCount];
        private bool _loading;

        public MijiaSettingsPanel(MijiaSettings settings, MijiaService service)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (service == null) throw new ArgumentNullException("service");
            _settings = settings;
            _service = service;
            _settings.Shortcuts = MijiaSettings.NormalizeShortcuts(_settings.Shortcuts);

            BuildLayout();
            LoadSettings();
            _service.ScenesChanged += OnServiceChanged;
            _service.StateChanged += OnServiceChanged;
            _service.LoginChanged += OnServiceChanged;
            _service.QrCodeUrlChanged += OnQrCodeUrlChanged;
            UpdateView();
        }

        public void ApplySettings()
        {
            CaptureSettings();
            _service.ApplySettings();
            UpdateView();
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 1,
                RowCount = 3
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var configuration = new TableLayoutPanel { AutoSize = true,
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            configuration.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var programRow = new FlowLayoutPanel { AutoSize = true,
                Dock = DockStyle.Fill, WrapContents = false };
            programRow.Controls.Add(new Label { Text = "环境：", AutoSize = true,
                Padding = new Padding(0, 6, 0, 0) });
            _environment.AutoSize = false;
            _environment.Width = 175;
            _environment.Height = 28;
            _environment.AutoEllipsis = true;
            _environment.TextAlign = ContentAlignment.MiddleLeft;
            _environment.ForeColor = Color.DarkSlateGray;
            programRow.Controls.Add(_environment);
            programRow.Controls.Add(new Label { Text = "程序：", AutoSize = true,
                Padding = new Padding(0, 6, 0, 0) });
            _executablePath.Width = 190;
            programRow.Controls.Add(_executablePath);
            Button pickExecutable = new Button { Text = "选择程序", AutoSize = true };
            pickExecutable.Click += delegate { SelectFile(_executablePath, "mijiaAPI.exe|mijiaAPI.exe|程序|*.exe"); };
            programRow.Controls.Add(pickExecutable);
            var authRow = new FlowLayoutPanel { AutoSize = true,
                Dock = DockStyle.Fill, WrapContents = false };
            authRow.Controls.Add(new Label { Text = "认证：", AutoSize = true,
                Padding = new Padding(0, 6, 0, 0) });
            _authPath.Width = 160;
            authRow.Controls.Add(_authPath);
            Button pickAuth = new Button { Text = "选择认证文件", AutoSize = true };
            pickAuth.Click += delegate { SelectFile(_authPath, "JSON 文件|*.json|所有文件|*.*"); };
            authRow.Controls.Add(pickAuth);
            configuration.Controls.Add(programRow, 0, 0);
            configuration.Controls.Add(authRow, 0, 1);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.Controls.Add(BuildQrPanel(), 0, 0);
            split.Controls.Add(BuildShortcutsPanel(), 1, 0);

            _status.AutoSize = true;
            _status.ForeColor = Color.DimGray;
            root.Controls.Add(configuration, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(_status, 0, 2);
            Controls.Add(root);
        }

        private Control BuildQrPanel()
        {
            var group = new GroupBox { Text = "二维码登录", Dock = DockStyle.Fill };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70 };
            _loginButton = new Button { Text = "生成登录二维码", AutoSize = true,
                Tag = "mijia-login" };
            _loginButton.Click += delegate
            {
                CaptureSettings();
                _service.ApplySettings();
                _service.StartLogin();
                UpdateView();
            };
            _qrStatus.AutoSize = true;
            _qrStatus.MaximumSize = new Size(210, 0);
            bottom.Controls.Add(_loginButton);
            bottom.Controls.Add(_qrStatus);
            _qrCode.Dock = DockStyle.Fill;
            _qrCode.SizeMode = PictureBoxSizeMode.Zoom;
            _qrCode.BackColor = Color.White;
            _qrCode.Tag = "mijia-qr-image";
            group.Controls.Add(_qrCode);
            group.Controls.Add(bottom);
            return group;
        }

        private Control BuildShortcutsPanel()
        {
            var group = new GroupBox { Text = "场景快捷方式", Dock = DockStyle.Fill };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var refreshBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            _autoRefresh.Text = "自动刷新场景";
            _autoRefresh.AutoSize = true;
            _autoRefresh.Tag = "mijia-auto-refresh";
            _autoRefresh.CheckedChanged += delegate { UpdateRefreshMinutesEnabled(); };
            _refreshMinutes.DropDownStyle = ComboBoxStyle.DropDownList;
            _refreshMinutes.Width = 70;
            _refreshMinutes.Tag = "mijia-refresh-minutes";
            foreach (int minutes in MijiaSettings.RefreshIntervalMinutes)
                _refreshMinutes.Items.Add(minutes + " 分钟");
            _refreshScenesButton = new Button { Text = "刷新场景", AutoSize = true,
                Tag = "mijia-refresh-scenes" };
            _refreshScenesButton.Click += delegate
            {
                ApplySettings();
                Task refresh = _service.RefreshScenesAsync();
                Observe(refresh, "刷新场景失败");
            };
            refreshBar.Controls.Add(_autoRefresh);
            refreshBar.Controls.Add(_refreshMinutes);
            refreshBar.Controls.Add(_refreshScenesButton);

            var rows = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4,
                RowCount = MijiaSettings.ShortcutCount,
                Height = MijiaSettings.ShortcutCount * 29 + 4 };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            for (int index = 0; index < MijiaSettings.ShortcutCount; index++)
            {
                int shortcutIndex = index;
                rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
                rows.Controls.Add(new Label { Text = (index + 1).ToString(), AutoSize = true,
                    TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(2, 6, 0, 0) }, 0, index);
                var name = new TextBox { Dock = DockStyle.Fill };
                var scene = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
                    DisplayMember = "Name" };
                var execute = new Button { Text = "执行", Dock = DockStyle.Fill, Tag = "mijia-execute" };
                execute.Click += delegate
                {
                    ApplySettings();
                    Task run = _service.ExecuteShortcutAsync(shortcutIndex);
                    Observe(run, "执行米家场景失败");
                };
                scene.SelectedIndexChanged += delegate
                {
                    if (_loading) return;
                    MijiaScene selected = scene.SelectedItem as MijiaScene;
                    _settings.Shortcuts[shortcutIndex].SceneId = selected == null ? "" : selected.Id;
                    _settings.Shortcuts[shortcutIndex].SceneName = selected == null ? "" : selected.Name;
                };
                _shortcutNames[index] = name;
                _shortcutScenes[index] = scene;
                _executeButtons[index] = execute;
                rows.Controls.Add(name, 1, index);
                rows.Controls.Add(scene, 2, index);
                rows.Controls.Add(execute, 3, index);
            }
            root.Controls.Add(refreshBar, 0, 0);
            var rowScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            rowScroll.Controls.Add(rows);
            root.Controls.Add(rowScroll, 0, 1);
            group.Controls.Add(root);
            return group;
        }

        private void LoadSettings()
        {
            _loading = true;
            _executablePath.Text = _settings.ExecutablePath ?? "";
            _authPath.Text = _settings.AuthPath ?? "";
            _autoRefresh.Checked = _settings.AutoRefresh;
            SelectRefreshMinutes();
            for (int index = 0; index < MijiaSettings.ShortcutCount; index++)
                _shortcutNames[index].Text = _settings.Shortcuts[index].Name;
            _loading = false;
            PopulateScenes();
        }

        private void CaptureSettings()
        {
            _settings.ExecutablePath = _executablePath.Text.Trim();
            _settings.AuthPath = _authPath.Text.Trim();
            _settings.AutoRefresh = _autoRefresh.Checked;
            _settings.RefreshMinutes = SelectedRefreshMinutes();
            for (int index = 0; index < MijiaSettings.ShortcutCount; index++)
            {
                MijiaShortcutConfig shortcut = _settings.Shortcuts[index];
                shortcut.Name = _shortcutNames[index].Text.Trim();
                MijiaScene selected = _shortcutScenes[index].SelectedItem as MijiaScene;
                if (selected == null) continue;
                shortcut.SceneId = selected.Id;
                if (string.IsNullOrEmpty(selected.Id)) shortcut.SceneName = "";
                else if (SceneExists(selected.Id)) shortcut.SceneName = selected.Name;
            }
        }

        private void PopulateScenes()
        {
            _loading = true;
            for (int index = 0; index < MijiaSettings.ShortcutCount; index++)
            {
                ComboBox combo = _shortcutScenes[index];
                string selectedId = _settings.Shortcuts[index].SceneId;
                combo.Items.Clear();
                combo.Items.Add(new MijiaScene("", "未绑定"));
                foreach (MijiaScene scene in _service.Scenes)
                    combo.Items.Add(new MijiaScene(scene.Id, scene.Name));
                combo.SelectedIndex = 0;
                bool found = string.IsNullOrEmpty(selectedId);
                for (int item = 1; item < combo.Items.Count; item++)
                {
                    MijiaScene scene = combo.Items[item] as MijiaScene;
                    if (scene != null && scene.Id == selectedId)
                    {
                        combo.SelectedIndex = item;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    string name = _settings.Shortcuts[index].SceneName;
                    combo.Items.Add(new MijiaScene(selectedId,
                        string.IsNullOrEmpty(name) ? "已保存场景（当前不可用）"
                        : name + "（当前不可用）"));
                    combo.SelectedIndex = combo.Items.Count - 1;
                }
            }
            _loading = false;
        }

        private bool SceneExists(string id)
        {
            foreach (MijiaScene scene in _service.Scenes)
                if (scene.Id == id) return true;
            return false;
        }

        private void UpdateView()
        {
            if (IsDisposed) return;
            PopulateScenes();
            bool environmentReady = _service.EnvironmentReady;
            _environment.Text = _service.EnvironmentStatus;
            _environment.ForeColor = environmentReady ? Color.DarkGreen : Color.DarkOrange;
            bool shortcutsEnabled = environmentReady && !_service.IsBusy
                && !_service.LoginInProgress;
            _autoRefresh.Enabled = environmentReady && !_service.LoginInProgress;
            UpdateRefreshMinutesEnabled();
            _refreshScenesButton.Enabled = _service.CanRefreshScenes && !_service.IsBusy
                && !_service.LoginInProgress;
            _loginButton.Text = _service.CanRefreshLogin
                ? "刷新二维码" : "生成登录二维码";
            _loginButton.Enabled = _service.CanStartLogin || _service.CanRefreshLogin;
            for (int index = 0; index < MijiaSettings.ShortcutCount; index++)
            {
                _shortcutNames[index].Enabled = shortcutsEnabled;
                _shortcutScenes[index].Enabled = shortcutsEnabled;
                _executeButtons[index].Enabled = shortcutsEnabled
                    && _service.IsShortcutAvailable(index);
            }
            _status.Text = !string.IsNullOrEmpty(_service.LastError) ? _service.LastError
                : _service.IsRefreshing ? "正在刷新米家场景…"
                : _service.LoginInProgress ? "等待扫描二维码登录…"
                : environmentReady ? "场景已准备好" : "配置程序和认证文件后可刷新场景";
            _qrStatus.Text = string.IsNullOrEmpty(_service.QrCodeUrl)
                ? "二维码仅在手动点击后生成。" : "请使用米家扫码；登录完成后会自动验证。";
        }

        private void OnServiceChanged(object sender, EventArgs args)
        {
            SafeUi(UpdateView);
        }

        private void OnQrCodeUrlChanged(object sender, EventArgs args)
        {
            SafeUi(delegate
            {
                LoadCurrentQrCode();
                UpdateView();
            });
        }

        private void LoadCurrentQrCode()
        {
            string url = _service.QrCodeUrl;
            if (string.IsNullOrEmpty(url))
            {
                ReplaceQrImage(null);
                return;
            }
            Task download = DownloadQrImageAsync(url);
            Observe(download, "二维码下载失败");
        }

        private async Task DownloadQrImageAsync(string url)
        {
            try
            {
                byte[] data;
                using (var client = new WebClient())
                    data = await client.DownloadDataTaskAsync(new Uri(url));
                if (IsDisposed || !string.Equals(url, _service.QrCodeUrl,
                    StringComparison.Ordinal)) return;
                using (var stream = new MemoryStream(data))
                using (Image image = Image.FromStream(stream))
                    ReplaceQrImage(new Bitmap(image));
            }
            catch (Exception ex)
            {
                if (!IsDisposed) _qrStatus.Text = "二维码下载失败：" + ex.Message;
            }
        }

        private void ReplaceQrImage(Image image)
        {
            Image old = _qrCode.Image;
            _qrCode.Image = image;
            if (old != null) old.Dispose();
        }

        private void Observe(Task task, string failurePrefix)
        {
            if (task == null) return;
            task.ContinueWith(delegate(Task failed)
            {
                AggregateException error = failed.Exception;
                SafeUi(delegate { _status.Text = failurePrefix + ": "
                    + (error == null ? "未知错误" : error.GetBaseException().Message); });
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void SafeUi(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (Exception) { }
        }

        private void SelectRefreshMinutes()
        {
            int minutes = MijiaSettings.NormalizeRefreshMinutes(_settings.RefreshMinutes);
            for (int index = 0; index < MijiaSettings.RefreshIntervalMinutes.Length; index++)
            {
                if (MijiaSettings.RefreshIntervalMinutes[index] == minutes)
                {
                    _refreshMinutes.SelectedIndex = index;
                    return;
                }
            }
            _refreshMinutes.SelectedIndex = 1;
        }

        private int SelectedRefreshMinutes()
        {
            int index = _refreshMinutes.SelectedIndex;
            return index >= 0 && index < MijiaSettings.RefreshIntervalMinutes.Length
                ? MijiaSettings.RefreshIntervalMinutes[index] : 15;
        }

        private void UpdateRefreshMinutesEnabled()
        {
            _refreshMinutes.Enabled = _service.EnvironmentReady && _autoRefresh.Checked
                && !_service.LoginInProgress;
        }

        private static void SelectFile(TextBox target, string filter)
        {
            using (var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = false })
            {
                if (dialog.ShowDialog() == DialogResult.OK) target.Text = dialog.FileName;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _service.ScenesChanged -= OnServiceChanged;
                _service.StateChanged -= OnServiceChanged;
                _service.LoginChanged -= OnServiceChanged;
                _service.QrCodeUrlChanged -= OnQrCodeUrlChanged;
                ReplaceQrImage(null);
            }
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LoadCurrentQrCode();
        }
    }
}
