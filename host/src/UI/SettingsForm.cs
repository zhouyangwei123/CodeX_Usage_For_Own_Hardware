using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Mijia;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;

namespace CodexToolsHost.UI
{
    public sealed class SettingsForm : Form
    {
        private static readonly string[] ActionNames = new string[]
        {
            "none","hotkey","text","launch","focus",
            "launchCodex",
            "restartChatgpt",
            "volumeUp","volumeDown","volumeMute",
            "mediaPlayPause","mediaNext","mediaPrev",
            "scrollUp","scrollDown","cycleOled",
            "rgbStatus","rgbSolid","rgbBreath","rgbRainbow","rgbWave","rgbBlink","rgbOff",
            "refreshQuota","showSettings","toggleDeepSeek",
            "mijia1","mijia2","mijia3","mijia4","mijia5","mijia6","mijia7","mijia8"
        };

        private static readonly string[] ActionNamesZh = new string[]
        {
            "无","快捷键","输入文本","启动程序","聚焦窗口",
            "启动 CodeX",
            "重启 ChatGPT APP",
            "音量加","音量减","静音",
            "播放/暂停","下一曲","上一曲",
            "上滚","下滚","切换 OLED 页",
            "RGB 状态色","RGB 常亮","RGB 呼吸","RGB 彩虹","RGB 波动","RGB 闪烁","RGB 关闭",
            "刷新额度","打开设置","切换 DeepSeek/官方账号",
            "米家快捷1","米家快捷2","米家快捷3","米家快捷4",
            "米家快捷5","米家快捷6","米家快捷7","米家快捷8"
        };

        private static readonly string[] EncActionsZh = new string[]
        {
            "音量","滚动","快捷键","切换 OLED 页","无"
        };

        private static readonly string[] EncActions = new string[]
        {
            "volume","scroll","hotkey","cycleOled","none"
        };

        private static readonly string[] RgbModesZh = new string[]
        {
            "状态色","常亮","呼吸","彩虹","波动","闪烁","测试","关闭"
        };

        private static readonly string[] RgbModes = new string[]
        {
            "status","solid","breath","rainbow","wave","blink","test","off"
        };

        private static readonly string[] ApiProvidersZh = new string[]
        {
            "DeepSeek", "硅基流动", "OpenRouter", "自定义"
        };

        private static readonly string[] ApiProvidersEn = CodexToolsHost.Quota.ApiBalanceProvider.KnownProviders;

        private static string ZhToEn(string zh, string[] zhList, string[] enList, string fallback)
        {
            for (int i = 0; i < zhList.Length; i++)
                if (zhList[i] == zh) return enList[i];
            return fallback;
        }

        private static string EnToZh(string en, string[] zhList, string[] enList)
        {
            for (int i = 0; i < enList.Length; i++)
                if (enList[i] == en) return zhList[i];
            return zhList.Length > 0 ? zhList[0] : "";
        }

        private readonly AppConfig _config;
        private readonly BridgeService _bridge;
        private TabControl _tabs;
        private DataGridView _grid;
        private ComboBox _encRotateAction;
        private TextBox _encRotateParam;
        private TextBox _dsKey;
        private TextBox _dsUrl;
        private ComboBox _dsProvider;
        private NumericUpDown _dsRefresh;
        private Label _dsTestResult;
        private ComboBox _quotaDisplaySource;
        private TextBox _goKey;
        private NumericUpDown _goRefresh;
        private Label _goTestResult;
        private Label _goRollingValue;
        private Label _goWeeklyValue;
        private Label _goMonthlyValue;
        private readonly Timer _goDisplayTimer = new Timer();
        private ComboBox _pageCombo;
        private ComboBox _serialPortCombo;
        private ComboBox _rgbMode;
        private NumericUpDown _rgbBrightness;
        private NumericUpDown _rgbPeriod;
        private NumericUpDown _rgbCount;
        private TextBox _rgbColor1;
        private Button _pickColor1;
        private TextBox _rgbStatusIdle;
        private TextBox _rgbStatusRunning;
        private TextBox _rgbStatusWaiting;
        private TextBox _rgbStatusError;
        private TextBox _rgbStatusComplete;
        private TextBox _rgbStatusOffline;
        private Label _portStatusLabel;
        private Label _pcCpuValue;
        private Label _pcGpuValue;
        private Label _pcMemoryValue;
        private Label _pcTemperatureValue;
        private Label _pcGpuTemperatureValue;
        private Label _pcMotherboardTemperatureValue;
        private Label _pcNetworkValue;
        private Label _pcTemperatureSourceValue;
        private Label _pcUpdatedValue;
        private Label _pcStateValue;
        private ComboBox _deepSeekModelCombo;
        private Label _deepSeekStateValue;
        private Label _deepSeekPathValue;
        private Label _deepSeekResult;
        private MijiaSettingsPanel _mijiaPanel;

        public SettingsForm(AppConfig config, BridgeService bridge)
        {
            _config = config;
            _bridge = bridge;
            Text = "CodeX Tools 设置";
            Width = 760;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;

            BuildTabs();
            BuildBottom();
            _bridge.PcMetricsChanged += SafeUpdatePcMetrics;
            _bridge.DeepSeek.Changed += OnDeepSeekChanged;
            _bridge.OpenCodeGo.Changed += OnOpenCodeGoChanged;
            LoadConfig();
            _goDisplayTimer.Interval = 1000;
            _goDisplayTimer.Tick += delegate { UpdateOpenCodeGoDetails(); };
            _goDisplayTimer.Start();
        }

        private void BuildTabs()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill };

            _tabs.TabPages.Add(BuildButtonsTab());
            _tabs.TabPages.Add(BuildEncoderTab());
            _tabs.TabPages.Add(BuildApiBalanceTab());
            _tabs.TabPages.Add(BuildDeepSeekTab());
            _tabs.TabPages.Add(BuildDisplayTab());
            _tabs.TabPages.Add(BuildMonitorTab());
            _tabs.TabPages.Add(BuildMijiaTab());
            _tabs.TabPages.Add(BuildAboutTab());

