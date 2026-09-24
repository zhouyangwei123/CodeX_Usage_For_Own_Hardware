using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using CodexToolsHost.Protocol;
using CodexToolsHost.Mijia;

namespace CodexToolsHost.Model
{
    public sealed class ActionSpec
    {
        public ActionSpec() { Action = "none"; Param = ""; }
        public ActionSpec(string action, string param) { Action = action; Param = param ?? ""; }
        public string Action { get; set; }
        public string Param { get; set; }
    }

    public sealed class RgbSettings
    {
        public RgbSettings()
        {
            Mode = "status";
            Count = 3;
            Brightness = 60;
            PeriodMs = 1500;
            Color1 = "#FF8000";
            Color2 = "#0000FF";
            StatusIdle = "#FF80C0";
            StatusRunning = "#0080FF";
            StatusWaiting = "#FF0000";
            StatusError = "#FF2020";
            StatusComplete = "#0080FF";
            StatusOffline = "#000050";
        }
        public string Mode { get; set; }
        public int Count { get; set; }
        public int Brightness { get; set; }
        public int PeriodMs { get; set; }
        public string Color1 { get; set; }
        public string Color2 { get; set; }
        public string StatusIdle { get; set; }
        public string StatusRunning { get; set; }
        public string StatusWaiting { get; set; }
        public string StatusError { get; set; }
        public string StatusComplete { get; set; }
        public string StatusOffline { get; set; }
    }

    public sealed class AppConfig
    {
        public const string DefaultFileName = "CodexToolsHost.json";
        public const string AutoPort = "auto";
        private static readonly int[] AllowedQuotaHudScalePercents = { 60, 75, 90, 100 };
        private readonly object _saveSync = new object();

        private AppConfig(string configPath, bool usesFallback)
        {
            ConfigPath = configPath;
            UsesFallbackPath = usesFallback;
            ConfigVersion = 9;
            SerialPort = "COM7";
            CodexRefreshSeconds = 30;
            DeepSeekApiKey = "";
            DeepSeekBaseUrl = "https://api.deepseek.com";
            DeepSeekRefreshSeconds = 300;
            ApiBalanceProvider = "deepseek";
            OpenCodeGoApiKey = "";
            OpenCodeGoRefreshSeconds = 300;
            QuotaDisplaySource = "deepseek";
            DefaultOledPage = 2;
            OledAddress = 0;      /* 0=自动, 0x3C, 0x3D */
            OledDriver = 0;       /* 0=自动, 1=SSD1306, 2=SH1106 */
            Rgb = new RgbSettings();
            Bindings = CreateDefaultBindings();
            /* 与当前联调配置同步；串口和密钥保留为可移植/非敏感默认值。 */
            EncoderRotate = new ActionSpec("cycleOled", "1");
            QuotaHudVisible = true;
            QuotaHudScalePercent = 75;
            QuotaHudOpacity = 0.90d;
            QuotaHudTopMost = true;
            QuotaHudX = null;
            QuotaHudY = null;
            Mijia = new MijiaSettings();
        }

        public static AppConfig CreateDefault()
        {
            return new AppConfig("", true);
        }

        public static string NormalizeQuotaDisplaySource(string value)
        {
            return string.Equals(value, "opencodego", StringComparison.OrdinalIgnoreCase)
                ? "opencodego" : "deepseek";
        }

        public string ConfigPath { get; private set; }
        public bool UsesFallbackPath { get; private set; }
        public bool NeedsSave { get; private set; }
        public bool RemovedSwitchBindingMigrated { get; private set; }
        public int ConfigVersion { get; set; }
        public string SerialPort { get; set; }
        public int CodexRefreshSeconds { get; set; }
        public string DeepSeekApiKey { get; set; }
        public string DeepSeekBaseUrl { get; set; }
        public int DeepSeekRefreshSeconds { get; set; }
        public string ApiBalanceProvider { get; set; }
        public string OpenCodeGoApiKey { get; set; }
        public int OpenCodeGoRefreshSeconds { get; set; }
        public string QuotaDisplaySource { get; set; }
        public int DefaultOledPage { get; set; }
        public int OledAddress { get; set; }
        public int OledDriver { get; set; }
        public RgbSettings Rgb { get; set; }
        public Dictionary<string, ActionSpec> Bindings { get; set; }
        public ActionSpec EncoderRotate { get; set; }
        public bool QuotaHudVisible { get; set; }
        public int QuotaHudScalePercent { get; set; }
        public double QuotaHudOpacity { get; set; }
        public bool QuotaHudTopMost { get; set; }
        public int? QuotaHudX { get; set; }
        public int? QuotaHudY { get; set; }
        public MijiaSettings Mijia { get; set; }

