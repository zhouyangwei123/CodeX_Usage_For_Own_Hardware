using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexToolsHost.UI
{
    // Desktop floor only: ordinary applications may cover an unpinned HUD.
    // Reuse the HUD's one-second display tick, with no Explorer injection,
    // ownership changes, foreground activation, or additional UI controls.
    internal static class HudDesktopLayer
    {
        internal static void ExcludeFromPeek(IntPtr window)
        {
            int enabled = 1;
            DwmSetWindowAttribute(window, 12 /* DWMWA_EXCLUDED_FROM_PEEK */, ref enabled, 4);
        }

        internal static void EnsureAboveDesktop(IntPtr window)
        {
            IntPtr desktop = FindDesktopSurface();
            if (desktop != IntPtr.Zero) RaiseAbove(window, desktop);
        }

        private static IntPtr FindDesktopSurface()
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return IntPtr.Zero;
            uint shellProcess;
            GetWindowThreadProcessId(shell, out shellProcess);
            IntPtr desktop = IntPtr.Zero;
            // Current Windows puts the desktop view under Progman; older builds
            // can host it under a top-level WorkerW belonging to the same shell.
            EnumWindows(delegate(IntPtr candidate, IntPtr unused)
            {
                if (!IsWindowVisible(candidate)) return true;
                if (candidate == shell) { desktop = candidate; return false; }
                uint process;
                GetWindowThreadProcessId(candidate, out process);
                if (process != shellProcess) return true;
                var name = new StringBuilder(32);
                GetClassName(candidate, name, name.Capacity);
                if (name.ToString() == "WorkerW" && FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                { desktop = candidate; return false; }
                return true;
            }, IntPtr.Zero);
            return desktop;
        }

        internal static void RaiseAbove(IntPtr window, IntPtr desktop)
        {
            if (window == desktop || !IsWindowVisible(window) || !IsWindowVisible(desktop)
                || (GetWindowLong(window, -20) & 8) != 0 || (GetWindowLong(desktop, -20) & 8) != 0) return;
            bool coveredByDesktop = false;
            EnumWindows(delegate(IntPtr candidate, IntPtr unused)
            {
                if (candidate == window) return false;
                if (candidate == desktop) { coveredByDesktop = true; return false; }
                return true;
            }, IntPtr.Zero);
            if (!coveredByDesktop) return;
            IntPtr previous = GetWindow(desktop, 3 /* GW_HWNDPREV */);
            // Inserting after a topmost HWND can promote us into its band.
            if (previous != IntPtr.Zero && (GetWindowLong(previous, -20) & 8) != 0) previous = IntPtr.Zero;
            SetWindowPos(window, previous, 0, 0, 0, 0, 0x0213 /* NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER */);
        }

        private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string title);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