            Controls.Add(_tabs);
        }

        private TabPage BuildMijiaTab()
        {
            TabPage page = new TabPage("米家控制");
            _mijiaPanel = new MijiaSettingsPanel(_config.Mijia, _bridge.Mijia);
            _mijiaPanel.Dock = DockStyle.Fill;
            page.Controls.Add(_mijiaPanel);
            return page;
        }

        private TabPage BuildButtonsTab()
        {
            TabPage page = new TabPage("按键");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label
            {
                Text = "按键单击绑定：btn0~btn7 对应 KEY1~KEY8，enc_ 为编码器按键。动作可随时修改，保存后立即下发。\r\n" +
                       "提示：启动 CodeX＝从后台唤起 Codex 完整界面；快捷键＝按参数中的组合键，支持 Win+D、Ctrl+Shift+K、\r\n" +
                       "Win+Alt+Del 等常见写法（Win=Windows 键）。Ctrl+Alt+Del 属系统安全组合键，程序无法注入。",
                AutoSize = true
            }, 0, 0);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            _grid.Columns.Add("key", "绑定键");
            _grid.Columns.Add("evt", "事件");
            var actionCol = new DataGridViewComboBoxColumn { Name = "action", HeaderText = "动作" };
            foreach (string a in ActionNamesZh) actionCol.Items.Add(a);
            _grid.Columns.Add(actionCol);
            _grid.Columns.Add("param", "参数");
            _grid.Columns["key"].ReadOnly = true;
            _grid.Columns["evt"].ReadOnly = true;
            _grid.Columns["action"].FillWeight = 40;
            _grid.Columns["param"].FillWeight = 40;
            layout.Controls.Add(_grid, 0, 1);

            Button reset = new Button { Text = "恢复默认绑定", Dock = DockStyle.Right };
            reset.Click += delegate { LoadBindingsToGrid(AppConfig.CreateDefault()); };
            layout.Controls.Add(reset, 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildEncoderTab()
        {
            TabPage page = new TabPage("编码器");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12), RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

            layout.Controls.Add(new Label { Text = "旋转动作：", TextAlign = ContentAlignment.MiddleRight }, 0, 0);
            _encRotateAction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _encRotateAction.Items.AddRange(EncActionsZh);
            layout.Controls.Add(_encRotateAction, 1, 0);
            _encRotateParam = new TextBox();
            layout.Controls.Add(_encRotateParam, 2, 0);

            layout.Controls.Add(new Label
            {
                Text = "旋转参数：音量/滚动填步数（默认 1）；快捷键填组合键如 Ctrl+Alt+Up；\r\n每格（detent）触发一次动作。编码器按键请在“按键”页绑定 enc_click / enc_double / enc_long。",
                AutoSize = true,
                ForeColor = Color.Gray
            }, 1, 2);
            layout.SetColumnSpan(layout.GetControlFromPosition(1, 2), 2);
            layout.Controls.Add(new Label
            {
                Text = "快捷键示例：Win+D 显示桌面、Win+Alt+Del 锁定/安全选项、Ctrl+Alt+Up 等。",
                AutoSize = true,
                ForeColor = Color.Gray
            }, 1, 3);
            layout.SetColumnSpan(layout.GetControlFromPosition(1, 3), 2);
            page.Controls.Add(layout);
            return page;
        }

        private void RefreshSerialPortList(string requestedPort)
        {
            if (_serialPortCombo == null) return;
            string desired = SerialPortSelection.Normalize(requestedPort);
            var values = new List<string> { SerialPortSelection.Auto };
            try
            {
                string[] detected = SerialPort.GetPortNames();
                foreach (string port in SerialPortSelection.OrderCandidates(detected, null))
                {
                    string normalized = SerialPortSelection.Normalize(port);
                    if (!SerialPortSelection.IsAuto(normalized)
                        && !values.Contains(normalized))
                        values.Add(normalized);
                }
            }
            catch (Exception) { }

            if (!SerialPortSelection.IsAuto(desired) && !values.Contains(desired))
                values.Add(desired);

            _serialPortCombo.Items.Clear();
            foreach (string value in values) _serialPortCombo.Items.Add(ToPortDisplay(value));
            int selected = values.FindIndex(delegate(string value)
            {
                return string.Equals(value, desired, StringComparison.OrdinalIgnoreCase);
            });
            _serialPortCombo.SelectedIndex = selected >= 0 ? selected : 0;
        }

        private string GetSelectedSerialPort()
        {
            if (_serialPortCombo == null || _serialPortCombo.SelectedItem == null)
                return _config.SerialPort;
            return FromPortDisplay(_serialPortCombo.SelectedItem);
        }

        private static string ToPortDisplay(string value)
        {
            return SerialPortSelection.IsAuto(value) ? "自动（协议握手）" : value;
        }

        private static string FromPortDisplay(object value)
        {
            string text = Convert.ToString(value) ?? "";
            return text.StartsWith("自动（协议握手）", StringComparison.Ordinal)
                ? SerialPortSelection.Auto : SerialPortSelection.Normalize(text);
        }

        private TabPage BuildApiBalanceTab()
        {
            TabPage page = new TabPage("API 余额");
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                Padding = new Padding(12),
                RowCount = 15,
                AutoScroll = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

            layout.Controls.Add(new Label { Text = "血条显示来源：", TextAlign = ContentAlignment.MiddleRight }, 0, 0);
            _quotaDisplaySource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _quotaDisplaySource.Items.Add("DeepSeek 官方余额");
            _quotaDisplaySource.Items.Add("OpenCode Go 额度");
            layout.Controls.Add(_quotaDisplaySource, 1, 0);
            layout.Controls.Add(new Label
            {
                Text = "合并悬浮窗与本页使用此选择",
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft
            }, 2, 0);

            layout.Controls.Add(new Label { Text = "厂商/预设：", TextAlign = ContentAlignment.MiddleRight }, 0, 1);
            _dsProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _dsProvider.Items.AddRange(ApiProvidersZh);
            _dsProvider.SelectedIndexChanged += delegate { FillProviderPresetUrl(); };
            layout.Controls.Add(_dsProvider, 1, 1);
            layout.Controls.Add(new Label
            {
                Text = "切换厂商自动填入官方查询地址",
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft
            }, 2, 1);

            layout.Controls.Add(new Label { Text = "API Key：", TextAlign = ContentAlignment.MiddleRight }, 0, 2);
            _dsKey = new TextBox { UseSystemPasswordChar = true };
            layout.Controls.Add(_dsKey, 1, 2);
            layout.Controls.Add(new Label
            {
                Text = "仅保存在本机配置文件中",
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft
            }, 2, 2);

            layout.Controls.Add(new Label { Text = "查询地址：", TextAlign = ContentAlignment.MiddleRight }, 0, 3);
            _dsUrl = new TextBox { Text = "https://api.deepseek.com" };
            layout.Controls.Add(_dsUrl, 1, 3);

            layout.Controls.Add(new Label { Text = "刷新间隔(秒)：", TextAlign = ContentAlignment.MiddleRight }, 0, 4);
            _dsRefresh = new NumericUpDown { Minimum = 30, Maximum = 86400, Value = 300 };
            layout.Controls.Add(_dsRefresh, 1, 4);

            Button test = new Button { Text = "测试余额" };
            test.Click += delegate
            {
                try
                {
                    var provider = new CodexToolsHost.Quota.ApiBalanceProvider(
                        _dsKey.Text.Trim(), _dsUrl.Text.Trim(), 300, CurrentApiProvider());
                    string result = provider.Fetch();
                    _dsTestResult.Text = "测试结果: " + result;
                }
                catch (Exception ex)
                {
                    _dsTestResult.Text = "测试失败: " + ex.Message;
                }
            };
            layout.Controls.Add(test, 2, 4);

            _dsTestResult = new Label { AutoSize = true, ForeColor = Color.Gray };
            layout.Controls.Add(_dsTestResult, 1, 5);
            layout.SetColumnSpan(_dsTestResult, 2);

            layout.Controls.Add(new Label { Text = "OpenCode Go Key：", TextAlign = ContentAlignment.MiddleRight }, 0, 6);
            _goKey = new TextBox { UseSystemPasswordChar = true };
            layout.Controls.Add(_goKey, 1, 6);
            layout.Controls.Add(new Label
            {
                Text = "在 OpenCode 控制台生成的 Go API Key",
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft
            }, 2, 6);

            layout.Controls.Add(new Label { Text = "Go 查询地址：", TextAlign = ContentAlignment.MiddleRight }, 0, 7);
            Label goEndpoint = new Label
            {
                Text = OpenCodeGoQuotaProvider.UsageEndpoint,
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            layout.Controls.Add(goEndpoint, 1, 7);
            layout.SetColumnSpan(goEndpoint, 2);

            layout.Controls.Add(new Label { Text = "Go 刷新间隔(秒)：", TextAlign = ContentAlignment.MiddleRight }, 0, 8);
            _goRefresh = new NumericUpDown { Minimum = 30, Maximum = 86400, Value = 300 };
            layout.Controls.Add(_goRefresh, 1, 8);
            Button testGo = new Button { Text = "测试 Go 额度" };
            testGo.Click += delegate
            {
                try
                {
                    using (var provider = new OpenCodeGoQuotaProvider(
                        _goKey.Text.Trim(), (int)_goRefresh.Value))
                    {
                        string testResult = provider.Fetch();
                        _goTestResult.Text = "测试结果: " + testResult + "\r\n"
                            + QuotaHudPresentation.FormatOpenCodeGoSummary(
                                provider.Quota, DateTimeOffset.UtcNow);
                    }
                }
                catch (Exception ex)
                {
                    _goTestResult.Text = "测试失败: " + ex.Message;
                }
            };
            layout.Controls.Add(testGo, 2, 8);

            _goTestResult = new Label { AutoSize = true, ForeColor = Color.Gray, Text = "--" };
            layout.Controls.Add(_goTestResult, 1, 9);
            layout.SetColumnSpan(_goTestResult, 2);

            layout.Controls.Add(new Label { Text = "5h：", TextAlign = ContentAlignment.MiddleRight }, 0, 10);
            _goRollingValue = CreateMonitorValueLabel();
            layout.Controls.Add(_goRollingValue, 1, 10);
            layout.SetColumnSpan(_goRollingValue, 2);

            layout.Controls.Add(new Label { Text = "7d：", TextAlign = ContentAlignment.MiddleRight }, 0, 11);
            _goWeeklyValue = CreateMonitorValueLabel();
            layout.Controls.Add(_goWeeklyValue, 1, 11);
            layout.SetColumnSpan(_goWeeklyValue, 2);

            layout.Controls.Add(new Label { Text = "月：", TextAlign = ContentAlignment.MiddleRight }, 0, 12);
            _goMonthlyValue = CreateMonitorValueLabel();
            layout.Controls.Add(_goMonthlyValue, 1, 12);
            layout.SetColumnSpan(_goMonthlyValue, 2);

            layout.Controls.Add(new Label
            {
                Text = "DeepSeek：GET /user/balance；硅基流动：GET /v1/user/info；OpenRouter：GET /v1/key。\r\n" +
                       "均使用 Bearer 鉴权，余额仅发送到您填写的接口地址；OpenRouter 无上限额度显示为“无限”。",
                AutoSize = true,
                ForeColor = Color.Gray
            }, 1, 13);
            layout.SetColumnSpan(layout.GetControlFromPosition(1, 13), 2);

            layout.Controls.Add(new Label
            {
                Text = "OpenCode Go 返回 5h / 7d / 月度百分比；“余”表示剩余比例，5h→后为滚动窗口重置倒计时。\r\n" +
                       "自定义：请填写返回余额 JSON 的完整接口地址，支持 total_balance / totalBalance / limit_remaining / balance 等字段。",
                AutoSize = true,
                ForeColor = Color.Gray
            }, 1, 14);
            layout.SetColumnSpan(layout.GetControlFromPosition(1, 14), 2);
            page.Controls.Add(layout);
            return page;
        }

        private string CurrentApiProvider()
        {
            int idx = _dsProvider == null ? 0 : _dsProvider.SelectedIndex;
            if (idx < 0 || idx >= ApiProvidersEn.Length) idx = 0;
            return ApiProvidersEn[idx];
        }

        private string CurrentQuotaDisplaySource()
        {
            return _quotaDisplaySource != null && _quotaDisplaySource.SelectedIndex == 1
                ? "opencodego" : "deepseek";
        }

        private void FillProviderPresetUrl()
        {
            if (_dsUrl == null) return;
            string current = _dsUrl.Text.Trim();
            bool isPreset = string.IsNullOrEmpty(current);
            foreach (string p in ApiProvidersEn)
            {
                string preset = CodexToolsHost.Quota.ApiBalanceProvider.DefaultBaseUrl(p);
                if (string.Equals(current, preset, StringComparison.OrdinalIgnoreCase))
                {
                    isPreset = true;
                    break;
                }
            }
            if (isPreset)
                _dsUrl.Text = CodexToolsHost.Quota.ApiBalanceProvider.DefaultBaseUrl(CurrentApiProvider());
        }

        private TabPage BuildDeepSeekTab()
        {
            TabPage page = new TabPage("DeepSeek 切换");
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 8,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label introduction = new Label
            {
                Text = "遵循 DeepSeek 官方 Codex 配置流程。配置由 Codex CLI、桌面端和 VS Code 共享；切换后请使用下方按钮重启 ChatGPT APP。\r\n" +
                       "首次使用请启动官方配置流程；本页不读取或显示 API Key，密钥仅由 .codex 配置目录管理。",
                AutoSize = true,
                ForeColor = Color.Gray
            };
            layout.Controls.Add(introduction, 0, 0);
            layout.SetColumnSpan(introduction, 2);

            layout.Controls.Add(new Label { Text = "当前状态：", TextAlign = ContentAlignment.MiddleRight }, 0, 1);
            _deepSeekStateValue = new Label { AutoSize = true, Text = "--", TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(_deepSeekStateValue, 1, 1);

            layout.Controls.Add(new Label { Text = "DeepSeek 模型：", TextAlign = ContentAlignment.MiddleRight }, 0, 2);
            _deepSeekModelCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 220
            };
            _deepSeekModelCombo.Items.Add("DeepSeek V4 Flash");
            _deepSeekModelCombo.Items.Add("DeepSeek V4 Pro");
            _deepSeekModelCombo.SelectedIndex = 0;
            layout.Controls.Add(_deepSeekModelCombo, 1, 2);

            layout.Controls.Add(new Label { Text = "Codex 配置目录：", TextAlign = ContentAlignment.MiddleRight }, 0, 3);
            _deepSeekPathValue = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "--",
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            layout.Controls.Add(_deepSeekPathValue, 1, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 4)
            };
            Button activate = new Button { Text = "切换到 DeepSeek", Width = 120, Height = 30 };
            activate.Click += delegate { ShowDeepSeekResult(_bridge.DeepSeekConfig.ActivateDeepSeek(GetSelectedDeepSeekModel())); };
            Button restore = new Button { Text = "恢复官方账号", Width = 110, Height = 30 };
            restore.Click += delegate { ShowDeepSeekResult(_bridge.DeepSeekConfig.RestoreOfficial()); };
            Button setup = new Button { Text = "启动官方配置流程", Width = 130, Height = 30 };
            setup.Click += delegate { ShowDeepSeekResult(_bridge.DeepSeekConfig.LaunchOfficialSetup()); };
            Button restart = new Button { Text = "重启 ChatGPT APP", Width = 130, Height = 30 };
            restart.Click += delegate { _bridge.RestartChatGpt(); };
            Button refresh = new Button { Text = "刷新状态", Width = 80, Height = 30 };
            refresh.Click += delegate { RefreshDeepSeekPage(); };
            buttons.Controls.Add(activate);
            buttons.Controls.Add(restore);
            buttons.Controls.Add(setup);
            buttons.Controls.Add(restart);
            buttons.Controls.Add(refresh);
            layout.Controls.Add(buttons, 0, 4);
            layout.SetColumnSpan(buttons, 2);

            _deepSeekResult = new Label { AutoSize = true, ForeColor = Color.Gray, Text = "--" };
            layout.Controls.Add(_deepSeekResult, 0, 5);
            layout.SetColumnSpan(_deepSeekResult, 2);

            Label note = new Label
            {
                Text = "官方配置完成后点击“刷新状态”；恢复官方账号不会删除官方备份，历史会话也不会被本工具删除。",
                AutoSize = true,
                ForeColor = Color.Gray
            };
            layout.Controls.Add(note, 0, 6);
            layout.SetColumnSpan(note, 2);

            page.Controls.Add(layout);
            return page;
        }

        private string GetSelectedDeepSeekModel()
        {
            return _deepSeekModelCombo != null && _deepSeekModelCombo.SelectedIndex == 1
                ? DeepSeekConfigManager.ProModel : DeepSeekConfigManager.FlashModel;
        }

        private void ShowDeepSeekResult(DeepSeekActionResult result)
        {
            RefreshDeepSeekPage();
            if (_deepSeekResult != null && result != null)
                _deepSeekResult.Text = result.Message;
        }

        private void RefreshDeepSeekPage()
        {
            if (_deepSeekStateValue == null) return;
            try
            {
                DeepSeekConfigStatus status = _bridge.DeepSeekConfig.ReadStatus();
                switch (status.State)
                {
                    case DeepSeekConfigState.DeepSeekActive:
                        _deepSeekStateValue.Text = "DeepSeek API · "
                            + (status.Model == DeepSeekConfigManager.ProModel ? "V4 Pro" : "V4 Flash");
                        break;
                    case DeepSeekConfigState.OfficialActive:
                        _deepSeekStateValue.Text = "Codex 官方账号（可切换）";
                        break;
                    case DeepSeekConfigState.Missing:
                        _deepSeekStateValue.Text = "尚未配置";
                        break;
                    default:
                        _deepSeekStateValue.Text = "配置不完整";
                        break;
                }
                _deepSeekPathValue.Text = status.CodexHome;
                if (status.Model == DeepSeekConfigManager.ProModel)
                    _deepSeekModelCombo.SelectedIndex = 1;
                else if (status.Model == DeepSeekConfigManager.FlashModel)
                    _deepSeekModelCombo.SelectedIndex = 0;
                _deepSeekResult.Text = status.NeedsOfficialSetup
                    ? "未发现完整的 DeepSeek 快照，请先启动官方配置流程。"
                    : "配置已就绪，可切换模型或恢复官方账号。";
            }
            catch (Exception)
            {
                _deepSeekStateValue.Text = "读取失败";
                _deepSeekPathValue.Text = _bridge.DeepSeekConfig.CodexHome;
                _deepSeekResult.Text = "读取配置失败，请检查 .codex 权限或文件是否被占用。";
            }
        }

        private TabPage BuildDisplayTab()
        {
            TabPage page = new TabPage("显示");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12), RowCount = 13 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

            layout.Controls.Add(new Label { Text = "默认 OLED 页：", TextAlign = ContentAlignment.MiddleRight }, 0, 0);
            _pageCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _pageCombo.Items.AddRange(new object[] { "0 状态/额度", "1 PC 监控+动画", "2 自动轮播" });
            layout.Controls.Add(_pageCombo, 1, 0);

            layout.Controls.Add(new Label { Text = "RGB 模式：", TextAlign = ContentAlignment.MiddleRight }, 0, 1);
            _rgbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _rgbMode.Items.AddRange(RgbModesZh);
            layout.Controls.Add(_rgbMode, 1, 1);

            layout.Controls.Add(new Label { Text = "亮度(0-255)：", TextAlign = ContentAlignment.MiddleRight }, 0, 2);
            _rgbBrightness = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 128 };
            layout.Controls.Add(_rgbBrightness, 1, 2);

            layout.Controls.Add(new Label { Text = "动画周期(ms)：", TextAlign = ContentAlignment.MiddleRight }, 0, 3);
            _rgbPeriod = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 2000 };
            layout.Controls.Add(_rgbPeriod, 1, 3);

            layout.Controls.Add(new Label { Text = "灯珠数量：", TextAlign = ContentAlignment.MiddleRight }, 0, 4);
            _rgbCount = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 3 };
            layout.Controls.Add(_rgbCount, 1, 4);

            layout.Controls.Add(new Label { Text = "动画主色：", TextAlign = ContentAlignment.MiddleRight }, 0, 5);
            _rgbColor1 = new TextBox();
            _pickColor1 = new Button { Text = "选择…" };
            _pickColor1.Click += delegate { PickColor(_rgbColor1); };
            layout.Controls.Add(_rgbColor1, 1, 5);
            layout.Controls.Add(_pickColor1, 2, 5);

            layout.Controls.Add(new Label { Text = "状态色-空闲：", TextAlign = ContentAlignment.MiddleRight }, 0, 6);
            _rgbStatusIdle = new TextBox();
            layout.Controls.Add(_rgbStatusIdle, 1, 6);
            Button pickIdle = new Button { Text = "选择…" };
            pickIdle.Click += delegate { PickColor(_rgbStatusIdle); };
            layout.Controls.Add(pickIdle, 2, 6);

            layout.Controls.Add(new Label { Text = "状态色-运行：", TextAlign = ContentAlignment.MiddleRight }, 0, 7);
            _rgbStatusRunning = new TextBox();
            layout.Controls.Add(_rgbStatusRunning, 1, 7);
            Button pickRunning = new Button { Text = "选择…" };
            pickRunning.Click += delegate { PickColor(_rgbStatusRunning); };
            layout.Controls.Add(pickRunning, 2, 7);

            layout.Controls.Add(new Label { Text = "状态色-等待：", TextAlign = ContentAlignment.MiddleRight }, 0, 8);
            _rgbStatusWaiting = new TextBox();
            layout.Controls.Add(_rgbStatusWaiting, 1, 8);
            Button pickWaiting = new Button { Text = "选择…" };
            pickWaiting.Click += delegate { PickColor(_rgbStatusWaiting); };
            layout.Controls.Add(pickWaiting, 2, 8);

            layout.Controls.Add(new Label { Text = "状态色-错误：", TextAlign = ContentAlignment.MiddleRight }, 0, 9);
            _rgbStatusError = new TextBox();
            layout.Controls.Add(_rgbStatusError, 1, 9);
            Button pickError = new Button { Text = "选择…" };
            pickError.Click += delegate { PickColor(_rgbStatusError); };
            layout.Controls.Add(pickError, 2, 9);

            layout.Controls.Add(new Label { Text = "状态色-完成：", TextAlign = ContentAlignment.MiddleRight }, 0, 10);
            _rgbStatusComplete = new TextBox();
            layout.Controls.Add(_rgbStatusComplete, 1, 10);
            Button pickComplete = new Button { Text = "选择…" };
            pickComplete.Click += delegate { PickColor(_rgbStatusComplete); };
            layout.Controls.Add(pickComplete, 2, 10);

            layout.Controls.Add(new Label { Text = "状态色-离线：", TextAlign = ContentAlignment.MiddleRight }, 0, 11);
            _rgbStatusOffline = new TextBox();
            layout.Controls.Add(_rgbStatusOffline, 1, 11);
            Button pickOffline = new Button { Text = "选择…" };
            pickOffline.Click += delegate { PickColor(_rgbStatusOffline); };
            layout.Controls.Add(pickOffline, 2, 11);

            layout.Controls.Add(new Label
            {
                Text = "动画主色：常亮/呼吸/波动/闪烁等动画使用的颜色（如粉色波浪）；颜色 2 已移除。\r\n" +
                       "“RGB 模式=状态色”时使用下方 6 种状态颜色；OLED 地址/驱动已固定为自动探测。",
                AutoSize = true,
                ForeColor = Color.Gray
            }, 1, 12);
            layout.SetColumnSpan(layout.GetControlFromPosition(1, 12), 2);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildMonitorTab()
        {
            TabPage page = new TabPage("PC 监控");
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 12
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            layout.Controls.Add(new Label { Text = "CPU 使用率：", TextAlign = ContentAlignment.MiddleRight }, 0, 0);
            _pcCpuValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcCpuValue, 1, 0);

            layout.Controls.Add(new Label { Text = "CPU 温度：", TextAlign = ContentAlignment.MiddleRight }, 0, 1);
            _pcTemperatureValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcTemperatureValue, 1, 1);

            layout.Controls.Add(new Label { Text = "CPU 温度来源：", TextAlign = ContentAlignment.MiddleRight }, 0, 2);
            _pcTemperatureSourceValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcTemperatureSourceValue, 1, 2);

            layout.Controls.Add(new Label { Text = "GPU 使用率：", TextAlign = ContentAlignment.MiddleRight }, 0, 3);
            _pcGpuValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcGpuValue, 1, 3);

            layout.Controls.Add(new Label { Text = "GPU 温度：", TextAlign = ContentAlignment.MiddleRight }, 0, 4);
            _pcGpuTemperatureValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcGpuTemperatureValue, 1, 4);

            layout.Controls.Add(new Label { Text = "内存：", TextAlign = ContentAlignment.MiddleRight }, 0, 5);
            _pcMemoryValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcMemoryValue, 1, 5);

            layout.Controls.Add(new Label { Text = "主板温度：", TextAlign = ContentAlignment.MiddleRight }, 0, 6);
            _pcMotherboardTemperatureValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcMotherboardTemperatureValue, 1, 6);

            layout.Controls.Add(new Label { Text = "网速：", TextAlign = ContentAlignment.MiddleRight }, 0, 7);
            _pcNetworkValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcNetworkValue, 1, 7);

            layout.Controls.Add(new Label { Text = "最近采样：", TextAlign = ContentAlignment.MiddleRight }, 0, 8);
            _pcUpdatedValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcUpdatedValue, 1, 8);

            layout.Controls.Add(new Label { Text = "状态：", TextAlign = ContentAlignment.MiddleRight }, 0, 9);
            _pcStateValue = CreateMonitorValueLabel();
            layout.Controls.Add(_pcStateValue, 1, 9);

            Label note = new Label
            {
                AutoSize = true,
                ForeColor = Color.Gray,
                Text = "CPU/内存使用 Windows 原生接口；硬件传感器自动加载 LibreHardwareMonitor 0.9.6。\r\n" +
                       "不自动安装驱动；硬件或权限不支持的真实温度显示为 --，不会用温区数据冒充。"
            };
            layout.Controls.Add(note, 1, 10);

            Label protocol = new Label
            {
                AutoSize = true,
                ForeColor = Color.Gray,
                Text = "当前主机与固件使用 PC_METRICS v3（28 字节单包），包含 GPU、主板温度及双向网速。"
            };
            layout.Controls.Add(protocol, 1, 11);
            page.Controls.Add(layout);
            return page;
        }

        private static Label CreateMonitorValueLabel()
        {
            return new Label { AutoSize = true, Text = "--", TextAlign = ContentAlignment.MiddleLeft };
        }

        private TabPage BuildAboutTab()
        {
            TabPage page = new TabPage("关于");
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                Text = "CodeX Tools Host（单文件便携版，.NET Framework 4.8）\r\n\r\n" +
                       "功能：\r\n" +
                       "• 读取 Codex 运行状态与额度（codex app-server）\r\n" +
                       "• 查询各家 API 账户余额（DeepSeek/硅基流动/OpenRouter/自定义地址）\r\n" +
                       "• 通过 USB CDC 驱动 STM32 控制台的 OLED/RGB\r\n" +
                       "• 自动探测 CPU/GPU/主板硬件传感器\r\n" +
                       "• 按键/编码器事件按配置执行白名单动作\r\n\r\n" +
                       "硬件监控组件首次运行释放到 %LOCALAPPDATA%\\CodexToolsHost\\dependencies，\r\n" +
                       "发布文件仍只有一个 EXE。LibreHardwareMonitor 0.9.6：MPL-2.0。\r\n" +
                       "项目：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor\r\n" +
                       "发布目录只放 EXE；配置优先读取程序目录已有文件，\r\n" +
                       "新发布版本或程序目录不可写时使用 %LOCALAPPDATA%\\CodexToolsHost。"
            };
            page.Controls.Add(label);
            return page;
        }

        private void BuildBottom()
        {
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 56 };

            Label portCaption = new Label
            {
                Text = "串口：",
                AutoSize = true,
                Left = 12,
                Top = 19
            };
            _serialPortCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 128,
                Height = 25,
                Left = 52,
                Top = 14
            };
            Button refreshPorts = new Button
            {
                Text = "刷新",
                Width = 55,
                Height = 30,
                Left = 185,
                Top = 12
            };
            refreshPorts.Click += delegate
            {
                RefreshSerialPortList(GetSelectedSerialPort());
            };

            Button reconnect = new Button { Text = "重新连接", Width = 80, Height = 30, Left = 245, Top = 12 };
            reconnect.Click += delegate
            {
                string selectedPort = GetSelectedSerialPort();
                _config.SerialPort = SerialPortSelection.Normalize(selectedPort);
                try { _config.Save(); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "串口选择暂时无法保存，但仍会尝试本次连接：\r\n" + ex.Message,
                        "CodeX Tools", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _bridge.ReconnectDevice(selectedPort);
                UpdatePortStatus();
            };

            _portStatusLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Width = 165,
                Height = 30,
                Left = 332,
                Top = 12,
                TextAlign = ContentAlignment.MiddleLeft
            };

            Button save = new Button { Text = "保存并应用", Width = 110, Height = 30 };
            save.Left = bottom.Width - 240;
            save.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            save.Click += delegate { SaveAndApply(); };

            Button close = new Button { Text = "关闭", Width = 90, Height = 30 };
            close.Left = bottom.Width - 120;
            close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            close.Click += delegate { Close(); };

            _bridge.StatusChanged += OnBridgeStatusChanged;

            bottom.Resize += delegate
            {
                save.Left = bottom.ClientSize.Width - 240;
                close.Left = bottom.ClientSize.Width - 120;
            };

            bottom.Controls.Add(portCaption);
            bottom.Controls.Add(_serialPortCombo);
            bottom.Controls.Add(refreshPorts);
            bottom.Controls.Add(_portStatusLabel);
            bottom.Controls.Add(reconnect);
            bottom.Controls.Add(save);
            bottom.Controls.Add(close);
            Controls.Add(bottom);
        }

        private void UpdatePortStatus()
        {
            if (_portStatusLabel == null) return;
            _portStatusLabel.Text = _bridge.IsDeviceConnected
                ? "串口: " + _bridge.DevicePort + " 已连接"
                : "串口: 未连接（自动重连中）";
        }

        private void SafeUpdatePortStatus()
        {
            try
            {
                if (IsDisposed || _portStatusLabel == null) return;
                if (InvokeRequired) BeginInvoke(new Action(SafeUpdatePortStatus));
                else UpdatePortStatus();
            }
            catch (Exception) { }
        }

        private void OnBridgeStatusChanged(string text)
        {
            SafeUpdatePortStatus();
        }

        private void LoadConfig()
        {
            UpdatePortStatus();

            RefreshSerialPortList(_config.SerialPort);

            LoadBindingsToGrid(_config);

            _encRotateAction.SelectedItem = _config.EncoderRotate == null
                ? EncActionsZh[0]
                : EnToZh(_config.EncoderRotate.Action, EncActionsZh, EncActions);
            _encRotateParam.Text = _config.EncoderRotate == null ? "1" : _config.EncoderRotate.Param;
            _dsKey.Text = _config.DeepSeekApiKey;
            _dsUrl.Text = _config.DeepSeekBaseUrl;
            _dsRefresh.Value = _config.DeepSeekRefreshSeconds;
            int providerIdx = Array.IndexOf(ApiProvidersEn,
                CodexToolsHost.Quota.ApiBalanceProvider.NormalizeProvider(_config.ApiBalanceProvider));
            _dsProvider.SelectedIndex = providerIdx >= 0 ? providerIdx : 0;
            _dsTestResult.Text = string.IsNullOrEmpty(_bridge.DeepSeek.LastError)
                ? "最近查询无错误"
                : "最近错误: " + _bridge.DeepSeek.LastError;
            _quotaDisplaySource.SelectedIndex = string.Equals(
                AppConfig.NormalizeQuotaDisplaySource(_config.QuotaDisplaySource),
                "opencodego", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _goKey.Text = _config.OpenCodeGoApiKey;
            _goRefresh.Value = Math.Max(30, Math.Min(86400, _config.OpenCodeGoRefreshSeconds));
            _goTestResult.Text = string.IsNullOrEmpty(_bridge.OpenCodeGo.LastError)
                ? "最近查询无错误"
                : "最近错误: " + _bridge.OpenCodeGo.LastError;

            _pageCombo.SelectedIndex = Math.Max(0, Math.Min(2, _config.DefaultOledPage));
            _rgbMode.SelectedItem = EnToZh(_config.Rgb.Mode, RgbModesZh, RgbModes);
            _rgbBrightness.Value = Math.Max(0, Math.Min(255, _config.Rgb.Brightness));
            _rgbPeriod.Value = Math.Max(100, _config.Rgb.PeriodMs);
            _rgbCount.Value = Math.Max(1, Math.Min(16, _config.Rgb.Count));
            _rgbColor1.Text = _config.Rgb.Color1;
            _rgbStatusIdle.Text = _config.Rgb.StatusIdle;
            _rgbStatusRunning.Text = _config.Rgb.StatusRunning;
            _rgbStatusWaiting.Text = _config.Rgb.StatusWaiting;
            _rgbStatusError.Text = _config.Rgb.StatusError;
            _rgbStatusComplete.Text = _config.Rgb.StatusComplete;
            _rgbStatusOffline.Text = _config.Rgb.StatusOffline;
            RefreshDeepSeekPage();
            UpdatePcMetrics(_bridge.PcMetrics);
            UpdateOpenCodeGoDetails();
        }

        private void OnDeepSeekChanged()
        {
            RunOnUiThread(delegate
            {
                if (_dsTestResult == null) return;
                _dsTestResult.Text = string.IsNullOrEmpty(_bridge.DeepSeek.LastError)
                    ? "最近查询无错误"
                    : "最近错误: " + _bridge.DeepSeek.LastError;
            });
        }

        private void OnOpenCodeGoChanged()
        {
            RunOnUiThread(UpdateOpenCodeGoDetails);
        }

        private void UpdateOpenCodeGoDetails()
        {
            if (_goRollingValue == null || IsDisposed) return;
            OpenCodeGoQuotaSnapshot snapshot = _bridge.OpenCodeGo.Quota
                ?? OpenCodeGoQuotaSnapshot.EmptyStale();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _goRollingValue.Text = FormatOpenCodeGoWindowDetail(
                snapshot.Rolling, "5h", now);
            _goWeeklyValue.Text = FormatOpenCodeGoWindowDetail(
                snapshot.Weekly, "7d", now);
            _goMonthlyValue.Text = FormatOpenCodeGoWindowDetail(
                snapshot.Monthly, "月", now);
            if (!string.IsNullOrEmpty(_bridge.OpenCodeGo.LastError))
                _goTestResult.Text = "最近错误: " + _bridge.OpenCodeGo.LastError;
        }

        private static string FormatOpenCodeGoWindowDetail(
            OpenCodeGoQuotaWindow window, string label, DateTimeOffset now)
        {
            if (window == null) window = OpenCodeGoQuotaWindow.Empty();
            string used = window.UsedPercent.HasValue
                ? window.UsedPercent.Value.ToString() + "%" : "--%";
            string remaining = window.RemainingPercent.HasValue
                ? window.RemainingPercent.Value.ToString() + "%" : "--%";
            string reset = window.IsRateLimited ? "已达上限"
                : QuotaHudPresentation.FormatResetCountdown(window.ResetsAt, now);
            return "已用 " + used + " / 剩余 " + remaining + " / 重置 " + reset;
        }

        private void RunOnUiThread(Action action)
        {
            if (action == null || IsDisposed) return;
            try
            {
                if (InvokeRequired)
                {
                    if (IsHandleCreated) BeginInvoke(action);
                    return;
                }
                action();
            }
            catch (InvalidOperationException) { }
        }

        private void SafeUpdatePcMetrics(PcMetricsSnapshot snapshot)
        {
            try
            {
                if (IsDisposed || _pcCpuValue == null) return;
                if (InvokeRequired)
                    BeginInvoke(new Action(delegate { SafeUpdatePcMetrics(snapshot); }));
                else UpdatePcMetrics(snapshot);
            }
            catch (Exception) { }
        }

        private void UpdatePcMetrics(PcMetricsSnapshot snapshot)
        {
            if (snapshot == null) return;
            _pcCpuValue.Text = snapshot.CpuLoadPercent >= 0
                ? snapshot.CpuLoadPercent + "%" : "--";
            _pcGpuValue.Text = snapshot.GpuLoadPercent >= 0
                ? snapshot.GpuLoadPercent + "%" : "--";
            int memoryPercent = snapshot.MemoryUsedPercent;
            if (memoryPercent >= 0)
            {
                double usedGiB = snapshot.MemoryUsedBytes / 1073741824.0;
                double totalGiB = snapshot.MemoryTotalBytes / 1073741824.0;
                _pcMemoryValue.Text = memoryPercent + "% (" + usedGiB.ToString("0.0") + "/" +
                    totalGiB.ToString("0.0") + " GB)";
            }
            else _pcMemoryValue.Text = "--";
            _pcTemperatureValue.Text = snapshot.CpuTemperatureC.HasValue
                ? snapshot.CpuTemperatureC.Value.ToString("0.0") + " °C" : "--";
            _pcTemperatureSourceValue.Text = string.IsNullOrEmpty(snapshot.TemperatureSource)
                ? "--" : snapshot.TemperatureSource;
            _pcGpuTemperatureValue.Text = snapshot.GpuTemperatureC.HasValue
                ? snapshot.GpuTemperatureC.Value.ToString("0.0") + " °C (" +
                    (string.IsNullOrEmpty(snapshot.GpuSource) ? "--" : snapshot.GpuSource) + ")"
                : "--";
            _pcMotherboardTemperatureValue.Text = snapshot.MotherboardTemperatureC.HasValue
                ? snapshot.MotherboardTemperatureC.Value.ToString("0.0") + " °C (" +
                    (string.IsNullOrEmpty(snapshot.MotherboardTemperatureSource)
                        ? "--" : snapshot.MotherboardTemperatureSource) + ")"
                : "--";
            string network = QuotaHudPresentation.FormatNetworkSpeed(
                snapshot.NetworkSpeedAvailable, snapshot.NetworkDownloadKiBPerSecond,
                snapshot.NetworkUploadKiBPerSecond);
            int separator = network.IndexOf(' ');
            _pcNetworkValue.Text = separator > 0
                ? network.Substring(separator + 1) + " / " + network.Substring(0, separator)
                : network;
            _pcUpdatedValue.Text = snapshot.SampledAtUtc == default(DateTimeOffset)
                ? "--" : snapshot.SampledAtUtc.ToLocalTime().ToString("HH:mm:ss");
            if (!string.IsNullOrEmpty(snapshot.ErrorText))
                _pcStateValue.Text = "采集异常：" + snapshot.ErrorText;
            else if (snapshot.IsStale)
                _pcStateValue.Text = "数据已过期";
            else if (!snapshot.CpuTemperatureC.HasValue
                     || !snapshot.GpuTemperatureC.HasValue
                     || !snapshot.MotherboardTemperatureC.HasValue)
                _pcStateValue.Text = "正常（部分硬件传感器不可用）";
            else
                _pcStateValue.Text = "正常";
        }

        private void LoadBindingsToGrid(AppConfig source)
        {
            _grid.Rows.Clear();
            foreach (string key in AppConfig.BindingKeys)
            {
                ActionSpec spec;
                if (!source.Bindings.TryGetValue(key, out spec)) spec = new ActionSpec("none", "");
                string evtName = key.EndsWith("_click") ? "单击"
                    : key.EndsWith("_double") ? "双击"
                    : key.EndsWith("_long") ? "长按" : "";
                string keyName = key.StartsWith("enc_") ? "编码器按键" : key.Substring(0, 4).ToUpperInvariant();
                _grid.Rows.Add(keyName + " " + evtName, evtName,
                    EnToZh(spec.Action, ActionNamesZh, ActionNames), spec.Param);
                _grid.Rows[_grid.Rows.Count - 1].Tag = key;
            }
        }

        private void SaveAndApply()
        {
            if (_mijiaPanel != null) _mijiaPanel.ApplySettings();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string key = row.Tag as string;
                if (key == null) continue;
                string action = ZhToEn(
                    Convert.ToString(row.Cells["action"].Value ?? ActionNamesZh[0]),
                    ActionNamesZh, ActionNames, "none");
                string param = Convert.ToString(row.Cells["param"].Value ?? "");
                if (_config.Bindings.ContainsKey(key))
                    _config.Bindings[key] = new ActionSpec(action, param);
            }
            _config.EncoderRotate = new ActionSpec(
                ZhToEn(Convert.ToString(_encRotateAction.SelectedItem ?? EncActionsZh[0]),
                       EncActionsZh, EncActions, "volume"),
                _encRotateParam.Text.Trim());
            _config.SerialPort = SerialPortSelection.Normalize(GetSelectedSerialPort());
            _config.DeepSeekApiKey = _dsKey.Text.Trim();
            _config.DeepSeekBaseUrl = _dsUrl.Text.Trim();
            _config.DeepSeekRefreshSeconds = (int)_dsRefresh.Value;
            _config.ApiBalanceProvider = CurrentApiProvider();
            _config.OpenCodeGoApiKey = _goKey.Text.Trim();
            _config.OpenCodeGoRefreshSeconds = (int)_goRefresh.Value;
            _config.QuotaDisplaySource = CurrentQuotaDisplaySource();
            _config.DefaultOledPage = Math.Max(0, Math.Min(2, _pageCombo.SelectedIndex));
            _config.Rgb.Mode = ZhToEn(
                Convert.ToString(_rgbMode.SelectedItem ?? RgbModesZh[0]),
                RgbModesZh, RgbModes, "status");
            _config.Rgb.Brightness = (int)_rgbBrightness.Value;
            _config.Rgb.PeriodMs = (int)_rgbPeriod.Value;
            _config.Rgb.Count = (int)_rgbCount.Value;
            _config.Rgb.Color1 = _rgbColor1.Text.Trim();
            _config.Rgb.StatusIdle = _rgbStatusIdle.Text.Trim();
            _config.Rgb.StatusRunning = _rgbStatusRunning.Text.Trim();
            _config.Rgb.StatusWaiting = _rgbStatusWaiting.Text.Trim();
            _config.Rgb.StatusError = _rgbStatusError.Text.Trim();
            _config.Rgb.StatusComplete = _rgbStatusComplete.Text.Trim();
            _config.Rgb.StatusOffline = _rgbStatusOffline.Text.Trim();

            _config.Save();
            _bridge.ApplyDeviceConfig();
            MessageBox.Show(this, "配置已保存并下发到设备。", "CodeX Tools",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void PickColor(TextBox target)
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                try { dialog.Color = ColorTranslator.FromHtml(target.Text); } catch (Exception) { }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    target.Text = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _bridge.StatusChanged -= OnBridgeStatusChanged;
            _bridge.PcMetricsChanged -= SafeUpdatePcMetrics;
            _bridge.DeepSeek.Changed -= OnDeepSeekChanged;
            _bridge.OpenCodeGo.Changed -= OnOpenCodeGoChanged;
            _goDisplayTimer.Stop();
            _goDisplayTimer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
