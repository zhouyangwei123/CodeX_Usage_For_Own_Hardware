using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace CodexToolsHost.Actions
{
    /// <summary>SendInput 封装：快捷键、文本、音量/媒体键、滚轮、窗口聚焦</summary>
    public static class InputInjector
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint INPUT_MOUSE = 0;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        /* IApplicationActivationManager：直接激活 Appx 应用（比 explorer shell:AppsFolder 可靠） */
        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig] int ActivateApplication(
                [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [In, MarshalAs(UnmanagedType.LPWStr)] string arguments,
                [In] uint options,
                [Out] out uint processId);
            [PreserveSig] int ActivateForFile(
                [In, MarshalAs(UnmanagedType.LPWStr)] string filePath,
                [In, MarshalAs(UnmanagedType.LPWStr)] string callerIsAumid,
                [In] uint options,
                [Out] out uint processId);
            [PreserveSig] int ActivateForProtocol(
                [In, MarshalAs(UnmanagedType.LPWStr)] string protocol,
                [In] IntPtr site,
                [Out] out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ApplicationActivationManager { }

        private const string CodexAumid = "OpenAI.Codex_2p2nqsd0c76g0!App";
        private const uint ActivateOptionsNone = 0x0000;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public const ushort VK_VOLUME_UP = 0xAF;
        public const ushort VK_VOLUME_DOWN = 0xAE;
        public const ushort VK_VOLUME_MUTE = 0xAD;
        public const ushort VK_MEDIA_NEXT = 0xB0;
        public const ushort VK_MEDIA_PREV = 0xB1;
        public const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

        public static void PressKey(ushort vk)
        {
            INPUT[] down = new INPUT[1];
            down[0].type = INPUT_KEYBOARD;
            down[0].U.ki.wVk = vk;
            SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));

            INPUT[] up = new INPUT[1];
            up[0].type = INPUT_KEYBOARD;
            up[0].U.ki.wVk = vk;
            up[0].U.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void WheelScroll(int lines)
        {
            INPUT[] input = new INPUT[1];
            input[0].type = INPUT_MOUSE;
            input[0].U.mi.dwFlags = MOUSEEVENTF_WHEEL;
            input[0].U.mi.mouseData = unchecked((uint)(lines * 120));
            SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char ch in text)
            {
                INPUT[] down = new INPUT[1];
                down[0].type = INPUT_KEYBOARD;
                down[0].U.ki.wVk = 0;
                down[0].U.ki.wScan = (ushort)ch;
                down[0].U.ki.dwFlags = KEYEVENTF_UNICODE;
                SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));

                INPUT[] up = new INPUT[1];
                up[0].type = INPUT_KEYBOARD;
                up[0].U.ki.wVk = 0;
                up[0].U.ki.wScan = (ushort)ch;
                up[0].U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        /// <summary>
        /// 解析组合键并按下。支持写法：Ctrl+Shift+K、Win+D、win+alt+del、Ctrl+Alt+Del 等。
        /// 返回 null 表示成功，否则返回错误说明（如系统安全组合键无法注入）。
        /// </summary>
        public static string PressCombo(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return "快捷键为空";
            ushort modifier = 0;
            ushort key = 0;
            string[] parts = spec.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                string lower = part.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control" || lower == "^") { modifier |= 0x11; continue; }
                if (lower == "alt" || lower == "!") { modifier |= 0x12; continue; }
                if (lower == "shift") { modifier |= 0x10; continue; }
                if (lower == "win" || lower == "windows" || lower == "meta" || lower == "cmd"
                    || lower == "lwin" || lower == "rwin" || lower == "#") { modifier |= 0x5B; continue; }
                key = MapKey(part);
            }
            if (key == 0 && modifier == 0) return "无法识别的快捷键: " + spec;
            if (key == 0) return "快捷键缺少按键: " + spec;

            /* Ctrl+Alt+Del 是 Windows 安全注意序列（SAS），SendInput 无法注入 */
            if ((modifier & 0x11) != 0 && (modifier & 0x12) != 0 && key == 0x2E)
                return "Ctrl+Alt+Del 是系统安全组合键，程序无法注入；请改用 Win+Alt+Del 或其他组合";

            var keys = new List<ushort>();
            if ((modifier & 0x11) != 0) keys.Add(0x11);
            if ((modifier & 0x12) != 0) keys.Add(0x12);
            if ((modifier & 0x10) != 0) keys.Add(0x10);
            if ((modifier & 0x5B) != 0) keys.Add(0x5B);
            keys.Add(key);

            var down = new INPUT[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                down[i].type = INPUT_KEYBOARD;
                down[i].U.ki.wVk = keys[i];
            }
            SendInput((uint)keys.Count, down, Marshal.SizeOf(typeof(INPUT)));

            var up = new INPUT[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                up[i].type = INPUT_KEYBOARD;
                up[i].U.ki.wVk = keys[i];
                up[i].U.ki.dwFlags = KEYEVENTF_KEYUP;
            }
            SendInput((uint)keys.Count, up, Marshal.SizeOf(typeof(INPUT)));
            return null;
        }

        public static bool FocusWindowByTitle(string titlePart)
        {
            if (string.IsNullOrWhiteSpace(titlePart)) return false;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return false;
            ShowWindow(found, 9); /* SW_RESTORE */
            SetForegroundWindow(found);
            BringWindowToTop(found);
            return true;
        }

        private static int _focusBusy;

        /* Codex 桌面版是 Appx 包 OpenAI.Codex（Application Id=App，进程 codex/ChatGPT）；
           窗口可能隐藏/无标题，需要按进程名枚举顶层窗口，找不到时启动应用再聚焦。
           启动+轮询可能耗时数秒，必须在后台线程执行，避免阻塞串口接收线程导致通信断联 */
        public static void FocusCodexAsync(Action<string> onResult)
        {
            if (Interlocked.CompareExchange(ref _focusBusy, 1, 0) != 0)
            {
                if (onResult != null) onResult("正在唤起 Codex…");
                return;
            }
            Task.Run(delegate
            {
                try
                {
                    string msg = FocusCodexCore();
                    if (onResult != null) onResult(msg);
                }
                catch (Exception ex) { if (onResult != null) onResult("唤起异常: " + ex.Message); }
                finally
                {
                    Interlocked.Exchange(ref _focusBusy, 0);
                }
            });
        }

        private static string FocusCodexCore()
        {
            IntPtr hwnd = FindCodexWindow();
            if (hwnd == IntPtr.Zero)
            {
                string launchMsg;
                if (!LaunchCodexApp(out launchMsg)) return launchMsg;
                for (int i = 0; i < 40; i++) /* 最多等待约 12 秒（应用从托盘后台恢复需要时间） */
                {
                    Thread.Sleep(300);
                    hwnd = FindCodexWindow();
                    if (hwnd != IntPtr.Zero) break;
                }
            }
            if (hwnd == IntPtr.Zero)
            {
                /* 应用可能仍驻留系统托盘且未响应激活：双击托盘图标兜底 */
                if (RestoreFromTray())
                {
                    for (int i = 0; i < 28; i++)
                    {
                        Thread.Sleep(300);
                        hwnd = FindCodexWindow();
                        if (hwnd != IntPtr.Zero) break;
                    }
                }
            }
            if (hwnd == IntPtr.Zero)
                return "已请求启动 Codex，但未找到窗口（应用可能在托盘且未响应激活）";

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            string title = "";
            StringBuilder sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) > 0) title = sb.ToString();

            bool wasVisible = IsWindowVisible(hwnd);
            ShowWindow(hwnd, 9); /* SW_RESTORE：最小化/后台恢复 */
            ShowWindow(hwnd, 5); /* SW_SHOW：隐藏窗口兜底显示 */
            ForceForeground(hwnd);
            return "已唤起 Codex (pid=" + pid
                 + (wasVisible ? ", 窗口在后台" : ", 窗口已隐藏")
                 + (title.Length > 0 ? ", 标题=" + title : "") + ")";
        }

        /* Windows 10 通知区域：双击 ChatGPT/Codex 托盘图标，等效点击托盘“打开” */
        private static bool RestoreFromTray()
        {
            /* 通过 UI Automation 找到“Codex/ChatGPT”托盘按钮并激活（主区域 + 折叠区） */
            return RestoreFromTrayUia("Shell_TrayWnd")
                || RestoreFromTrayUia("NotifyIconOverflowWindow");
        }

        private static bool RestoreFromTrayUia(string windowClass)
        {
            try
            {
                AutomationElement host = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, windowClass));
                if (host == null) return false;

                var buttons = host.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement btn in buttons)
                {
                    string name;
                    try { name = btn.Current.Name ?? ""; }
                    catch (Exception) { continue; }
                    if (name.IndexOf("codex", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    /* 优先 Invoke；托盘按钮通常不支持，退回真实鼠标双击 */
                    try
                    {
                        object pattern;
                        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            return true;
                        }
                    }
                    catch (Exception) { }

                    System.Windows.Rect r = btn.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
                    int cx = (int)(r.Left + r.Width / 2);
                    int cy = (int)(r.Top + r.Height / 2);
                    SetCursorPos(cx, cy);
                    Thread.Sleep(120);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(60);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /* 后台线程调用 SetForegroundWindow 可能被 Windows 前台锁定拒绝，
           用 AttachThreadInput + TOPMOST 技巧强制置前 */
        private static void ForceForeground(IntPtr hwnd)
        {
            uint targetThread;
            GetWindowThreadProcessId(hwnd, out targetThread);
            IntPtr fg = GetForegroundWindow();
            uint fgThread = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgThread);
            bool attached = false;
            if (fgThread != 0 && fgThread != targetThread)
                attached = AttachThreadInput(fgThread, targetThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(fgThread, targetThread, false);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        private static IntPtr FindCodexWindow()
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string proc = "";
                try
                {
                    using (Process p = Process.GetProcessById((int)pid))
                        proc = p.ProcessName;
                }
                catch (Exception) { }
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                bool procMatch = proc.Equals("codex", StringComparison.OrdinalIgnoreCase)
                              || proc.Equals("chatgpt", StringComparison.OrdinalIgnoreCase);
                bool titleMatch = title.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0
                               || title.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!procMatch && !titleMatch) return true;
                /* 标题匹配 > 进程名匹配 > 有标题 > 可见，避免选中隐藏的无标题辅助窗口 */
                int score = (titleMatch ? 8 : 0) + (procMatch ? 4 : 0)
                          + (title.Length > 0 ? 2 : 0) + (IsWindowVisible(hWnd) ? 1 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hWnd;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static bool LaunchCodexApp(out string message)
        {
            message = null;
            try
            {
                try
                {
                    var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                    uint pid;
                    int hr = mgr.ActivateApplication(CodexAumid, "", ActivateOptionsNone, out pid);
                    if (hr >= 0)
                    {
                        message = "已激活 Codex 应用进程 pid=" + pid;
                        return true;
                    }
                    message = "应用激活失败(0x" + hr.ToString("X8") + ")，改用资源管理器启动";
                }
                catch (Exception ex) { message = "应用激活异常: " + ex.Message; }
                try
                {
                    Process.Start("explorer.exe", "shell:AppsFolder\\" + CodexAumid);
                    message = "已通过资源管理器请求启动 Codex";
                    return true;
                }
                catch (Exception ex)
                {
                    message = "启动 Codex 失败: " + ex.Message;
                    return false;
                }
            }
            catch (Exception ex) { message = "启动 Codex 失败: " + ex.Message; return false; }
        }

        private static ushort MapKey(string part)
        {
            if (part.Length == 0) return 0;
            string lower = part.ToLowerInvariant();
            if (lower.StartsWith("f") && lower.Length >= 2 && lower.Length <= 3)
            {
                int n;
                if (int.TryParse(part.Substring(1), out n) && n >= 1 && n <= 24)
                    return (ushort)(0x70 + n - 1);
            }
            switch (lower)
            {
                case "enter": case "return": return 0x0D;
                case "space": return 0x20;
                case "tab": return 0x09;
                case "esc": case "escape": return 0x1B;
                case "back": case "backspace": return 0x08;
                case "delete": case "del": return 0x2E;
                case "insert": case "ins": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "pgup": return 0x21;
                case "pagedown": case "pgdn": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "capslock": return 0x14;
                case "printscreen": return 0x2C;
                case "pause": return 0x13;
                case "plus": case "=": return 0xBB;
                case "minus": case "-": return 0xBD;
                case "comma": case ",": return 0xBC;
                case "period": case ".": return 0xBE;
                case "slash": case "/": return 0xBF;
                case "semicolon": case ";": return 0xBA;
                case "quote": case "'": return 0xDE;
                case "backslash": case "\\": return 0xDC;
                case "bracketleft": case "[": return 0xDB;
                case "bracketright": case "]": return 0xDD;
                case "backtick": case "`": return 0xC0;
            }
            if (part.Length == 1)
            {
                char c = part[0];
                if (c >= 'a' && c <= 'z') return (ushort)(c - 'a' + 'A');
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
            }
            return 0;
        }
    }
}
