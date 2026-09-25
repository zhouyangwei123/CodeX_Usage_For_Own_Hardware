using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CodexToolsHost.Usage;

namespace CodexToolsHost.UI
{
    public sealed class UsagePanel : UserControl
    {
        private readonly LocalUsageService service;
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

        public UsagePanel(LocalUsageService service)
        {
            if (service == null) throw new ArgumentNullException("service");
            this.service = service;
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
            layout.Controls.Add(new Label { Text = "本机 Token 用量", Font = titleFont, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            layout.Controls.Add(new Label { Text = "每 5 秒增量刷新 · 每分钟发现新会话 · 缓存属于输入，推理属于输出。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(98, 113, 132) }, 0, 1);
            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            source.Name = "usageSource"; source.DropDownStyle = ComboBoxStyle.DropDownList; source.Width = 150;
            source.Items.Add("本机 Codex 日志"); source.SelectedIndex = 0;
            period.Name = "usagePeriod"; period.DropDownStyle = ComboBoxStyle.DropDownList; period.Width = 100;
            period.Items.AddRange(new object[] { "今天", "近 7 天", "本月", "全部会话" }); period.SelectedIndex = 1;
            refresh = Button("刷新", "usageRefresh", 66);
            Button import = Button("导入 JSON", "usageImport", 93);
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
            source.SelectedIndexChanged += delegate { RenderReport(); };
            period.SelectedIndexChanged += delegate { RenderReport(); };
            refresh.Click += async delegate
            {
                refresh.Enabled = false; status.Text = "正在读取新增日志…";
                try { await service.RefreshAsync(); }
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
            HandleCreated += delegate { RenderReport(); };
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
            UsageReport report = source.SelectedIndex == 1 && imported != null ? imported : service.Report;
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
        private static string ShortId(string value) { return value != null && value.Length > 18 ? value.Substring(0, 8) + "…" + value.Substring(value.Length - 6) : value; }
        private void Import()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "ccusage JSON (*.json)|*.json", Title = "导入独立 ccusage 统计" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    imported = CcusageImport.ReadFile(dialog.FileName);
                    if (source.Items.Count == 1) source.Items.Add("ccusage JSON 导入");
                    source.SelectedIndex = 1; RenderReport();
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
                try { File.WriteAllText(dialog.FileName, UsageReport.ToCsv(visibleRows, source.SelectedIndex == 1 ? "ccusage JSON 独立导入" : "本机 Codex"), new UTF8Encoding(true)); status.Text = "已导出当前清单。"; }
                catch (IOException) { status.Text = "导出失败，文件可能正在使用。"; }
                catch (UnauthorizedAccessException) { status.Text = "导出失败，没有写入权限。"; }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { service.Changed -= ServiceChanged; titleFont.Dispose(); summaryFont.Dispose(); bodyFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
