using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CodexToolsHost.Model;
using CodexToolsHost.Usage;

namespace CodexToolsHost.UI
{
    internal sealed class HudStylePreview : Control
    {
        private readonly QuotaHudRenderer renderer = new QuotaHudRenderer();
        private string style = "glass";
        private bool expanded = true;
        public string Style { get { return style; } set { style = value; Invalidate(); } }
        public bool ChartExpanded { get { return expanded; } set { expanded = value; Invalidate(); } }
        public HudStylePreview()
        {
            DoubleBuffered = true; Height = 264; MinimumSize = new Size(330, 264);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var text = new SolidBrush(Color.FromArgb(75, 88, 106)))
                e.Graphics.DrawString("外观预览（演示数据）", Font, text, new PointF(0, 0));
            var quota = new QuotaSnapshot(68, 42, null, null, DateTimeOffset.UtcNow, false, 300, 10080, "");
            var now = DateTimeOffset.UtcNow;
            var points = new List<UsageActivityPoint>();
            for (int i = 0; i <= 360; i++) points.Add(new UsageActivityPoint { Time = now.AddSeconds((i - 360) * 10),
                HasData = true, InputTokens = (long)(2600 + 1300 * Math.Sin(i * .075)), OutputTokens = (long)(650 + 500 * Math.Cos(i * .07)) });
            var activity = new UsageActivitySnapshot { AsOf = now, IsReady = true, Status = "正常", InputTokensPerMinute = 3200,
                OutputTokensPerMinute = 1000, HourTokens = 198000, Points = points.AsReadOnly() };
            Size canonical = QuotaHudRenderer.ActivityWindowSize(expanded);
            using (var frame = renderer.RenderActivityFrame(new Size(315, (int)(canonical.Height * .75)), quota, style, 0, false,
                "CNY 123.45\r\n已更新 12:30", "↑12K ↓1.2M", false, true, false, SystemInformation.HighContrast, activity, expanded))
                e.Graphics.DrawImageUnscaled(frame, 0, 29);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var hit = QuotaHudRenderer.ActivityToggleBounds();
            var scaled = new Rectangle((int)(hit.X * .75), 29 + (int)(hit.Y * .75), (int)(hit.Width * .75), (int)(hit.Height * .75));
            if (e.Button == MouseButtons.Left && scaled.Contains(e.Location)) ChartExpanded = !ChartExpanded;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) renderer.Dispose();
            base.Dispose(disposing);
        }
    }
}
