using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace CodexToolsHost.Monitor
{
    internal sealed class EmbeddedDependencyPreparation
    {
        public string DirectoryPath { get; set; }
        public int ResourceCount { get; set; }
        public string[] FileNames { get; set; }
        public bool AllHashesValid { get; set; }
    }

    /// <summary>
    /// 将随主程序内嵌的硬件监控程序集释放到版本化用户缓存。
    /// 每次使用前校验 SHA-256，损坏或被替换的文件会自动恢复。
    /// </summary>
    internal static class EmbeddedDependencyStore
    {
        internal const string PackageVersion = "LibreHardwareMonitor-0.9.6";
        internal const string ResourcePrefix = "CodexToolsHost.Dependencies.";

        public static EmbeddedDependencyPreparation Prepare(string cacheRoot)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string[] resources = Array.FindAll(assembly.GetManifestResourceNames(),
                delegate(string name)
                {
                    return name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                });
            Array.Sort(resources, StringComparer.OrdinalIgnoreCase);
            if (resources.Length == 0)
                throw new InvalidOperationException("Embedded hardware monitor resources are missing.");

            string root = string.IsNullOrWhiteSpace(cacheRoot)
                ? GetDefaultCacheRoot() : Path.GetFullPath(cacheRoot);
            string packageDirectory = Path.Combine(root, PackageVersion);
            Directory.CreateDirectory(packageDirectory);

            var fileNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string resourceName in resources)
            {
                string fileName = resourceName.Substring(ResourcePrefix.Length);
                if (string.IsNullOrWhiteSpace(fileName)
                    || !string.Equals(fileName, Path.GetFileName(fileName),
                        StringComparison.Ordinal)
                    || !seen.Add(fileName))
                    throw new InvalidDataException("Invalid embedded dependency name: " + fileName);

                string targetPath = Path.Combine(packageDirectory, fileName);
                byte[] expectedHash = ComputeResourceHash(assembly, resourceName);
                if (!FileHashMatches(targetPath, expectedHash))
                    ExtractResourceAtomically(assembly, resourceName, targetPath, expectedHash);

                bool valid = FileHashMatches(targetPath, expectedHash);
                if (!valid)
                    throw new InvalidDataException(
                        "Cached dependency hash mismatch: " + fileName);
                fileNames.Add(fileName);
            }

            return new EmbeddedDependencyPreparation
            {
                DirectoryPath = packageDirectory,
                ResourceCount = resources.Length,
                FileNames = fileNames.ToArray(),
                AllHashesValid = true
            };
        }

        private static string GetDefaultCacheRoot()
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("LocalApplicationData is unavailable.");
            return Path.Combine(localAppData, "CodexToolsHost", "dependencies");
        }

        private static byte[] ComputeResourceHash(Assembly assembly, string resourceName)
        {
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidDataException("Embedded dependency stream is missing: " + resourceName);
                using (SHA256 hash = SHA256.Create()) return hash.ComputeHash(stream);
            }
        }

        private static bool FileHashMatches(string path, byte[] expectedHash)
        {
            if (!File.Exists(path)) return false;
            try
            {
                byte[] actual;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 hash = SHA256.Create())
                    actual = hash.ComputeHash(stream);
                return HashEquals(actual, expectedHash);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HashEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static void ExtractResourceAtomically(Assembly assembly, string resourceName,
            string targetPath, byte[] expectedHash)
        {
            string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (Stream source = assembly.GetManifestResourceStream(resourceName))
                {
                    if (source == null)
                        throw new InvalidDataException(
                            "Embedded dependency stream is missing: " + resourceName);
                    using (var destination = new FileStream(tempPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(destination);
                        destination.Flush(true);
                    }
                }

                if (!FileHashMatches(tempPath, expectedHash))
                    throw new InvalidDataException(
                        "Extracted dependency hash mismatch: " + Path.GetFileName(targetPath));

                if (File.Exists(targetPath))
                {
                    try
                    {
                        File.Replace(tempPath, targetPath, null);
                    }
                    catch (IOException)
                    {
                        if (!FileHashMatches(targetPath, expectedHash)) throw;
                    }
                }
                else
                {
                    try
                    {
                        File.Move(tempPath, targetPath);
                    }
                    catch (IOException)
                    {
                        if (!FileHashMatches(targetPath, expectedHash))
                            throw;
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch (Exception) { }
            }
        }
    }
}
