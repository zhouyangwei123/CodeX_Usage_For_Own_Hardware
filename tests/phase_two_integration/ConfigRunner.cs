using System;
using System.IO;
using System.Reflection;
using CodexToolsHost.Model;
internal static class ConfigRunner
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
    static int Main(string[] args)
    {
        try
        {
            PropertyInfo style = typeof(AppConfig).GetProperty("QuotaHudStyle");
            PropertyInfo updates = typeof(AppConfig).GetProperty("UpdateChecksEnabled");
            Check(style != null && updates != null, "HUD style and update preferences are missing");
            string root = args[0]; Directory.CreateDirectory(root);
            var config = AppConfig.Load(root, root);
            Check((string)style.GetValue(config, null) == "glass", "default style should be glass");
            Check((bool)updates.GetValue(config, null), "update checks should default enabled");
            config.DeepSeekApiKey = "synthetic-config-preservation-fixture";
            style.SetValue(config, "minimal", null); updates.SetValue(config, false, null);
            config.Save();
            var loaded = AppConfig.Load(root, root);
            Check((string)style.GetValue(loaded, null) == "minimal", "style did not round trip");
            Check(!(bool)updates.GetValue(loaded, null), "opt-out did not round trip");
            Check(loaded.DeepSeekApiKey == "synthetic-config-preservation-fixture", "unrelated integration setting was lost");
            style.SetValue(loaded, "invalid-style", null); loaded.Save();
            Check((string)style.GetValue(AppConfig.Load(root, root), null) == "glass", "unknown style did not normalize");
            File.WriteAllText(config.ConfigPath, "{\"configVersion\":9,\"quotaHudScalePercent\":90,\"quotaHudOpacity\":0.7,\"deepSeekApiKey\":\"synthetic-old-key\"}");
            var old = AppConfig.Load(root, root);
            Check(old.NeedsSave && old.ConfigVersion == 10, "old config did not request migration");
            Check(old.QuotaHudScalePercent == 90 && Math.Abs(old.QuotaHudOpacity - 0.7) < 0.001 && old.DeepSeekApiKey == "synthetic-old-key", "migration changed existing preferences");
            Check((string)style.GetValue(old, null) == "glass" && (bool)updates.GetValue(old, null), "migration defaults invalid");
            Console.WriteLine("PASS preferences default, round trip, opt-out, fallback and migration preserve existing settings"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error.Message); return 1; }
    }
}
