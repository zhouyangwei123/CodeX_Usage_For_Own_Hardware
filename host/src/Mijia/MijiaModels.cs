using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CodexToolsHost.Mijia
{
    public sealed class MijiaScene
    {
        public MijiaScene() { Id = ""; Name = ""; }
        public MijiaScene(string id, string name) { Id = id ?? ""; Name = name ?? ""; }
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class MijiaShortcutConfig
    {
        public MijiaShortcutConfig() { Name = ""; SceneId = ""; SceneName = ""; }
        public MijiaShortcutConfig(string name, string sceneId, string sceneName)
        {
            Name = name ?? "";
            SceneId = sceneId ?? "";
            SceneName = sceneName ?? "";
        }
        public string Name { get; set; }
        public string SceneId { get; set; }
        public string SceneName { get; set; }
    }

    public sealed class MijiaSettings
    {
        public const int ShortcutCount = 8;
        public static readonly int[] RefreshIntervalMinutes = { 5, 15, 30, 60 };

        public MijiaSettings()
        {
            ExecutablePath = "";
            AuthPath = "";
            AutoRefresh = true;
            RefreshMinutes = 15;
            Shortcuts = NormalizeShortcuts(null);
        }

        public string ExecutablePath { get; set; }
        public string AuthPath { get; set; }
        public bool AutoRefresh { get; set; }
        public int RefreshMinutes { get; set; }
        public MijiaShortcutConfig[] Shortcuts { get; set; }

        public static int NormalizeRefreshMinutes(int value)
        {
            foreach (int allowed in RefreshIntervalMinutes)
                if (value == allowed) return value;
            return 15;
        }

        public static MijiaShortcutConfig[] NormalizeShortcuts(
            MijiaShortcutConfig[] shortcuts)
        {
            var normalized = new MijiaShortcutConfig[ShortcutCount];
            for (int index = 0; index < ShortcutCount; index++)
            {
                MijiaShortcutConfig source = shortcuts != null && index < shortcuts.Length
                    ? shortcuts[index] : null;
                normalized[index] = new MijiaShortcutConfig(
                    source == null || string.IsNullOrEmpty(source.Name)
                        ? "快捷" + (index + 1) : source.Name,
                    source == null ? "" : source.SceneId,
                    source == null ? "" : source.SceneName);
            }
            return normalized;
        }
    }

    public sealed class MijiaCommandResult
    {
        public MijiaCommandResult(int exitCode, string standardOutput,
            string standardError, bool timedOut)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? "";
            StandardError = standardError ?? "";
            TimedOut = timedOut;
        }

        public int ExitCode { get; private set; }
        public string StandardOutput { get; private set; }
        public string StandardError { get; private set; }
        public bool TimedOut { get; private set; }
    }

    public sealed class MijiaShortcutExecutionResult
    {
        public MijiaShortcutExecutionResult(bool succeeded, bool busy, string message)
        {
            Succeeded = succeeded;
            Busy = busy;
            Message = message ?? "";
        }

        public bool Succeeded { get; private set; }
        public bool Busy { get; private set; }
        public string Message { get; private set; }
    }

    public static class MijiaCliParser
    {
        private static readonly Regex SceneName = new Regex(@"^\s*-\s*(.+?)\s*$",
            RegexOptions.Compiled);
        private static readonly Regex SceneId = new Regex(@"^\s*ID:\s*(\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static MijiaScene[] ParseScenes(string output)
        {
            var scenes = new List<MijiaScene>();
            string name = null;
            string[] lines = (output ?? "").Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                Match nameMatch = SceneName.Match(line);
                if (nameMatch.Success)
                {
                    name = nameMatch.Groups[1].Value.Trim();
                    continue;
                }
                Match idMatch = SceneId.Match(line);
                if (name != null && idMatch.Success)
                {
                    scenes.Add(new MijiaScene(idMatch.Groups[1].Value, name));
                    name = null;
                }
            }
            return scenes.ToArray();
        }

        public static bool IsSceneId(string value)
        {
            return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"^\d+$");
        }
    }
}
