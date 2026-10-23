using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;

namespace CodexToolsHost.UI
{
    // Font bytes are copied from oled.c / usage_oled.c; cross-language tests check them.
    internal static class OledPixelRenderer
    {
        internal const int FramePadding = 4;
        internal static Size LogicalSize(bool expanded) { return new Size(136, expanded ? 72 : 36); }
        private const string FontBytes = "AAAAAAAAAF8AAAAHAAcAFH8UfxQkKn8qEiMTCGRiNklVIlAABQMAAAAcIkEAAEEiHAAUCD4IFAgIPggIAFAwAAAICAgICABgYAAAIBAIBAI+UUlFPgBCf0AAQmFRSUYhQUVLMRgUEn8QJ0VFRTk8SklJMAFxCQUDNklJSTYGSUkpHgA2NgAAAFY2AAAIFCJBABQUFBQUAEEiFAgCAVEJBjJJeUE+fhEREX5/SUlJNj5BQUEif0FBIhx/SUlJQX8JCQkBPkFJSXp/CAgIfwBBf0EAIEBBPwF/CBQiQX9AQEBAfwIMAn9/BAgQfz5BQUE+fwkJCQY+QVEhXn8JGSlGRklJSTEBAX8BAT9AQEA/HyBAIB8/QDhAP2MUCBRjBwhwCAdhUUlFQwB/QUEAAgQIECAAQUF/AAQCAQIEQEBAQEAAAQIEACBUVFR4f0hERDg4REREIDhEREh/OFRUVBgIfgkBAgxSUlI+fwgEBHgARH1AACBARD0AfxAoRAAAQX9AAHwEGAR4fAgEBHg4REREOHwUFBQICBQUGHx8CAQECEhUVFQgBD9EQCA8QEAgfBwgQCAcPEAwQDxEKBAoRAxQUFA8RGRUTEQACDZBAAAAfwAAAEE2CAAIBAgQCD5BQUE+";
        private const string SmallBytes = "HxEfEh8QHRUXFRUfBwQfFxUdHxUdAQEfHxUfFxUfHwoRHwYfHxEdABAABAQEAh8CCB8IAAAAHgUeHwUHER8RGAQDHgIeHwQYDBIfHxAQAR8BHwUBHwIfHxURABcA";
        private static readonly byte[] Font = Convert.FromBase64String(FontBytes);
        private static readonly byte[] Small = Convert.FromBase64String(SmallBytes);
        private const string Symbols = "0123456789KMG.-UD API/mhdLTFNE!";

