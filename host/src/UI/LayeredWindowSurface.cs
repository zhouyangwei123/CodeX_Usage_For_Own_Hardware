using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexToolsHost.UI
{
    // Native resources live for one upload only. Copy exact premultiplied rows into a
    // top-down 32bpp DIB; no screen capture or GetHbitmap alpha conversion is involved.
    internal static class LayeredWindowSurface
    {
        public const int ExtendedStyle = 0x00080000;

        public static bool TryPresent(IntPtr window, Point location, Bitmap frame, byte opacity)
        {
            if (window == IntPtr.Zero || frame == null || frame.PixelFormat != PixelFormat.Format32bppPArgb)
                return false;
            IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            BitmapData locked = null;
            try
            {
                dc = CreateCompatibleDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return false;
                var info = new BitmapInfo();
                info.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
                info.Header.Width = frame.Width; info.Header.Height = -frame.Height;
                info.Header.Planes = 1; info.Header.BitCount = 32;
                IntPtr pixels;
                bitmap = CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) return false;
                locked = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                int rowSize = checked(frame.Width * 4);
                byte[] row = new byte[rowSize];
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, rowSize);
                    Marshal.Copy(row, 0, IntPtr.Add(pixels, y * rowSize), rowSize);
                }
                frame.UnlockBits(locked); locked = null;
                previous = SelectObject(dc, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return false;
                var destination = new NativePoint { X = location.X, Y = location.Y };
                var source = new NativePoint();
                var size = new NativeSize { Width = frame.Width, Height = frame.Height };
                var blend = new BlendFunction { SourceConstantAlpha = opacity, AlphaFormat = 1 };
                return UpdateLayeredWindow(window, IntPtr.Zero, ref destination, ref size,
                    dc, ref source, 0, ref blend, 2);
            }
            catch (ExternalException) { return false; }
            finally
            {
                if (locked != null) frame.UnlockBits(locked);
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (dc != IntPtr.Zero) DeleteDC(dc);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction
        { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        { public BitmapInfoHeader Header; public uint Colors; }
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc,
            ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc,
            ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source,
            int colorKey, ref BlendFunction blend, int flags);
    }
}