        public static string[] BindingKeys = new string[]
        {
            "btn0_click","btn1_click","btn2_click","btn3_click",
            "btn4_click","btn5_click","btn6_click","btn7_click",
            "enc_click"
        };

        private static Dictionary<string, ActionSpec> CreateDefaultBindings()
        {
            var d = new Dictionary<string, ActionSpec>();
            foreach (string key in BindingKeys) d[key] = new ActionSpec("none", "");
            d["btn0_click"] = new ActionSpec("refreshQuota", "");
            d["btn1_click"] = new ActionSpec("showSettings", "");
            d["btn2_click"] = new ActionSpec("cycleOled", "");
            d["btn3_click"] = new ActionSpec("launchCodex", "");
            d["btn4_click"] = new ActionSpec("hotkey", "Alt+D");
            d["btn5_click"] = new ActionSpec("rgbWave", "");
            d["btn6_click"] = new ActionSpec("rgbStatus", "");
            d["btn7_click"] = new ActionSpec("rgbOff", "");
            d["enc_click"] = new ActionSpec("cycleOled", "");
            return d;
        }

        public static AppConfig Load(string executableDirectory, string localAppDataDirectory)
        {
            string executableConfigPath = Path.Combine(executableDirectory, DefaultFileName);
            /*
             * A clean release directory contains only the EXE.  In that case
             * keep user settings under LocalAppData instead of recreating a
             * sidecar file beside the published binary.  Existing sidecar
             * configurations remain supported for backward compatibility.
             */
            bool fallback = !CanWriteDirectory(executableDirectory)
                || !File.Exists(executableConfigPath);
            string directory = fallback
                ? Path.Combine(localAppDataDirectory, "CodexToolsHost")
                : executableDirectory;
            Directory.CreateDirectory(directory);
            AppConfig config = new AppConfig(Path.Combine(directory, DefaultFileName), fallback);
            config.ReadExistingFile();
            return config;
        }

        public void Save()
        {
            lock (_saveSync) SaveCore();
        }

