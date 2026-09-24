using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private readonly Font _font = new Font("Segoe UI", 8f,
            FontStyle.Bold, GraphicsUnit.Point);

        public Size PreferredSize { get { return new Size(320, 78); } }

        public QuotaHudLayout CalculateLayout(Rectangle bounds, QuotaSnapshot snapshot)
        {
            if (snapshot == null) snapshot = QuotaSnapshot.EmptyStale();
            Rectangle primary = new Rectangle(bounds.Left + 18, bounds.Top + 14,
                Math.Max(1, bounds.Width - 66), 18);
            Rectangle secondary = new Rectangle(bounds.Left + 18, bounds.Top + 46,
                Math.Max(1, bounds.Width - 66), 18);
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
            return new Rectangle(bounds.Right - 40,
                bounds.Top + (bounds.Height - 30) / 2, 30, 30);
        }

        public void Draw(Graphics graphics, Rectangle bounds, QuotaSnapshot snapshot,
            float animationAngle, bool refreshing, bool stale)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            if (snapshot == null) snapshot = QuotaSnapshot.EmptyStale();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            QuotaHudLayout layout = CalculateLayout(bounds, snapshot);

            using (Brush panel = new SolidBrush(Color.FromArgb(22, 27, 36)))
            using (Brush track = new SolidBrush(Color.FromArgb(7, 10, 15)))
            using (Brush red = new SolidBrush(snapshot.PrimaryRemainingPercent.HasValue
                && snapshot.PrimaryRemainingPercent.Value < 20
                ? Color.FromArgb(255, 70, 92) : Color.FromArgb(237, 49, 76)))
            using (Brush blue = new SolidBrush(snapshot.SecondaryRemainingPercent.HasValue
                && snapshot.SecondaryRemainingPercent.Value < 20
                ? Color.FromArgb(76, 167, 255) : Color.FromArgb(45, 119, 229)))
            using (Brush text = new SolidBrush(Color.FromArgb(245, 247, 251)))
            {
                FillRounded(graphics, panel,
                    new Rectangle(bounds.X + 1, bounds.Y + 1,
                        Math.Max(1, bounds.Width - 2), Math.Max(1, bounds.Height - 2)), 13);
                FillRounded(graphics, track, layout.PrimaryTrack, 8);
                FillRounded(graphics, track, layout.SecondaryTrack, 8);
                FillRounded(graphics, red, layout.PrimaryFill, 8);
                FillRounded(graphics, blue, layout.SecondaryFill, 8);
                DrawCenteredText(graphics, layout.PrimaryText, _font, text, layout.PrimaryTrack);
                DrawCenteredText(graphics, layout.SecondaryText, _font, text, layout.SecondaryTrack);
            }

            DrawRefresh(graphics, layout.RefreshHit, animationAngle,
                refreshing, stale || snapshot.IsStale);
        }

        private static Rectangle FillRectangle(Rectangle track, int? remaining)
        {
            int percent = remaining.HasValue
                ? Math.Max(0, Math.Min(100, remaining.Value)) : 0;
            return new Rectangle(track.X, track.Y, track.Width * percent / 100, track.Height);
        }

        private static void FillRounded(Graphics graphics, Brush brush,
            Rectangle rectangle, int radius)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            int safeRadius = Math.Max(1,
                Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2));
            using (GraphicsPath path = RoundedRectangle(rectangle, safeRadius))
                graphics.FillPath(brush, path);
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top,
                diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter,
                diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawCenteredText(Graphics graphics, string text, Font font,
            Brush brush, Rectangle bounds)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }

        private static void DrawRefresh(Graphics graphics, Rectangle hit, float angle,
            bool refreshing, bool stale)
        {
            Color color = stale ? Color.FromArgb(145, 151, 164)
                : refreshing ? Color.FromArgb(210, 225, 238)
                : Color.FromArgb(118, 255, 167);
            Rectangle arc = new Rectangle(hit.X + 6, hit.Y + 6,
                Math.Max(1, hit.Width - 12), Math.Max(1, hit.Height - 12));
            using (Pen pen = new Pen(color, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float start = -45f + (refreshing ? angle : 0f);
                graphics.DrawArc(pen, arc, start, 275f);
                double radians = (start + 275f) * Math.PI / 180d;
                PointF end = new PointF(
                    arc.X + arc.Width / 2f + (float)Math.Cos(radians) * arc.Width / 2f,
                    arc.Y + arc.Height / 2f + (float)Math.Sin(radians) * arc.Height / 2f);
                PointF tangent = new PointF(-(float)Math.Sin(radians),
                    (float)Math.Cos(radians));
                PointF normal = new PointF(-tangent.Y, tangent.X);
                PointF basePoint = new PointF(end.X - tangent.X * 4f,
                    end.Y - tangent.Y * 4f);
                graphics.DrawLine(pen, end,
                    new PointF(basePoint.X + normal.X * 2.5f,
                        basePoint.Y + normal.Y * 2.5f));
                graphics.DrawLine(pen, end,
                    new PointF(basePoint.X - normal.X * 2.5f,
                        basePoint.Y - normal.Y * 2.5f));
            }
        }

        public void Dispose()
        {
            _font.Dispose();
        }
    }
}
