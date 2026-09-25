using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using CodexToolsHost.Model;

namespace CodexToolsHost.UI
{
    internal sealed class QuotaHudLayout
    {
        public Rectangle PrimaryTrack { get; set; }
        public Rectangle PrimaryFill { get; set; }
        public Rectangle SecondaryTrack { get; set; }
        public Rectangle SecondaryFill { get; set; }
        public Rectangle RefreshHit { get; set; }
        public string PrimaryText { get; set; }
        public string SecondaryText { get; set; }
    }

    internal sealed class QuotaHudRenderer : IDisposable
    {
        private readonly Font _labelFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font _percentFont = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font _apiFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font _apiGoFont = new Font("Segoe UI", 9.3f, FontStyle.Regular, GraphicsUnit.Pixel);

        public Size PreferredSize { get { return new Size(320, 78); } }

        public QuotaHudLayout CalculateLayout(Rectangle bounds, QuotaSnapshot snapshot)
        {
            if (snapshot == null) snapshot = QuotaSnapshot.EmptyStale();
            Rectangle primary = new Rectangle(bounds.Left + 18, bounds.Top + 30,
                Math.Max(1, bounds.Width - 66), 6);
            Rectangle secondary = new Rectangle(bounds.Left + 18, bounds.Top + 64,
                Math.Max(1, bounds.Width - 66), 6);
            return new QuotaHudLayout
            {
                PrimaryTrack = primary,
                PrimaryFill = FillRectangle(primary, snapshot.PrimaryRemainingPercent),
                SecondaryTrack = secondary,
                SecondaryFill = FillRectangle(secondary, snapshot.SecondaryRemainingPercent),
                RefreshHit = RefreshHitBounds(bounds),
                PrimaryText = QuotaHudPresentation.FormatPercent(snapshot.PrimaryRemainingPercent),
                SecondaryText = QuotaHudPresentation.FormatPercent(snapshot.SecondaryRemainingPercent)
            };
        }

        public Rectangle RefreshHitBounds(Rectangle bounds)
        {
            return new Rectangle(bounds.Right - 40, bounds.Top + (bounds.Height - 30) / 2, 30, 30);
        }

        // Caller owns the PArgb bitmap. Grayscale text coverage avoids ClearType color fringes.
        public Bitmap RenderFrame(Size size, QuotaSnapshot snapshot, string style,
            float angle, bool refreshing, string apiText, string networkText,
            bool apiStale, bool apiAvailable, bool apiIsGo, bool opaque)
        {
            if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException("size");
            style = NormalizeStyle(style);
            if (opaque) style = "minimal";
            Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(opaque ? Color.FromArgb(15, 20, 28) : Color.Transparent);
                    graphics.ScaleTransform(size.Width / 320f, size.Height / 138f);
                    ConfigureGraphics(graphics);
                    DrawPanel(graphics, new RectangleF(1, 1, 318, 136), style, opaque);
                    DrawQuota(graphics, new Rectangle(0, 0, 320, 78), snapshot,
                        angle, refreshing, snapshot == null || snapshot.IsStale, style);
                    DrawDetails(graphics, apiText, networkText, apiStale, apiAvailable, apiIsGo, style);
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        public void Draw(Graphics graphics, Rectangle bounds, QuotaSnapshot snapshot,
            float animationAngle, bool refreshing, bool stale)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            ConfigureGraphics(graphics);
            DrawPanel(graphics, new RectangleF(bounds.X + 1, bounds.Y + 1,
                Math.Max(1, bounds.Width - 2), Math.Max(1, bounds.Height - 2)), "glass", false);
            DrawQuota(graphics, bounds, snapshot, animationAngle, refreshing, stale, "glass");
        }

        private static string NormalizeStyle(string style)
        {
            return string.Equals(style, "minimal", StringComparison.OrdinalIgnoreCase) ? "minimal"
                : string.Equals(style, "classic", StringComparison.OrdinalIgnoreCase) ? "classic" : "glass";
        }

