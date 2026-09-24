using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodexToolsHost.Actions
{
    internal interface IChatGptAppRuntime
    {
        bool IsRunning { get; }
        bool TryClose(out string error);
        bool WaitForExit(int timeoutMs);
        bool TryTerminate(out string error);
        bool TryLaunch(out string error);
        bool WaitForWindow(int timeoutMs);
    }

    /// <summary>Windows 10/11 ChatGPT/Codex 桌面应用的发现、关闭和启动适配层。</summary>
    internal sealed class WindowsChatGptAppRuntime : IChatGptAppRuntime
    {
        private const string KnownAppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
        private const int WmClose = 0x0010;
        private const uint ActivateOptionsNone = 0x0000;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint message,
            IntPtr wParam, IntPtr lParam);

        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            [PreserveSig]
            int ActivateApplication(
                [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [In, MarshalAs(UnmanagedType.LPWStr)] string arguments,
                [In] uint options,
                [Out] out uint processId);

            [PreserveSig]
            int ActivateForFile(
                [In, MarshalAs(UnmanagedType.LPWStr)] string filePath,
                [In, MarshalAs(UnmanagedType.LPWStr)] string callerIsAumid,
                [In] uint options,
                [Out] out uint processId);

            [PreserveSig]
            int ActivateForProtocol(
                [In, MarshalAs(UnmanagedType.LPWStr)] string protocol,
                [In] IntPtr site,
                [Out] out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ApplicationActivationManager
        {
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public bool IsRunning
        {
            get { return GetDesktopProcessIds().Count > 0; }
        }

        public bool TryClose(out string error)
        {
            error = "";
            List<int> processIds = GetDesktopProcessIds();
            if (processIds.Count == 0) return true;

            bool sent = false;
            foreach (int processId in processIds)
            {
                IntPtr window = FindWindowForProcess(processId);
                if (window == IntPtr.Zero) continue;

                try
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        if (process.CloseMainWindow()) sent = true;
                    }
                }
                catch (Exception)
                {
                    /* MainWindowHandle may be hidden for a packaged app; use WM_CLOSE below. */
                }

                if (PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
                    sent = true;
            }

            if (!sent) error = "未找到可安全关闭的 ChatGPT APP 窗口";
            return sent;
        }

        public bool WaitForExit(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            do
            {
                if (!IsRunning) return true;
                Thread.Sleep(200);
            }
            while (DateTime.UtcNow < deadline);
            return !IsRunning;
        }

        public bool TryTerminate(out string error)
        {
            error = "";
            List<int> processIds = GetDesktopProcessIds();
            if (processIds.Count == 0) return true;

            bool terminated = false;
            foreach (int processId in processIds)
            {
                try
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        if (process.HasExited) continue;
                        process.Kill();
                        terminated = true;
                    }
                }
                catch (Exception) { }
            }

            if (!terminated) error = "未能终止驻留的 ChatGPT APP 进程";
            return terminated;
        }

        public bool TryLaunch(out string error)
        {
            error = "";
            List<string> appIds = FindRegisteredAppIds();
            if (!appIds.Contains(KnownAppUserModelId)) appIds.Add(KnownAppUserModelId);

            foreach (string appId in appIds)
            {
                if (TryActivateApp(appId)) return true;
                if (TryOpenAppsFolder(appId)) return true;
            }

            error = "未找到已注册的 ChatGPT/Codex 应用入口";
            return false;
        }

        public bool WaitForWindow(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
            do
            {
                if (FindChatGptWindow() != IntPtr.Zero) return true;
                Thread.Sleep(300);
            }
            while (DateTime.UtcNow < deadline);
            return FindChatGptWindow() != IntPtr.Zero;
        }

        private static List<int> GetDesktopProcessIds()
        {
            var ids = new List<int>();
            AddProcessIds("ChatGPT", ids);
            AddProcessIds("codex", ids);
            return ids;
        }

        private static void AddProcessIds(string processName, List<int> ids)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch (Exception) { return; }

            foreach (Process process in processes)
            {
                try
                {
                    if (IsPackagedOpenAiProcess(process))
                        if (!ids.Contains(process.Id)) ids.Add(process.Id);
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
        }

        private static bool IsPackagedOpenAiProcess(Process process)
        {
            try
            {
                string path = process.MainModule == null ? "" : process.MainModule.FileName;
                string name = process.ProcessName;
                bool supportedName = name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("codex", StringComparison.OrdinalIgnoreCase);
                return supportedName
                    && path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0
                    && path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IntPtr FindChatGptWindow()
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;
            List<int> desktopProcessIds = GetDesktopProcessIds();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);
                if (!desktopProcessIds.Contains((int)processId)) return true;

                StringBuilder titleBuilder = new StringBuilder(256);
                GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString();
                bool titleMatch = title.IndexOf("chatgpt", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0;
                int score = (titleMatch ? 8 : 0) + (title.Length > 0 ? 2 : 0)
                    + (IsWindowVisible(hWnd) ? 1 : 0);
                if (score > bestScore)
                {
                    best = hWnd;
                    bestScore = score;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static IntPtr FindWindowForProcess(int processId)
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint currentProcessId;
                GetWindowThreadProcessId(hWnd, out currentProcessId);
                if ((int)currentProcessId != processId) return true;

                StringBuilder titleBuilder = new StringBuilder(256);
                GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString();
                int score = (title.Length > 0 ? 2 : 0) + (IsWindowVisible(hWnd) ? 1 : 0);
                if (score > bestScore)
                {
                    best = hWnd;
                    bestScore = score;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static List<string> FindRegisteredAppIds()
        {
            var appIds = new List<string>();
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -NonInteractive -Command \""
                        + "Get-StartApps | Where-Object { $_.Name -match '^(ChatGPT|Codex)' } "
                        + "| Select-Object -ExpandProperty AppID\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null) return appIds;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(3000);
                    string[] lines = output.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string appId = line.Trim();
                        if (appId.Length > 0 && !appIds.Contains(appId)) appIds.Add(appId);
                    }
                }
            }
            catch (Exception) { }
            return appIds;
        }

        private static bool TryActivateApp(string appId)
        {
            IApplicationActivationManager manager = null;
            try
            {
                manager = (IApplicationActivationManager)new ApplicationActivationManager();
                uint processId;
                return manager.ActivateApplication(appId, "", ActivateOptionsNone,
                    out processId) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (manager != null)
                {
                    try { Marshal.ReleaseComObject(manager); }
                    catch (Exception) { }
                }
            }
        }

        private static bool TryOpenAppsFolder(string appId)
        {
            try
            {
                Process.Start("explorer.exe", "shell:AppsFolder\\" + appId);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
