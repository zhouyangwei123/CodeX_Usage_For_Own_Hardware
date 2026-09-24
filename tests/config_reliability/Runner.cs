using System;
using System.IO;
using CodexToolsHost.Model;
class ConfigTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    static int Run(string[] args)
    {
        string root = args[0]; Directory.CreateDirectory(root);
        var config = AppConfig.Load(root, root);
        config.SerialPort = "COM31"; config.Save();
        byte[] first = File.ReadAllBytes(config.ConfigPath);
        config.SerialPort = "COM32"; config.Save();
        string backup = config.ConfigPath + ".bak";
        Check(File.Exists(backup), "previous valid config backup missing");
        Check(Convert.ToBase64String(File.ReadAllBytes(backup)) == Convert.ToBase64String(first), "backup did not preserve previous bytes");
        var reloaded = AppConfig.Load(root, root); Check(reloaded.SerialPort == "COM32", "saved configuration did not round trip");
        byte[] second = File.ReadAllBytes(config.ConfigPath);
        using (var locked = new FileStream(config.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            config.SerialPort = "COM33"; bool failed = false;
            try { config.Save(); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
            Check(failed, "locked destination save unexpectedly succeeded");
        }
        Check(Convert.ToBase64String(File.ReadAllBytes(config.ConfigPath)) == Convert.ToBase64String(second), "failed save altered original");
        Check(Directory.GetFiles(Path.GetDirectoryName(config.ConfigPath), "*.tmp").Length == 0, "failed save left temporary secrets");
        Console.WriteLine("PASS backup bytes, round trip, failed save preserves original, temp cleanup"); return 0;
    }
}