        private static void ConfigureGraphics(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.GammaCorrected;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        private static void DrawPanel(Graphics graphics, RectangleF bounds, string style, bool opaque)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, style == "minimal" ? 13f : 18f))
            {
                if (style == "glass")
                {
                    using (Brush gradient = new LinearGradientBrush(bounds,
                        Color.FromArgb(235, 35, 48, 68), Color.FromArgb(220, 16, 23, 37), 65f))
                        graphics.FillPath(gradient, path);
                    using (Pen rim = new Pen(Color.FromArgb(48, 206, 228, 255), 1f))
                        graphics.DrawPath(rim, path);
                    using (Brush sheen = new LinearGradientBrush(new RectangleF(18, 2, 200, 1),
                        Color.FromArgb(70, 203, 237, 255), Color.FromArgb(0, 203, 237, 255), 0f))
                        graphics.FillRectangle(sheen, 20, 2, 198, 1);
                }
                else
                {
                    using (Brush background = new SolidBrush(style == "classic"
                        ? Color.FromArgb(245, 22, 27, 36) : Color.FromArgb(15, 20, 28)))
                        graphics.FillPath(background, path);
                    if (!opaque)
                        using (Pen rim = new Pen(Color.FromArgb(100, 76, 91, 112), 1f))
                            graphics.DrawPath(rim, path);
                }
            }
        }

        private void DrawQuota(Graphics graphics, Rectangle bounds, QuotaSnapshot snapshot,
            float angle, bool refreshing, bool stale, string style)
        {
            if (snapshot == null) snapshot = QuotaSnapshot.EmptyStale();
            QuotaHudLayout layout = CalculateLayout(bounds, snapshot);
            Color primary = style == "classic" ? Color.FromArgb(247, 68, 99) : Color.FromArgb(89, 224, 210);
            Color secondary = style == "classic" ? Color.FromArgb(78, 145, 255) : Color.FromArgb(155, 163, 255);
            if (style == "minimal") { primary = Color.FromArgb(125, 231, 217); secondary = Color.FromArgb(209, 215, 228); }
            if (snapshot.PrimaryRemainingPercent.HasValue && snapshot.PrimaryRemainingPercent.Value < 20)
                primary = Color.FromArgb(255, 153, 112);
            DrawQuotaRow(graphics, layout.PrimaryTrack, layout.PrimaryFill, layout.PrimaryText,
                WindowLabel(snapshot.PrimaryWindowMinutes, "主窗口"), primary, style);
            DrawQuotaRow(graphics, layout.SecondaryTrack, layout.SecondaryFill, layout.SecondaryText,
                WindowLabel(snapshot.SecondaryWindowMinutes, "次窗口"), secondary, style);
            DrawRefresh(graphics, layout.RefreshHit, angle, refreshing, stale || snapshot.IsStale);
        }

        private static string WindowLabel(int? minutes, string fallback)
        {
            if (!minutes.HasValue || minutes.Value <= 0) return "CODEX / " + fallback;
            if (minutes.Value % 1440 == 0) return "CODEX / " + (minutes.Value / 1440) + "D";
            if (minutes.Value % 60 == 0) return "CODEX / " + (minutes.Value / 60) + "H";
            return "CODEX / " + minutes.Value + "M";
        }

        private void DrawQuotaRow(Graphics graphics, Rectangle track, Rectangle fill, string text,
            string label, Color color, string style)
        {
            using (Brush muted = new SolidBrush(Color.FromArgb(177, 191, 212)))
            using (Brush foreground = new SolidBrush(Color.FromArgb(245, 249, 255)))
            using (Brush trough = new SolidBrush(style == "minimal" ? Color.FromArgb(43, 52, 65) : Color.FromArgb(150, 5, 10, 19)))
            {
                Text(graphics, label, _labelFont, muted, new RectangleF(track.X, track.Y - 20, track.Width - 48, 17), StringAlignment.Near);
                Text(graphics, text, _percentFont, foreground, new RectangleF(track.Right - 60, track.Y - 22, 60, 21), StringAlignment.Far);
                FillRounded(graphics, trough, track, 3f);
                if (fill.Width > 0)
                {
                    if (style == "minimal")
                        using (Brush solid = new SolidBrush(color)) FillRounded(graphics, solid, fill, 3f);
                    else
                        using (Brush gradient = new LinearGradientBrush(track,
                            Color.FromArgb(255, (int)(color.R * .65), (int)(color.G * .75), (int)(color.B * .85)), color, 0f))
                            FillRounded(graphics, gradient, fill, 3f);
                }
            }
        }

        private void DrawDetails(Graphics graphics, string api, string network,
            bool stale, bool available, bool isGo, string style)
        {
            using (Pen separator = new Pen(Color.FromArgb(style == "minimal" ? 75 : 36, 166, 190, 219)))
            using (Brush muted = new SolidBrush(Color.FromArgb(160, 179, 205)))
            using (Brush foreground = new SolidBrush(Color.FromArgb(238, 244, 253)))
            using (Brush dot = new SolidBrush(stale ? Color.FromArgb(158, 168, 185)
                : available ? Color.FromArgb(116, 226, 171) : Color.FromArgb(255, 155, 116)))
            {
                graphics.DrawLine(separator, 18, 80, 302, 80);
                graphics.DrawLine(separator, 161, 92, 161, 121);
                Text(graphics, isGo ? "API / GO" : "API", _labelFont, muted, new RectangleF(18, 87, 112, 13), StringAlignment.Near);
                Text(graphics, "NETWORK", _labelFont, muted, new RectangleF(179, 87, 124, 13), StringAlignment.Near);
                graphics.FillEllipse(dot, 141, 91, 4, 4);
                using (StringFormat format = new StringFormat())
                {
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags = StringFormatFlags.NoWrap;
                    string[] lines = (api ?? "API · --").Replace("\r", "").Split('\n');
                    Font font = isGo ? _apiGoFont : _apiFont;
                    for (int i = 0; i < Math.Min(2, lines.Length); i++)
                        graphics.DrawString(lines[i].Trim(), font, foreground,
                            new RectangleF(18, 102 + 13 * i, 135, 15), format);
                }
                Text(graphics, network ?? "↑-- ↓--", _apiFont, foreground,
                    new RectangleF(179, 102, 124, 25), StringAlignment.Near);
            }
        }

        private static void Text(Graphics graphics, string text, Font font, Brush brush,
            RectangleF bounds, StringAlignment alignment)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = alignment;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }

        private static Rectangle FillRectangle(Rectangle track, int? remaining)
        {
            int percent = remaining.HasValue ? Math.Max(0, Math.Min(100, remaining.Value)) : 0;
            return new Rectangle(track.X, track.Y, track.Width * percent / 100, track.Height);
        }

        private static void FillRounded(Graphics graphics, Brush brush, Rectangle rectangle, float radius)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            using (GraphicsPath path = RoundedRectangle(rectangle, radius)) graphics.FillPath(brush, path);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawRefresh(Graphics graphics, Rectangle hit, float angle, bool refreshing, bool stale)
        {
            Color color = refreshing ? Color.FromArgb(237, 246, 255)
                : stale ? Color.FromArgb(143, 158, 181) : Color.FromArgb(137, 229, 198);
            Rectangle arc = new Rectangle(hit.X + 7, hit.Y + 7, Math.Max(1, hit.Width - 14), Math.Max(1, hit.Height - 14));
            using (Pen pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float start = -45f + (refreshing ? angle : 0f);
                graphics.DrawArc(pen, arc, start, 275f);
                double radians = (start + 275f) * Math.PI / 180d;
                PointF end = new PointF(arc.X + arc.Width / 2f + (float)Math.Cos(radians) * arc.Width / 2f,
                    arc.Y + arc.Height / 2f + (float)Math.Sin(radians) * arc.Height / 2f);
                PointF tangent = new PointF(-(float)Math.Sin(radians), (float)Math.Cos(radians));
                PointF normal = new PointF(-tangent.Y, tangent.X);
                PointF basePoint = new PointF(end.X - tangent.X * 4f, end.Y - tangent.Y * 4f);
                graphics.DrawLine(pen, end, new PointF(basePoint.X + normal.X * 2.5f, basePoint.Y + normal.Y * 2.5f));
                graphics.DrawLine(pen, end, new PointF(basePoint.X - normal.X * 2.5f, basePoint.Y - normal.Y * 2.5f));
            }
        }

        public void Dispose()
        {
            _labelFont.Dispose(); _percentFont.Dispose(); _apiFont.Dispose(); _apiGoFont.Dispose();
        }
    }
}
