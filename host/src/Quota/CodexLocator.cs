using System;
using System.Collections.Generic;
using System.IO;

namespace CodexToolsHost.Quota
{
    public static class CodexLocator
    {
        public static string Find()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            var candidates = new List<FileInfo>();
            if (Directory.Exists(binRoot))
            {
                foreach (string directory in Directory.GetDirectories(binRoot))
                {
                    string path = Path.Combine(directory, "codex.exe");
                    if (File.Exists(path)) candidates.Add(new FileInfo(path));
                }
            }
            candidates.Sort(delegate(FileInfo left, FileInfo right)
            {
                return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            });
            if (candidates.Count > 0) return candidates[0].FullName;

            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    string candidate = Path.Combine(directory.Trim(), "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }
}
