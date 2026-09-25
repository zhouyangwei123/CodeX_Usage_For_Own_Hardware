using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CodexToolsHost.Model;

namespace CodexToolsHost.UI
{
    internal sealed class HudStylePreview : Control
    {
        private readonly QuotaHudRenderer renderer = new QuotaHudRenderer();
        private string style = "glass";
        public string Style { get { return style; } set { style = value; Invalidate(); } }
        public HudStylePreview()
        {
            DoubleBuffered = true; Height = 176; MinimumSize = new Size(330, 176);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var text = new SolidBrush(Color.FromArgb(75, 88, 106)))
                e.Graphics.DrawString("外观预览（演示数据）", Font, text, new PointF(0, 0));
            var quota = new QuotaSnapshot(68, 42, null, null, DateTimeOffset.UtcNow, false, 300, 10080, "");
            using (var frame = renderer.RenderFrame(new Size(320, 138), quota, style, 0, false,
                "API · CNY 123.45", "↑12K ↓1.2M", false, true, false, SystemInformation.HighContrast))
                e.Graphics.DrawImageUnscaled(frame, 0, 29);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) renderer.Dispose();
            base.Dispose(disposing);
        }
    }
}
