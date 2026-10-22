using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Protocol;
using CodexToolsHost.Updates;

// Test harness for the delivered production updater/installer. It adds explicit
// bench identity checks and an audit log; it does not change product code or
// replace the image validator, writer, backup, or readback implementation.
internal static class HardwareUpdateRunner
{
    const string Probe="53FF77064986484859341587", Usb="20553872524E";
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static string Hash(byte[] bytes){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","");}
    static int Main(string[] args)
    {
        string mode=args[0], programmerPath=args[1], package=args[2], output=Path.GetFullPath(args[3]);
        Directory.CreateDirectory(output);
        var record=new Dictionary<string,object>();
        record["mode"]=mode;record["startedUtc"]=DateTime.UtcNow.ToString("o");
        record["assembly"]=typeof(FirmwareImage).Assembly.Location;
        record["assemblySha256"]=Hash(File.ReadAllBytes(typeof(FirmwareImage).Assembly.Location));
        record["programmerSha256"]=Hash(File.ReadAllBytes(programmerPath));
        record["probeSerial"]=Probe;record["usbSerial"]=Usb;
        int step=0;var clock=Stopwatch.StartNew();int result=1;
        var programmer=new CubeProgrammer(programmerPath);
        Func<string[],int,string> run=delegate(string[] argv,int timeout)
        {
            if(argv.Contains("-c"))
            {
                string expected=argv.Contains("port=SWD")?"sn="+Probe:"sn="+Usb;
                if(!argv.Contains(expected))throw new IOException("Bench identity guard rejected selected device");
            }
            string stem=Path.Combine(output,(++step).ToString("D2"));
            File.WriteAllText(stem+"-argv.json",Json.Serialize(new{argv=argv,timeoutMs=timeout,utc=DateTime.UtcNow.ToString("o")}),new UTF8Encoding(false));
            Console.WriteLine("STEP "+step+" "+string.Join(" ",argv));
            try
            {
                string text=programmer.Run(argv,timeout);
                File.WriteAllText(stem+"-output.txt",text,new UTF8Encoding(false));
                if(argv.SequenceEqual(new[]{"-l","st-link-only"}))
                {
                    var found=Regex.Matches(text,@"ST-LINK SN\s*:\s*([A-Za-z0-9]+)",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Groups[1].Value).Distinct().ToArray();
                    if(found.Length!=1||found[0]!=Probe)throw new IOException("Bench probe identity changed");
                }
                if(argv.SequenceEqual(new[]{"-l","usb"}))
                {
                    var found=Regex.Matches(text,@"Serial number\s*:\s*([A-Za-z0-9]+)",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Groups[1].Value).Distinct().ToArray();
                    if(found.Length>0&&(found.Length!=1||found[0]!=Usb))throw new IOException("Bench DFU identity changed");
                }
                return text;
            }
            catch(Exception ex){File.WriteAllText(stem+"-error.txt",ex.ToString(),new UTF8Encoding(false));throw;}
        };
        try
        {
            byte[] app=File.ReadAllBytes(Path.Combine(package,"Application-0.9.bin"));
            record["appSha256"]=Hash(app);record["appBytes"]=app.Length;
            if(mode=="install")
            {
                byte[] boot=File.ReadAllBytes(Path.Combine(package,"Bootloader-1.0.bin"));
                record["bootSha256"]=Hash(boot);
                if(Hash(boot)!="FC479FE98C0F74219AAF1D83BC6084BB1F490B56CCA9C763EBC4F48A36F3046C" ||
                   Hash(app)!="4D215913ECE13D0CAFBC0CF0AF8F5F3F02ED8C2C8A38C2B63BF7CCE36577C812")
                    throw new IOException("Initial install must use the audited Git delivery images");
                record["backupDirectory"]=new StLinkFirmwareInstaller(run).Install(boot,app,Path.Combine(output,"backups"));
            }
            else if(mode=="update")
            {
                record["backupDirectory"]=new UsbFirmwareUpdater(run,Thread.Sleep).Update(app,Path.Combine(output,"backups"),null,Console.WriteLine);
            }
            else throw new ArgumentException("Use install or update; device must already be in DFU for update");
            record["passed"]=true;result=0;
        }
        catch(Exception ex){record["passed"]=false;record["error"]=ex.ToString();Console.WriteLine("FAILED "+ex.Message);}
        record["elapsedSeconds"]=clock.Elapsed.TotalSeconds;record["steps"]=step;
        File.WriteAllText(Path.Combine(output,"result.json"),Json.Serialize(record),new UTF8Encoding(false));
        Console.WriteLine("RESULT "+record["passed"]+"; evidence="+output);return result;
    }
}
