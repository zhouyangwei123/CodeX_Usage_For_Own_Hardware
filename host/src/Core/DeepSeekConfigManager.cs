using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexToolsHost.Core
{
    public enum DeepSeekConfigState
    {
        Missing,
        Incomplete,
        DeepSeekActive,
        OfficialActive
    }

    /// <summary>不包含密钥的 DeepSeek/Codex 配置状态。</summary>
    public sealed class DeepSeekConfigStatus
    {
        internal DeepSeekConfigStatus(DeepSeekConfigState state, string model,
                                      string codexHome, bool needsOfficialSetup)
        {
            State = state;
            Model = string.IsNullOrEmpty(model) ? "" : model;
            CodexHome = codexHome;
            NeedsOfficialSetup = needsOfficialSetup;
        }

        public DeepSeekConfigState State { get; private set; }
        public string Model { get; private set; }
        public string CodexHome { get; private set; }
        public bool NeedsOfficialSetup { get; private set; }
        public bool IsDeepSeekActive { get { return State == DeepSeekConfigState.DeepSeekActive; } }
    }

    /// <summary>配置切换结果；Message 永远不包含 API Key。</summary>
    public sealed class DeepSeekActionResult
    {
        internal DeepSeekActionResult(bool success, bool needsOfficialSetup,
                                      bool launchedOfficialSetup, string message,
                                      DeepSeekConfigStatus status)
        {
            Success = success;
            NeedsOfficialSetup = needsOfficialSetup;
            LaunchedOfficialSetup = launchedOfficialSetup;
            Message = message ?? "";
            Status = status;
        }

        public bool Success { get; private set; }
        public bool NeedsOfficialSetup { get; private set; }
        public bool LaunchedOfficialSetup { get; private set; }
        public string Message { get; private set; }
        public DeepSeekConfigStatus Status { get; private set; }
    }

    /// <summary>
    /// 管理 DeepSeek 官方 Codex 配置与原有官方配置之间的可移植切换。
    /// 只操作 CODEX_HOME 下的已知文件，不调用官方脚本的破坏性恢复菜单。
    /// </summary>
    public sealed class DeepSeekConfigManager
    {
        public const string FlashModel = "deepseek-v4-flash";
        public const string ProModel = "deepseek-v4-pro";
        public const string DeepSeekProvider = "deepseek";
        public const string OfficialSetupUrl =
            "https://cdn.deepseek.com/api-docs/codex-deepseek-setup-en.ps1";

        private readonly string _codexHome;
        private readonly string _configPath;
        private readonly string _modelsPath;
        private readonly string _managedDirectory;
        private readonly string _deepSeekConfigPath;
        private readonly string _deepSeekModelsPath;
        private readonly string _officialConfigPath;
        private readonly string _officialModelsPath;
        private readonly object _sync = new object();

        public DeepSeekConfigManager()
            : this(ResolveCodexHome())
        {
        }

        public DeepSeekConfigManager(string codexHome)
        {
            if (string.IsNullOrWhiteSpace(codexHome))
                throw new ArgumentException("Codex 配置目录不能为空。", "codexHome");

            _codexHome = Path.GetFullPath(codexHome);
            _configPath = Path.Combine(_codexHome, "config.toml");
            _modelsPath = Path.Combine(_codexHome, "models.json");
            _managedDirectory = Path.Combine(_codexHome, "codex-tools-deepseek");
            _deepSeekConfigPath = Path.Combine(_managedDirectory, "config.deepseek.toml");
            _deepSeekModelsPath = Path.Combine(_managedDirectory, "models.deepseek.json");
            _officialConfigPath = Path.Combine(_managedDirectory, "config.official.toml");
            _officialModelsPath = Path.Combine(_managedDirectory, "models.official.json");
        }

        public string CodexHome { get { return _codexHome; } }

        public static string ResolveCodexHome()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
                throw new InvalidOperationException("无法解析当前 Windows 用户目录。");
            return Path.Combine(profile, ".codex");
        }

        public DeepSeekConfigStatus ReadStatus()
        {
            lock (_sync)
            {
                try
                {
                    string activeConfig = ReadIfExists(_configPath);
                    string activeModels = ReadIfExists(_modelsPath);
                    if (IsDeepSeekConfig(activeConfig, activeModels))
                    {
                        string activeModel = ReadTopLevelString(activeConfig, "model");
                        return MakeStatus(DeepSeekConfigState.DeepSeekActive, activeModel, false);
                    }

                    if (File.Exists(_configPath) && File.Exists(_deepSeekConfigPath)
                        && File.Exists(_deepSeekModelsPath)
                        && IsRecognizableOfficialConfig(activeConfig))
                    {
                        string snapshot = ReadIfExists(_deepSeekConfigPath);
                        return MakeStatus(DeepSeekConfigState.OfficialActive,
                            ReadTopLevelString(snapshot, "model"), false);
                    }

                    if (File.Exists(_configPath))
                        return MakeStatus(DeepSeekConfigState.Incomplete, "", true);
                    return MakeStatus(DeepSeekConfigState.Missing, "", true);
                }
                catch (Exception)
                {
                    return MakeStatus(DeepSeekConfigState.Incomplete, "", true);
                }
            }
        }

        public DeepSeekActionResult ActivateDeepSeek(string model)
        {
            lock (_sync)
            {
                if (!IsSupportedModel(model))
                    return Result(false, false, false, "不支持的 DeepSeek 模型。", ReadStatus());

                try
                {
                    EnsureSnapshotsFromExistingArtifacts();
                    if (!File.Exists(_deepSeekConfigPath) || !File.Exists(_deepSeekModelsPath))
                        return Result(false, true, false,
                            "尚未发现完整的 DeepSeek 配置，请先启动官方配置流程。", ReadStatus());

                    string deepSeekConfig = File.ReadAllText(_deepSeekConfigPath, Encoding.UTF8);
                    string deepSeekModels = File.ReadAllText(_deepSeekModelsPath, Encoding.UTF8);
                    if (!IsDeepSeekConfig(deepSeekConfig, deepSeekModels))
                        return Result(false, true, false,
                            "DeepSeek 配置不完整，请先启动官方配置流程。", ReadStatus());

                    bool hadConfig = File.Exists(_configPath);
                    bool hadModels = File.Exists(_modelsPath);
                    string previousConfig = ReadIfExists(_configPath);
                    string previousModels = ReadIfExists(_modelsPath);
                    string updatedConfig = ReplaceTopLevelString(deepSeekConfig, "model", model);
                    updatedConfig = ReplaceTopLevelString(updatedConfig,
                        "model_reasoning_effort", "high");
                    try
                    {
                        WriteTextAtomic(_configPath, updatedConfig);
                        CopyFileAtomic(_deepSeekModelsPath, _modelsPath);
                    }
                    catch (Exception)
                    {
                        bool rollbackConfigOk = RestoreActiveFile(_configPath, previousConfig, hadConfig);
                        bool rollbackModelsOk = RestoreActiveFile(_modelsPath, previousModels, hadModels);
                        if (!rollbackConfigOk || !rollbackModelsOk)
                            return Result(false, false, false,
                                "切换失败，且原配置回滚不完整，请检查 .codex/config.toml 与 models.json。",
                                ReadStatus());
                        throw;
                    }
                    return Result(true, false, false,
                        "已切换到 " + ModelDisplayName(model) + "，请重启 Codex 生效。", ReadStatus());
                }
                catch (Exception)
                {
                    return Result(false, false, false,
                        "切换 DeepSeek 失败，请检查 .codex 权限或文件占用。", ReadStatus());
                }
            }
        }

        public DeepSeekActionResult RestoreOfficial()
        {
            lock (_sync)
            {
                try
                {
                    EnsureSnapshotsFromExistingArtifacts();
                    string activeConfig = ReadIfExists(_configPath);
                    string activeModels = ReadIfExists(_modelsPath);
                    if (IsDeepSeekConfig(activeConfig, activeModels))
                        SaveDeepSeekSnapshot(activeConfig, activeModels);

                    if (!File.Exists(_officialConfigPath))
                        return Result(false, true, false,
                            "尚未发现可恢复的官方账号配置，请先完成官方配置流程。", ReadStatus());

                    string officialConfig = File.ReadAllText(_officialConfigPath, Encoding.UTF8);
                    if (IsDeepSeekConfig(officialConfig,
                        File.Exists(_officialModelsPath)
                            ? File.ReadAllText(_officialModelsPath, Encoding.UTF8) : null))
                        return Result(false, true, false,
                            "官方账号快照无效，请先完成官方配置流程。", ReadStatus());

                    bool hadConfig = File.Exists(_configPath);
                    bool hadModels = File.Exists(_modelsPath);
                    string previousConfig = ReadIfExists(_configPath);
                    string previousModels = ReadIfExists(_modelsPath);
                    try
                    {
                        WriteTextAtomic(_configPath, officialConfig);
                        if (File.Exists(_officialModelsPath))
                            CopyFileAtomic(_officialModelsPath, _modelsPath);
                        else if (IsDeepSeekCatalog(activeModels))
                            DeleteIfExists(_modelsPath);
                    }
                    catch (Exception)
                    {
                        bool rollbackConfigOk = RestoreActiveFile(_configPath, previousConfig, hadConfig);
                        bool rollbackModelsOk = RestoreActiveFile(_modelsPath, previousModels, hadModels);
                        if (!rollbackConfigOk || !rollbackModelsOk)
                            return Result(false, false, false,
                                "恢复失败，且原配置回滚不完整，请检查 .codex/config.toml 与 models.json。",
                                ReadStatus());
                        throw;
                    }

                    return Result(true, false, false,
                        "已恢复 Codex 官方账号配置，请重启 Codex 生效。", ReadStatus());
                }
                catch (Exception)
                {
                    return Result(false, false, false,
                        "恢复官方账号失败，请检查 .codex 权限或文件占用。", ReadStatus());
                }
            }
        }

        public DeepSeekActionResult Toggle()
        {
            lock (_sync)
            {
                DeepSeekConfigStatus status = ReadStatus();
                if (status.State == DeepSeekConfigState.DeepSeekActive)
                    return RestoreOfficial();
                if (status.State == DeepSeekConfigState.OfficialActive)
                    return ActivateDeepSeek(
                        IsSupportedModel(status.Model) ? status.Model : FlashModel);

                return LaunchOfficialSetup();
            }
        }

        public DeepSeekActionResult LaunchOfficialSetup()
        {
            try
            {
                string command = "irm '" + OfficialSetupUrl + "' | iex";
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \""
                        + command + "\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                    CreateNoWindow = false
                };
                Process.Start(startInfo);
                return Result(true, true, true,
                    "已启动官方 DeepSeek 配置流程，完成后点击刷新状态。", ReadStatus());
            }
            catch (Exception)
            {
                return Result(false, true, false,
                    "启动官方配置流程失败，请检查 PowerShell 和网络连接。", ReadStatus());
            }
        }

        private DeepSeekActionResult Result(bool success, bool needsSetup,
                                             bool launched, string message,
                                             DeepSeekConfigStatus status)
        {
            return new DeepSeekActionResult(success, needsSetup, launched, message, status);
        }

        private DeepSeekConfigStatus MakeStatus(DeepSeekConfigState state,
                                                string model, bool needsSetup)
        {
            return new DeepSeekConfigStatus(state, model, _codexHome, needsSetup);
        }

        private void EnsureSnapshotsFromExistingArtifacts()
        {
            if (!Directory.Exists(_codexHome)) return;
            Directory.CreateDirectory(_managedDirectory);

            if (!File.Exists(_officialConfigPath))
            {
                string backup = Path.Combine(_codexHome, "backup-deepseek", "config.toml");
                string backupText = ReadIfExists(backup);
                if (!string.IsNullOrEmpty(backupText) && !IsDeepSeekConfig(backupText, null))
                    CopyFileAtomic(backup, _officialConfigPath);
                else
                {
                    string active = ReadIfExists(_configPath);
                    string models = ReadIfExists(_modelsPath);
                    if (!string.IsNullOrEmpty(active) && !IsDeepSeekConfig(active, models))
                        CopyFileAtomic(_configPath, _officialConfigPath);
                }
            }

            if (!File.Exists(_officialModelsPath) && File.Exists(_modelsPath))
            {
                string activeModels = ReadIfExists(_modelsPath);
                if (!IsDeepSeekCatalog(activeModels))
                    CopyFileAtomic(_modelsPath, _officialModelsPath);
            }

            string activeConfig = ReadIfExists(_configPath);
            string activeModelsForSnapshot = ReadIfExists(_modelsPath);
            if (IsDeepSeekConfig(activeConfig, activeModelsForSnapshot))
                SaveDeepSeekSnapshot(activeConfig, activeModelsForSnapshot);
        }

        private void SaveDeepSeekSnapshot(string configText, string modelsText)
        {
            if (!IsDeepSeekConfig(configText, modelsText)) return;
            Directory.CreateDirectory(_managedDirectory);
            WriteTextAtomic(_deepSeekConfigPath, configText);
            WriteTextAtomic(_deepSeekModelsPath, modelsText);
        }

        private static bool IsSupportedModel(string model)
        {
            return string.Equals(model, FlashModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model, ProModel, StringComparison.OrdinalIgnoreCase);
        }

        private static string ModelDisplayName(string model)
        {
            return string.Equals(model, ProModel, StringComparison.OrdinalIgnoreCase)
                ? "DeepSeek V4 Pro" : "DeepSeek V4 Flash";
        }

        private static bool IsDeepSeekConfig(string configText, string modelsText)
        {
            if (string.IsNullOrEmpty(configText) || string.IsNullOrEmpty(modelsText)) return false;
            string provider = ReadTopLevelString(configText, "model_provider");
            string model = ReadTopLevelString(configText, "model");
            string auth = ReadTopLevelString(configText, "preferred_auth_method");
            string forced = ReadTopLevelString(configText, "forced_login_method");
            string effort = ReadTopLevelString(configText, "model_reasoning_effort");
            string catalog = ReadTopLevelString(configText, "model_catalog_json");
            return string.Equals(provider, DeepSeekProvider, StringComparison.OrdinalIgnoreCase)
                && IsSupportedModel(model)
                && string.Equals(auth, "apikey", StringComparison.OrdinalIgnoreCase)
                && string.Equals(forced, "api", StringComparison.OrdinalIgnoreCase)
                && string.Equals(effort, "high", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(catalog)
                && catalog.EndsWith("models.json", StringComparison.OrdinalIgnoreCase)
                && HasPattern(configText, "(?m)^[ \\t]*\\[model_providers\\.deepseek\\][ \\t]*\\r?$")
                && HasTopLevelValue(configText, "name", "deepseek")
                && HasTopLevelValue(configText, "base_url", "https://api.deepseek.com/")
                && HasTopLevelValue(configText, "wire_api", "responses")
                && HasKeyLine(configText, "experimental_bearer_token")
                && IsDeepSeekCatalog(modelsText);
        }

        private static bool IsRecognizableOfficialConfig(string configText)
        {
            if (string.IsNullOrEmpty(configText)) return false;
            return !string.IsNullOrEmpty(ReadTopLevelString(configText, "model"))
                && !string.IsNullOrEmpty(ReadTopLevelString(configText, "model_reasoning_effort"));
        }

        private static bool IsDeepSeekCatalog(string modelsText)
        {
            if (string.IsNullOrEmpty(modelsText)) return false;
            return modelsText.IndexOf("\"" + FlashModel + "\"",
                       StringComparison.OrdinalIgnoreCase) >= 0
                && modelsText.IndexOf("\"" + ProModel + "\"",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasTopLevelValue(string text, string key, string expected)
        {
            return string.Equals(ReadTopLevelString(text, key), expected,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasKeyLine(string text, string key)
        {
            return HasPattern(text, "(?m)^[ \\t]*" + Regex.Escape(key)
                + "[ \\t]*=");
        }

        private static bool HasPattern(string text, string pattern)
        {
            return !string.IsNullOrEmpty(text)
                && Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
        }

        private static string ReadTopLevelString(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string pattern = "(?m)^[ \\t]*" + Regex.Escape(key)
                + "[ \\t]*=[ \\t]*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)')";
            Match match = Regex.Match(text, pattern);
            if (!match.Success) return null;
            return match.Groups["double"].Success
                ? match.Groups["double"].Value : match.Groups["single"].Value;
        }

        private static string ReplaceTopLevelString(string text, string key, string value)
        {
            if (text == null) text = "";
            string pattern = "(?m)^([ \\t]*" + Regex.Escape(key)
                + "[ \\t]*=[ \\t]*)(?:\"[^\"]*\"|'[^']*')([ \\t]*(?:#.*)?)$";
            Regex regex = new Regex(pattern);
            if (regex.IsMatch(text))
            {
                return regex.Replace(text, delegate(Match match)
                {
                    return match.Groups[1].Value + "\"" + value + "\""
                        + match.Groups[2].Value;
                }, 1);
            }
            return key + " = \"" + value + "\"\r\n" + text;
        }

        private static string ReadIfExists(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        private static void CopyFileAtomic(string source, string destination)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("配置文件不存在。", source);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".new";
            try
            {
                File.Copy(source, temporary, true);
                ReplaceTemporaryFile(temporary, destination);
            }
            finally
            {
                DeleteIfExists(temporary);
            }
        }

        private static void WriteTextAtomic(string destination, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + ".new";
            try
            {
                File.WriteAllText(temporary, content ?? "", new UTF8Encoding(false));
                ReplaceTemporaryFile(temporary, destination);
            }
            finally
            {
                DeleteIfExists(temporary);
            }
        }

        private static bool RestoreActiveFile(string path, string content, bool existed)
        {
            try
            {
                if (existed) WriteTextAtomic(path, content ?? "");
                else DeleteIfExists(path);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void ReplaceTemporaryFile(string temporary, string destination)
        {
            if (File.Exists(destination))
                File.Replace(temporary, destination, null);
            else
                File.Move(temporary, destination);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