        private void SaveCore()
        {
            ConfigVersion = 9;
            SerialPort = SerialPortSelection.Normalize(SerialPort);
            if (CodexRefreshSeconds < 10) CodexRefreshSeconds = 30;
            if (DeepSeekRefreshSeconds < 30) DeepSeekRefreshSeconds = 300;
            if (OpenCodeGoRefreshSeconds < 30 || OpenCodeGoRefreshSeconds > 86400)
                OpenCodeGoRefreshSeconds = 300;
            QuotaDisplaySource = NormalizeQuotaDisplaySource(QuotaDisplaySource);
            if (Array.IndexOf(AllowedQuotaHudScalePercents, QuotaHudScalePercent) < 0)
                QuotaHudScalePercent = 75;
            if (double.IsNaN(QuotaHudOpacity) || double.IsInfinity(QuotaHudOpacity)
                || QuotaHudOpacity < 0.35d || QuotaHudOpacity > 1.0d)
                QuotaHudOpacity = 0.90d;
            if (Mijia == null) Mijia = new MijiaSettings();
            Mijia.RefreshMinutes = MijiaSettings.NormalizeRefreshMinutes(Mijia.RefreshMinutes);
            Mijia.Shortcuts = MijiaSettings.NormalizeShortcuts(Mijia.Shortcuts);
            string directory = Path.GetDirectoryName(ConfigPath);
            Directory.CreateDirectory(directory);

            var values = new Dictionary<string, object>();
            values["configVersion"] = ConfigVersion;
            values["serialPort"] = SerialPort;
            values["codexRefreshSeconds"] = CodexRefreshSeconds;
            values["deepSeekApiKey"] = DeepSeekApiKey;
            values["deepSeekBaseUrl"] = DeepSeekBaseUrl;
            values["deepSeekRefreshSeconds"] = DeepSeekRefreshSeconds;
            values["apiBalanceProvider"] = ApiBalanceProvider;
            values["openCodeGoApiKey"] = OpenCodeGoApiKey ?? "";
            values["openCodeGoRefreshSeconds"] = OpenCodeGoRefreshSeconds;
            values["quotaDisplaySource"] = QuotaDisplaySource;
            values["defaultOledPage"] = DefaultOledPage;
            values["oledAddress"] = OledAddress;
            values["oledDriver"] = OledDriver;
            values["quotaHudVisible"] = QuotaHudVisible;
            values["quotaHudScalePercent"] = QuotaHudScalePercent;
            values["quotaHudOpacity"] = QuotaHudOpacity;
            values["quotaHudTopMost"] = QuotaHudTopMost;
            values["quotaHudX"] = QuotaHudX;
            values["quotaHudY"] = QuotaHudY;

            var mijia = new Dictionary<string, object>();
            mijia["executablePath"] = Mijia.ExecutablePath ?? "";
            mijia["authPath"] = Mijia.AuthPath ?? "";
            mijia["autoRefresh"] = Mijia.AutoRefresh;
            mijia["refreshMinutes"] = Mijia.RefreshMinutes;
            var shortcuts = new List<Dictionary<string, object>>();
            foreach (MijiaShortcutConfig shortcut in Mijia.Shortcuts)
            {
                if (shortcut == null) continue;
                var item = new Dictionary<string, object>();
                item["name"] = shortcut.Name ?? "";
                item["sceneId"] = shortcut.SceneId ?? "";
                item["sceneName"] = shortcut.SceneName ?? "";
                shortcuts.Add(item);
            }
            mijia["shortcuts"] = shortcuts;
            values["mijia"] = mijia;

            var rgb = new Dictionary<string, object>();
            rgb["mode"] = Rgb.Mode;
            rgb["count"] = Rgb.Count;
            rgb["brightness"] = Rgb.Brightness;
            rgb["periodMs"] = Rgb.PeriodMs;
            rgb["color1"] = Rgb.Color1;
            rgb["color2"] = Rgb.Color2;
            rgb["statusIdle"] = Rgb.StatusIdle;
            rgb["statusRunning"] = Rgb.StatusRunning;
            rgb["statusWaiting"] = Rgb.StatusWaiting;
            rgb["statusError"] = Rgb.StatusError;
            rgb["statusComplete"] = Rgb.StatusComplete;
            rgb["statusOffline"] = Rgb.StatusOffline;
            values["rgb"] = rgb;

            var bindings = new Dictionary<string, object>();
            foreach (var kv in Bindings)
            {
                var spec = new Dictionary<string, object>();
                spec["action"] = kv.Value.Action;
                spec["param"] = kv.Value.Param;
                bindings[kv.Key] = spec;
            }
            values["bindings"] = bindings;

            var encRot = new Dictionary<string, object>();
            encRot["action"] = EncoderRotate.Action;
            encRot["param"] = EncoderRotate.Param;
            values["encoderRotate"] = encRot;
            string json = new JavaScriptSerializer().Serialize(values);
            if (!(new JavaScriptSerializer().DeserializeObject(json) is IDictionary<string, object>))
                throw new InvalidDataException("配置序列化结果无效，原文件未修改。");
            string temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, ConfigPath + ".bak");
                else File.Move(temp, ConfigPath);
            }
            finally
            {
                // Never delete the live configuration as a fallback for failed replacement.
                if (File.Exists(temp)) File.Delete(temp);
            }
            NeedsSave = false;
        }

        private void ReadExistingFile()
        {
            if (!File.Exists(ConfigPath)) return;
            IDictionary<string, object> values;
            try
            {
                values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath, Encoding.UTF8))
                    as IDictionary<string, object>;
                if (values == null) return;
            }
            catch (Exception) { return; }

            SerialPort = SerialPortSelection.Normalize(ReadString(values, "serialPort") ?? "COM7");
            int? crs = ReadInt(values, "codexRefreshSeconds");
            if (crs.HasValue && crs.Value >= 10) CodexRefreshSeconds = crs.Value;
            DeepSeekApiKey = ReadString(values, "deepSeekApiKey") ?? "";
            DeepSeekBaseUrl = ReadString(values, "deepSeekBaseUrl") ?? "https://api.deepseek.com";
            int? drs = ReadInt(values, "deepSeekRefreshSeconds");
            if (drs.HasValue && drs.Value >= 30) DeepSeekRefreshSeconds = drs.Value;
            string apiProvider = ReadString(values, "apiBalanceProvider");
            ApiBalanceProvider = CodexToolsHost.Quota.ApiBalanceProvider.NormalizeProvider(apiProvider);
            OpenCodeGoApiKey = ReadString(values, "openCodeGoApiKey") ?? "";
            int? ogrs = ReadInt(values, "openCodeGoRefreshSeconds");
            if (ogrs.HasValue && ogrs.Value >= 30 && ogrs.Value <= 86400)
                OpenCodeGoRefreshSeconds = ogrs.Value;
            QuotaDisplaySource = NormalizeQuotaDisplaySource(ReadString(values, "quotaDisplaySource"));
            int? page = ReadInt(values, "defaultOledPage");
            int? ver = ReadInt(values, "configVersion");
            int loadedVersion = ver.HasValue ? ver.Value : 0;
            if (page.HasValue && page.Value >= 0 && page.Value <= 3)
            {
                /* v1: 0=状态 1=额度 2=动画 3=关于；v2/v3: 0=状态 1=动画 2=PC 3=自动。 */
                if (loadedVersion < 2)
                {
                    if (page.Value <= 1) DefaultOledPage = 0;
                    else if (page.Value == 2) DefaultOledPage = 1;
                    else DefaultOledPage = 1;
                }
                else if (loadedVersion < 4)
                {
                    if (page.Value == 0) DefaultOledPage = 0;
                    else if (page.Value <= 2) DefaultOledPage = 1;
                    else DefaultOledPage = 2;
                }
                else DefaultOledPage = Math.Min(2, page.Value);
            }
            NeedsSave = loadedVersion != 9;
            ConfigVersion = 9;
            int? oa = ReadInt(values, "oledAddress");
            if (oa.HasValue && (oa.Value == 0 || oa.Value == 0x3C || oa.Value == 0x3D)) OledAddress = oa.Value;
            int? od = ReadInt(values, "oledDriver");
            if (od.HasValue && od.Value >= 0 && od.Value <= 2) OledDriver = od.Value;

            bool? hudVisible = ReadBool(values, "quotaHudVisible");
            bool? legacyApiVisible = ReadBool(values, "apiBalanceVisible");
            if (hudVisible.HasValue) QuotaHudVisible = hudVisible.Value;
            else if (legacyApiVisible.HasValue) QuotaHudVisible = legacyApiVisible.Value;
            int? hudScale = ReadInt(values, "quotaHudScalePercent");
            if (!hudScale.HasValue) hudScale = ReadInt(values, "scalePercent");
            if (hudScale.HasValue
                && Array.IndexOf(AllowedQuotaHudScalePercents, hudScale.Value) >= 0)
                QuotaHudScalePercent = hudScale.Value;
            double? hudOpacity = ReadDouble(values, "quotaHudOpacity");
            if (!hudOpacity.HasValue) hudOpacity = ReadDouble(values, "opacity");
            if (hudOpacity.HasValue && hudOpacity.Value >= 0.35d
                && hudOpacity.Value <= 1.0d)
                QuotaHudOpacity = hudOpacity.Value;
            bool? hudTopMost = ReadBool(values, "quotaHudTopMost");
            if (!hudTopMost.HasValue) hudTopMost = ReadBool(values, "topMost");
            if (hudTopMost.HasValue) QuotaHudTopMost = hudTopMost.Value;
            QuotaHudX = ReadInt(values, "quotaHudX");
            QuotaHudY = ReadInt(values, "quotaHudY");
            if (!QuotaHudX.HasValue || !QuotaHudY.HasValue)
            {
                /* 旧版 API 小窗位置仅作为一次性迁移候选，不再写回独立字段。 */
                int? legacyApiX = ReadInt(values, "apiBalanceX");
                int? legacyApiY = ReadInt(values, "apiBalanceY");
                if (legacyApiX.HasValue && legacyApiY.HasValue)
                {
                    QuotaHudX = legacyApiX;
                    QuotaHudY = legacyApiY;
                }
            }

            var rgb = values.ContainsKey("rgb") ? values["rgb"] as IDictionary<string, object> : null;
            if (rgb != null)
            {
                Rgb.Mode = ReadString(rgb, "mode") ?? "status";
                int? c = ReadInt(rgb, "count");
                if (c.HasValue && c.Value >= 1 && c.Value <= 16) Rgb.Count = c.Value;
                int? br = ReadInt(rgb, "brightness");
                if (br.HasValue && br.Value >= 0 && br.Value <= 255) Rgb.Brightness = br.Value;
                int? pm = ReadInt(rgb, "periodMs");
                if (pm.HasValue && pm.Value >= 100) Rgb.PeriodMs = pm.Value;
                Rgb.Color1 = ReadString(rgb, "color1") ?? "#FF8000";
                Rgb.Color2 = ReadString(rgb, "color2") ?? "#0000FF";
                Rgb.StatusIdle = ReadString(rgb, "statusIdle") ?? "#FF80C0";
                Rgb.StatusRunning = ReadString(rgb, "statusRunning") ?? "#0080FF";
                Rgb.StatusWaiting = ReadString(rgb, "statusWaiting") ?? "#FF0000";
                Rgb.StatusError = ReadString(rgb, "statusError") ?? "#FF2020";
                Rgb.StatusComplete = ReadString(rgb, "statusComplete") ?? "#0080FF";
                Rgb.StatusOffline = ReadString(rgb, "statusOffline") ?? "#000050";
            }

            var bindings = values.ContainsKey("bindings") ? values["bindings"] as IDictionary<string, object> : null;
            if (bindings != null)
            {
                foreach (string key in BindingKeys)
                {
                    if (!bindings.ContainsKey(key)) continue;
                    var spec = bindings[key] as IDictionary<string, object>;
                    if (spec == null) continue;
                    string action = ReadString(spec, "action") ?? "none";
                    /* 旧“聚焦 Codex”动作已并入“启动 CodeX” */
                    if (string.Equals(action, "codexFocus", StringComparison.OrdinalIgnoreCase))
                        action = "launchCodex";
                    string param = ReadString(spec, "param") ?? "";
                    if (string.Equals(action, "toggleDeepSeek", StringComparison.OrdinalIgnoreCase))
                    {
                        action = "none";
                        param = "";
                        NeedsSave = true;
                        RemovedSwitchBindingMigrated = true;
                    }
                    Bindings[key] = new ActionSpec(action, param);
                }
            }

            var er = values.ContainsKey("encoderRotate") ? values["encoderRotate"] as IDictionary<string, object> : null;
            if (er != null)
            {
                string action = ReadString(er, "action") ?? "cycleOled";
                string param = ReadString(er, "param") ?? "1";
                if (string.Equals(action, "toggleDeepSeek", StringComparison.OrdinalIgnoreCase))
                {
                    action = "none";
                    param = "";
                    NeedsSave = true;
                    RemovedSwitchBindingMigrated = true;
                }
                EncoderRotate = new ActionSpec(action, param);
            }

            var mijia = values.ContainsKey("mijia")
                ? values["mijia"] as IDictionary<string, object> : null;
            if (mijia != null)
            {
                Mijia.ExecutablePath = ReadString(mijia, "executablePath") ?? "";
                Mijia.AuthPath = ReadString(mijia, "authPath") ?? "";
                bool? autoRefresh = ReadBool(mijia, "autoRefresh");
                if (autoRefresh.HasValue) Mijia.AutoRefresh = autoRefresh.Value;
                int? refreshMinutes = ReadInt(mijia, "refreshMinutes");
                Mijia.RefreshMinutes = MijiaSettings.NormalizeRefreshMinutes(
                    refreshMinutes.HasValue ? refreshMinutes.Value : Mijia.RefreshMinutes);
                var shortcutValues = mijia.ContainsKey("shortcuts")
                    ? mijia["shortcuts"] as IEnumerable : null;
                if (shortcutValues != null)
                {
                    var shortcuts = new List<MijiaShortcutConfig>();
                    foreach (object shortcutValue in shortcutValues)
                    {
                        var shortcut = shortcutValue as IDictionary<string, object>;
                        if (shortcut == null) continue;
                        shortcuts.Add(new MijiaShortcutConfig(
                            ReadString(shortcut, "name") ?? "",
                            ReadString(shortcut, "sceneId") ?? "",
                            ReadString(shortcut, "sceneName") ?? ""));
                    }
                    Mijia.Shortcuts = shortcuts.ToArray();
                }
            }
            Mijia.Shortcuts = MijiaSettings.NormalizeShortcuts(Mijia.Shortcuts);
        }

        private static bool CanWriteDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { }
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static string ReadString(IDictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        private static int? ReadInt(IDictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null) return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        private static double? ReadDouble(IDictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        private static bool? ReadBool(IDictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value) || value == null) return null;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }
    }
}
