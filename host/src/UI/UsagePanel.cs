using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CodexToolsHost.Usage;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Quota;

namespace CodexToolsHost.UI
{
    public sealed class UsagePanel : UserControl
    {
        private readonly LocalUsageService service;
        private readonly BridgeService bridge;
        private readonly AppConfig config;
        private readonly Label heading = new Label();
        private readonly Label description = new Label();
        private Button importButton;
        private List<AccountUsageDay> visibleAccountDays = new List<AccountUsageDay>();
        private readonly ComboBox source = new ComboBox();
        private readonly ComboBox period = new ComboBox();
        private readonly Label summary = new Label();
        private readonly Label inputSummary = new Label();
        private readonly Label cacheSummary = new Label();
        private readonly Label outputSummary = new Label();
        private readonly Label status = new Label();
        private readonly Label coverage = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly Button refresh;
        private UsageReport imported;
        private List<UsageRow> visibleRows = new List<UsageRow>();
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold);
        private readonly Font summaryFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        private readonly Font bodyFont = new Font("Microsoft YaHei UI", 9f);

        public UsagePanel(LocalUsageService service) : this(service, null, AppConfig.CreateDefault()) { }
        public UsagePanel(LocalUsageService service, BridgeService bridge, AppConfig config)
        {
            if (service == null) throw new ArgumentNullException("service");
            if (config == null) throw new ArgumentNullException("config");
            this.service = service;
            this.bridge = bridge; this.config = config;
            Name = "usagePanel";
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(246, 248, 251);
            ForeColor = Color.FromArgb(33, 55, 83);
            Font = bodyFont;
            Padding = new Padding(20, 14, 20, 14);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float height in new float[] { 40, 36, 44, 68 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            heading.Font = titleFont; heading.Dock = DockStyle.Fill; heading.TextAlign = ContentAlignment.MiddleLeft;
            description.Dock = DockStyle.Fill; description.TextAlign = ContentAlignment.MiddleLeft; description.ForeColor = Color.FromArgb(98, 113, 132);
            layout.Controls.Add(heading, 0, 0); layout.Controls.Add(description, 0, 1);
            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            source.Name = "usageSource"; source.DropDownStyle = ComboBoxStyle.DropDownList; source.Width = 150;
            source.Items.AddRange(new object[] { "整个账号 · 服务端", "本机 Codex 日志" });
            source.SelectedIndex = AppConfig.NormalizeUsageScope(config.UsageDisplayScope) == "local" ? 1 : 0;
            period.Name = "usagePeriod"; period.DropDownStyle = ComboBoxStyle.DropDownList; period.Width = 100;
            period.Items.AddRange(new object[] { "今天", "近 7 天", "本月", "全部会话" }); period.SelectedIndex = 1;
            refresh = Button("刷新", "usageRefresh", 66);
            Button import = Button("导入 JSON", "usageImport", 93);
            importButton = import;
            Button export = Button("导出 CSV", "usageExport", 90);
            Button help = Button("导入帮助", "usageHelp", 90);
            toolbar.Controls.AddRange(new Control[] { source, period, refresh, import, export, help });
            layout.Controls.Add(toolbar, 0, 2);
            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 4, 0, 10) };
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            summary.Name = "usageSummary"; inputSummary.Name = "usageInputSummary"; cacheSummary.Name = "usageCacheSummary"; outputSummary.Name = "usageOutputSummary";
            cards.Controls.Add(Card("总计 Tokens", summary), 0, 0);
            cards.Controls.Add(Card("输入 · 含缓存", inputSummary), 1, 0);
            cards.Controls.Add(Card("缓存 · 输入子集", cacheSummary), 2, 0);
            cards.Controls.Add(Card("输出 · 含推理", outputSummary), 3, 0);
            layout.Controls.Add(cards, 0, 3);
            BuildGrid(); layout.Controls.Add(grid, 0, 4);
            status.Name = "usageStatus"; status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Color.FromArgb(98, 113, 132); layout.Controls.Add(status, 0, 5);
            coverage.Name = "usageCoverage"; coverage.Dock = DockStyle.Fill; coverage.Padding = new Padding(10, 7, 10, 5);
            coverage.BackColor = Color.FromArgb(237, 243, 250); coverage.ForeColor = Color.FromArgb(75, 93, 117);
            coverage.AutoEllipsis = true; layout.Controls.Add(coverage, 0, 6);
            Controls.Add(layout);
            source.SelectedIndexChanged += delegate {
                if (source.SelectedIndex < 2 && bridge != null)
                {
                    string scope = source.SelectedIndex == 0 ? "account" : "local";
                    if (scope != AppConfig.NormalizeUsageScope(config.UsageDisplayScope))
                    { bridge.SetUsageScope(scope); try { config.Save(); } catch (IOException) { status.Text = "来源保存失败"; } catch (UnauthorizedAccessException) { status.Text = "来源保存失败"; } }
                }
                RenderReport();
            };
            period.SelectedIndexChanged += delegate { RenderReport(); };
            refresh.Click += async delegate
            {
                refresh.Enabled = false; status.Text = source.SelectedIndex == 0 ? "正在刷新账号统计…" : "正在读取新增日志…";
                try {
                    if (source.SelectedIndex == 0)
                    {
                        if (bridge == null) throw new InvalidOperationException("账号统计未连接");
                        var provider = bridge.Codex as CodexStatusProvider; if (provider != null) provider.RequestUsageRefresh();
                        await bridge.Codex.RefreshQuotaAsync();
                    }
                    else await service.RefreshAsync();
                }
                catch (Exception) { if (!IsDisposed) status.Text = "读取未完成，请稍后重试。"; }
                finally { if (!IsDisposed) { refresh.Enabled = true; RenderReport(); } }
            };
            import.Click += delegate { Import(); };
            export.Click += delegate { Export(); };
            help.Click += delegate
            {
                MessageBox.Show(this, "可选：在终端手动运行固定版本，保存 JSON 后导入。应用不会安装或执行 npm。\r\n\r\n" +
                    CcusageImport.CommandHelp + "\r\n\r\nPowerShell 保存 UTF-8：\r\n" + CcusageImport.CommandHelp + " | Out-File -Encoding utf8 usage.json\r\n\r\n" +
                    "也支持 codex monthly / session 的专用 JSON。统一多来源 JSON 不支持。导入结果独立显示，不与本机合计相加。", "ccusage 导入帮助", MessageBoxButtons.OK, MessageBoxIcon.None);
            };
            service.Changed += ServiceChanged;
            if (bridge != null) { bridge.Codex.Changed += ServiceChanged; bridge.UsageScopeChanged += ScopeChanged; }
            HandleCreated += delegate { ScopeChanged(); RenderReport(); };
            RenderReport();
        }
        private static Button Button(string text, string name, int width)
        {
            Button button = new Button { Text = text, Name = name, Width = width, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(5, 0, 0, 0) };
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 216, 229); return button;
        }
        private Control Card(string caption, Label value)
        {
            Panel card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(10, 5, 5, 4) };
            Label label = new Label { Text = caption, Dock = DockStyle.Top, Height = 20, ForeColor = Color.FromArgb(98, 113, 132) };
            value.Tag = label;
            value.Dock = DockStyle.Fill; value.Font = summaryFont; value.AutoEllipsis = true; value.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(value); card.Controls.Add(label); return card;
        }
        private void BuildGrid()
        {
            grid.Name = "usageGrid"; grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeight = 36; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 239, 248); grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 251); grid.DefaultCellStyle.SelectionForeColor = ForeColor;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 250, 253); grid.RowTemplate.Height = 34;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(233, 239, 246);
            string[] labels = { "日期 / 会话", "模型", "输入（含缓存）", "缓存", "输出", "总计" };
            for (int i = 0; i < labels.Length; i++)
            {
                DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn { Name = "usageColumn" + i, HeaderText = labels[i],
                    FillWeight = i == 0 ? 105 : i == 1 ? 140 : 105, MinimumWidth = i == 1 ? 110 : 86 };
                if (i >= 2) { column.ValueType = typeof(long); column.DefaultCellStyle.Format = "N0"; column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; }
                grid.Columns.Add(column);
            }
        }
        private void ServiceChanged()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke((Action)RenderReport); } catch (InvalidOperationException) { } }
            else RenderReport();
        }
        private void RenderReport()
        {
            if (IsDisposed || Disposing) return;
            bool accountView = source.SelectedIndex == 0;
            heading.Text = accountView ? "整个账号 Token 用量" : source.SelectedIndex == 2 ? "独立导入 Token 用量" : "本机 Token 用量";
            description.Text = accountView ? "服务端累计量与按日汇总 · 自动查询，可能延迟 · 不显示逐秒速率。" : "每 5 秒增量刷新 · 缓存属于输入，推理属于输出。";
            string allLabel = accountView ? "已返回全部日期" : "全部会话";
            if (!Equals(period.Items[3], allLabel)) period.Items[3] = allLabel;
            importButton.Enabled = !accountView;
            for (int i = 1; i <= 4; i++) grid.Columns[i].Visible = !accountView;
            if (accountView) { RenderAccount(); return; }
            ((Label)summary.Tag).Text = "所选周期总计"; ((Label)inputSummary.Tag).Text = "输入 · 含缓存";
            ((Label)cacheSummary.Tag).Text = "缓存 · 输入子集"; ((Label)outputSummary.Tag).Text = "输出 · 含推理";
            UsageReport report = source.SelectedIndex == 2 && imported != null ? imported : service.Report;
            bool aggregateImport = report.IsImported && report.PeriodKind != "daily";
            period.Enabled = !aggregateImport;
            IEnumerable<UsageRow> selected = report.Rows;
            DateTime today = DateTime.Today;
            if (!aggregateImport && period.SelectedIndex != 3)
            {
                DateTime start = period.SelectedIndex == 0 ? today : period.SelectedIndex == 1 ? today.AddDays(-6) : new DateTime(today.Year, today.Month, 1);
                selected = selected.Where(x => x.Day >= start && x.Day <= today);
            }
            bool sessionView = !aggregateImport && period.SelectedIndex == 3;
            if (sessionView) visibleRows = selected.GroupBy(x => new { x.SessionId, x.Model }).Select(x => UsageReport.Sum(x, x.Key.SessionId, x.Key.Model)).OrderByDescending(x => x.LastActivity).ToList();
            else if (aggregateImport) visibleRows = selected.OrderByDescending(x => x.Day).ToList();
            else visibleRows = selected.GroupBy(x => new { x.Day, x.Model }).Select(x => UsageReport.Sum(x, "日汇总", x.Key.Model)).OrderByDescending(x => x.Day).ThenBy(x => x.Model).ToList();
            UsageRow total = UsageReport.Sum(visibleRows, "", "");
            summary.Text = report.UpdatedAt == default(DateTimeOffset) ? "等待读取" : total.TotalTokens.ToString("N0");
            inputSummary.Text = total.InputTokens.ToString("N0"); cacheSummary.Text = total.CachedInputTokens.ToString("N0"); outputSummary.Text = total.OutputTokens.ToString("N0");
            summary.ForeColor = report.FilesPending > 0 ? Color.FromArgb(156, 103, 36) : ForeColor;
            grid.Rows.Clear();
            foreach (UsageRow row in visibleRows)
            {
                string key = sessionView || report.PeriodKind == "sessions" ? ShortId(row.SessionId) : row.Day.ToString(report.PeriodKind == "monthly" ? "yyyy-MM" : "MM-dd");
                int index = grid.Rows.Add(key, row.Model, row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.TotalTokens);
                grid.Rows[index].Cells[0].ToolTipText = row.SessionId + (row.LastActivity == default(DateTimeOffset) ? "" : "\r\n最近活动 " + row.LastActivity.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            }
            status.Text = report.UpdatedAt == default(DateTimeOffset) ? "尚未读取 · 可点击刷新" : (report.FilesPending > 0 ? "正在读取 · 当前数字仅为已扫描部分 · " : "") + report.Source + " · 更新 " + report.UpdatedAt.ToLocalTime().ToString("HH:mm:ss") +
                " · " + visibleRows.Count + " 行" + (report.IsImported ? "" : " · 日志 " + report.FilesDiscovered + " 个" + (report.FilesPending > 0 ? " · 待扫描 " + report.FilesPending + " 个" : ""));
            coverage.Text = string.Join("  ", report.Warnings.ToArray());
        }
        private void ScopeChanged()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke((Action)ScopeChanged); } catch (InvalidOperationException) { } return; }
            source.SelectedIndex = AppConfig.NormalizeUsageScope(config.UsageDisplayScope) == "local" ? 1 : 0;
            RenderReport();
        }
        private void RenderAccount()
        {
            period.Enabled = true;
            var provider = bridge == null ? null : bridge.Codex as IAccountUsageSource;
            var snapshot = provider == null ? AccountUsageSnapshot.Empty() : provider.AccountUsage;
            IEnumerable<AccountUsageDay> days = snapshot.Days;
            DateTime today = DateTime.Today;
            if (period.SelectedIndex != 3)
            {
                DateTime first = period.SelectedIndex == 0 ? today : period.SelectedIndex == 1 ? today.AddDays(-6) : new DateTime(today.Year, today.Month, 1);
                days = days.Where(d => d.Date >= first && d.Date <= today);
            }
            visibleAccountDays = days.OrderByDescending(d => d.Date).ToList();
            visibleRows.Clear();
            ((Label)summary.Tag).Text = "账号累计"; ((Label)inputSummary.Tag).Text = "已汇总周期合计";
            ((Label)cacheSummary.Tag).Text = "最近统计日"; ((Label)outputSummary.Tag).Text = "最近日用量";
            summary.Text = snapshot.LifetimeTokens.HasValue ? snapshot.LifetimeTokens.Value.ToString("N0") : "--";
            inputSummary.Text = visibleAccountDays.Count == 0 ? "--" : visibleAccountDays.Sum(d => (decimal)d.Tokens).ToString("N0");
            AccountUsageDay last = snapshot.Days.LastOrDefault();
            cacheSummary.Text = last == null ? "--" : last.Date.ToString("yyyy-MM-dd");
            outputSummary.Text = last == null ? "--" : last.Tokens.ToString("N0");
            summary.ForeColor = snapshot.IsStale ? Color.FromArgb(156, 103, 36) : ForeColor;
            grid.Rows.Clear();
            foreach (AccountUsageDay day in visibleAccountDays) grid.Rows.Add(day.Date.ToString("yyyy-MM-dd"), "", null, null, null, day.Tokens);
            status.Text = snapshot.Status + (snapshot.ObservedAt == DateTimeOffset.MinValue ? "" : " · 查询 " + snapshot.ObservedAt.ToLocalTime().ToString("HH:mm:ss")) + (snapshot.IsStale ? " · 数据不可用/已过期" : "");
            coverage.Text = "默认显示整个账号的服务端统计；本机可在来源中切换。服务端不提供此处的输入/缓存/输出分项，尚未返回的日期不按0处理。累计量不等于下表已返回日期的简单合计，跨设备覆盖范围和汇总延迟以服务端为准。";
        }
        private static string ShortId(string value) { return value != null && value.Length > 18 ? value.Substring(0, 8) + "…" + value.Substring(value.Length - 6) : value; }
        private void Import()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "ccusage JSON (*.json)|*.json", Title = "导入独立 ccusage 统计" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    imported = CcusageImport.ReadFile(dialog.FileName);
                    if (source.Items.Count == 2) source.Items.Add("ccusage JSON 导入");
                    source.SelectedIndex = 2; RenderReport();
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)) throw;
                    MessageBox.Show(this, ex is FormatException ? ex.Message : "无法读取所选文件，请检查权限或稍后重试。", "导入未完成", MessageBoxButtons.OK, MessageBoxIcon.None);
                }
            }
        }
        private void Export()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "codex-usage-" + DateTime.Today.ToString("yyyyMMdd") + ".csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try {
                    string csv = source.SelectedIndex == 0 ? "来源,服务端日期,账号Token总数\r\n" + string.Join("\r\n", visibleAccountDays.Select(d => "整个账号," + d.Date.ToString("yyyy-MM-dd") + "," + d.Tokens.ToString(CultureInfo.InvariantCulture)).ToArray()) + "\r\n"
                        : UsageReport.ToCsv(visibleRows, source.SelectedIndex == 2 ? "ccusage JSON 独立导入" : "本机 Codex");
                    File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(true)); status.Text = "已导出当前清单。";
                }
                catch (IOException) { status.Text = "导出失败，文件可能正在使用。"; }
                catch (UnauthorizedAccessException) { status.Text = "导出失败，没有写入权限。"; }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                service.Changed -= ServiceChanged;
                if (bridge != null) { bridge.Codex.Changed -= ServiceChanged; bridge.UsageScopeChanged -= ScopeChanged; }
                titleFont.Dispose(); summaryFont.Dispose(); bodyFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
