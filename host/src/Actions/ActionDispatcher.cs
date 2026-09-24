using System;
using System.Diagnostics;
using System.Globalization;
using CodexToolsHost.Model;

namespace CodexToolsHost.Actions
{
    public interface IHostService
    {
        void CycleOledPage();
        void SetRgbMode(string mode);
        void RefreshQuota();
        void ShowSettings();
        void ToggleDeepSeek();
        void RestartChatGpt();
        void RunMijiaShortcut(int index);
        void Toast(string message);
    }

    /// <summary>白名单动作分发（不执行任意命令）</summary>
    public static class ActionDispatcher
    {
        public static void Execute(ActionSpec spec, IHostService host)
        {
            if (spec == null || host == null) return;
            switch ((spec.Action ?? "none").ToLowerInvariant())
            {
                case "none":
                    break;
                case "hotkey":
                    {
                        string err = InputInjector.PressCombo(spec.Param);
                        if (err != null) host.Toast(err);
                    }
                    break;
                case "text":
                    InputInjector.TypeText(spec.Param);
                    break;
                case "launch":
                    if (!string.IsNullOrWhiteSpace(spec.Param))
                    {
                        try { Process.Start(spec.Param); }
                        catch (Exception ex) { host.Toast("启动失败: " + ex.Message); }
                    }
                    break;
                case "focus":
                    if (!InputInjector.FocusWindowByTitle(spec.Param))
                        host.Toast("未找到窗口: " + spec.Param);
                    break;
                case "codexfocus":
                case "launchcodex":
                    /* 启动/唤起 Codex 可能等待数秒，放到后台线程，避免阻塞串口接收线程 */
                    InputInjector.FocusCodexAsync(host.Toast);
                    break;
                case "restartchatgpt":
                    host.RestartChatGpt();
                    break;
                case "volumeup":
                    Repeat(ParseSteps(spec.Param), InputInjector.VK_VOLUME_UP);
                    break;
                case "volumedown":
                    Repeat(ParseSteps(spec.Param), InputInjector.VK_VOLUME_DOWN);
                    break;
                case "volumemute":
                    InputInjector.PressKey(InputInjector.VK_VOLUME_MUTE);
                    break;
                case "mediaplaypause":
                    InputInjector.PressKey(InputInjector.VK_MEDIA_PLAY_PAUSE);
                    break;
                case "medianext":
                    InputInjector.PressKey(InputInjector.VK_MEDIA_NEXT);
                    break;
                case "mediaprev":
                    InputInjector.PressKey(InputInjector.VK_MEDIA_PREV);
                    break;
                case "scrollup":
                    InputInjector.WheelScroll(ParseSteps(spec.Param));
                    break;
                case "scrolldown":
                    InputInjector.WheelScroll(-ParseSteps(spec.Param));
                    break;
                case "cycleoled":
                    host.CycleOledPage();
                    break;
                case "rgbstatus":
                    host.SetRgbMode("status");
                    break;
                case "rgbsolid":
                    host.SetRgbMode("solid");
                    break;
                case "rgbbreath":
                    host.SetRgbMode("breath");
                    break;
                case "rgbrainbow":
                    host.SetRgbMode("rainbow");
                    break;
                case "rgbwave":
                    host.SetRgbMode("wave");
                    break;
                case "rgbblink":
                    host.SetRgbMode("blink");
                    break;
                case "rgboff":
                    host.SetRgbMode("off");
                    break;
                case "rgbtest":
                    host.SetRgbMode("test");
                    break;
                case "refreshquota":
                    host.RefreshQuota();
                    break;
                case "showsettings":
                    host.ShowSettings();
                    break;
                case "toggledeepseek":
                    host.ToggleDeepSeek();
                    break;
                case "mijia1": host.RunMijiaShortcut(0); break;
                case "mijia2": host.RunMijiaShortcut(1); break;
                case "mijia3": host.RunMijiaShortcut(2); break;
                case "mijia4": host.RunMijiaShortcut(3); break;
                case "mijia5": host.RunMijiaShortcut(4); break;
                case "mijia6": host.RunMijiaShortcut(5); break;
                case "mijia7": host.RunMijiaShortcut(6); break;
                case "mijia8": host.RunMijiaShortcut(7); break;
            }
        }

        private static int ParseSteps(string param)
        {
            int steps;
            if (!int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out steps) || steps < 1)
                return 1;
            if (steps > 20) steps = 20;
            return steps;
        }

        private static void Repeat(int count, ushort vk)
        {
            for (int i = 0; i < count; i++) InputInjector.PressKey(vk);
        }
    }
}