        internal static string Network(char prefix, uint kib, bool valid)
        {
            if (!valid) return prefix + "--";
            if (kib < 1000) return prefix + kib.ToString() + "K";
            if (kib < 10UL * 1024) return prefix + (Math.Min(99, ((ulong)kib * 10 + 512) / 1024) / 10m).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (kib < 1000UL * 1024) return prefix + Math.Min(999, ((ulong)kib + 512) / 1024).ToString() + "M";
            if (kib < 10UL * 1024 * 1024) return prefix + (Math.Min(99, ((ulong)kib * 10 + 524288) / 1048576) / 10m).ToString("0.0", CultureInfo.InvariantCulture) + "G";
            return prefix + Math.Min(999, ((ulong)kib + 524288) / 1048576).ToString() + "G";
        }
        private sealed class Surface
        {
            internal readonly byte[] Bits = new byte[1024];
            internal void Pixel(int x, int y)
            { if (x >= 0 && x < 128 && y >= 0 && y < 64) Bits[x + y / 8 * 128] |= (byte)(1 << (y % 8)); }
            internal void Rect(int x, int y, int w, int h)
            { for (int px = x; px < x + w; px++) for (int py = y; py < y + h; py++) Pixel(px, py); }
            internal void Line(int x, int y, int endX, int endY)
            {
                int dx = Math.Abs(endX - x), sx = x < endX ? 1 : -1, dy = Math.Abs(endY - y), sy = y < endY ? 1 : -1, error = dx - dy;
                for (;;) { Pixel(x, y); if (x == endX && y == endY) return; int twice = error * 2; if (twice > -dy) { error -= dy; x += sx; } if (twice < dx) { error += dx; y += sy; } }
            }
            internal void Text(int x, int y, string value, bool small = false)
            {
                foreach (char ch in value)
                {
                    int index = small ? Symbols.IndexOf(ch) : ch >= 32 && ch <= 127 ? ch - 32 : '?' - 32;
                    int width = small ? 3 : 5, height = small ? 5 : 7;
                    if (index >= 0)
                        for (int col = 0; col < width; col++) for (int row = 0; row < height; row++)
                            if (((small ? Small : Font)[index * width + col] & (1 << row)) != 0) Pixel(x + col, y + row);
                    x += width + 1;
                }
            }
        }
        internal static byte[] RenderBits(OledUsageFrame frame, string upload, string download)
        {
            var s = new Surface();
            bool quota = frame.Has(OledUsageFrame.QuotaKnown) && !frame.Has(OledUsageFrame.QuotaStale);
            bool usage = frame.Has(OledUsageFrame.UsageKnown) && !frame.Has(OledUsageFrame.UsageStale);
            s.Text(2, 2, quota ? "7D " + frame.Remaining + "%" : "7D --");
            uint seconds = frame.ResetSeconds;
            string reset = !quota || !frame.Has(OledUsageFrame.ResetKnown) ? "--" : seconds >= 8640000 ? ">99d" : seconds >= 86400
                ? seconds / 86400 + "d" + (seconds % 86400 / 3600).ToString("00") + "h" : seconds >= 3600
                ? seconds / 3600 + "h" + (seconds % 3600 / 60).ToString("00") + "m" : seconds / 60 + "m";
            s.Text(46, 2, reset); s.Text(82, 4, upload, true); s.Text(105, 4, download, true);
            if (usage && frame.Has(OledUsageFrame.Partial)) s.Text(77, 2, "!", true);
            s.Line(2, 10, 125, 10); s.Line(2, 13, 125, 13); s.Line(2, 10, 2, 13); s.Line(125, 10, 125, 13);
            if (quota) s.Rect(3, 11, (frame.Remaining * 122 + 50) / 100, 2);
            if (usage)
            {
                int[] rows = { 17, 34, 51 }, labels = { 17, 32, 47 };
                string[] ticks = { OledUsageFrame.FormatCount(frame.Upper), frame.Upper == 1 ? "0.5" : frame.Upper == 5 ? "2.5" : OledUsageFrame.FormatCount(frame.Upper / 2), "0" };
                s.Line(22, 17, 22, 51);
                for (int i = 0; i < 3; i++)
                { s.Text(2, labels[i], ticks[i], true); s.Line(22, rows[i], 24, rows[i]); for (int x = 28; x <= 125; x += 8) s.Pixel(x, rows[i]); }
                int priorX = -1, priorY = 0;
                for (int i = 0; i < frame.Points.Length; i++)
                {
                    if (frame.Points[i] == 255) { priorX = -1; continue; }
                    int x = 24 + (i * 101 + (frame.Points.Length - 1) / 2) / (frame.Points.Length - 1), y = 51 - frame.Points[i];
                    if (priorX >= 0) s.Line(priorX, priorY, x, y); else s.Pixel(x, y);
                    priorX = x; priorY = y;
                    if (i == frame.Points.Length - 1) s.Rect(x - 1, y <= 17 ? 17 : y >= 51 ? 50 : y - 1, 2, 2);
                }
            }
            else s.Text(34, 31, frame.Has(OledUsageFrame.UsageStale) ? "DATA STALE" : "NO DATA");
            s.Text(2, 56, frame.CurrentText()); s.Text(44, 58, frame.DetailText(), true);
            return s.Bits;
        }
        internal static Bitmap Render(Size size, OledUsageFrame frame, PcMetricsSnapshot pc, bool expanded)
        {
            bool network = pc != null && pc.NetworkSpeedAvailable && !pc.IsStale && DateTimeOffset.UtcNow - pc.SampledAtUtc <= TimeSpan.FromSeconds(10);
            byte[] bits = RenderBits(frame, Network('U', pc == null ? 0 : pc.NetworkUploadKiBPerSecond, network), Network('D', pc == null ? 0 : pc.NetworkDownloadKiBPerSecond, network));
            Size logical = LogicalSize(expanded);
            using (var source = new Bitmap(logical.Width, logical.Height, PixelFormat.Format32bppPArgb))
            {
                DrawBezel(source);
                for (int y = 0; y < (expanded ? 64 : 28); y++) for (int x = 0; x < 128; x++)
                {
                    int originalY = !expanded && y >= 20 ? y + 36 : y;
                    bool lit = (expanded || y < 14 || y >= 20) && (bits[x + originalY / 8 * 128] & (1 << (originalY % 8))) != 0;
                    source.SetPixel(x + FramePadding, y + FramePadding, lit ? Color.White : Color.Black);
                }
                var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(result))
                { g.Clear(Color.Transparent); g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(source, new Rectangle(Point.Empty, size)); }
                return result;
            }
        }
        private static void DrawBezel(Bitmap image)
        {
            Color face = Color.FromArgb(27,36,43), rim = Color.FromArgb(111,130,140), tile = Color.FromArgb(64,81,90);
            int w = image.Width, h = image.Height;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int edgeX = Math.Min(x, w - 1 - x), edgeY = Math.Min(y, h - 1 - y);
                if (edgeX + edgeY < 3) continue;
                Color color = face;
                if (edgeX == 0 || edgeY == 0 || edgeX + edgeY == 3) color = rim;
                else if ((edgeY == 1 && x % 6 < 2) || (edgeX == 1 && y % 6 < 2)) color = tile;
                image.SetPixel(x, y, color);
            }
        }
    }
}
