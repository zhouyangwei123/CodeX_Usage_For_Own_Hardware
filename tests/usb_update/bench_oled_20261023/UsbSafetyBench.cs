using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Protocol;
using CodexToolsHost.UI;
using CodexToolsHost.Updates;

class UsbSafetyBench
{
 static readonly BindingFlags Fields=BindingFlags.NonPublic|BindingFlags.Instance;
 [STAThread] static int Main(string[] args)
 {
  string cli=args[0], package=args[1], output=Path.GetFullPath(args[2]), port=args[3], expected=args[4];
  Directory.CreateDirectory(output);
  var record=new Dictionary<string,object>();record["startedUtc"]=DateTime.UtcNow.ToString("o");
  record["hostVersion"]=typeof(SerialLink).Assembly.GetName().Version.ToString();
  record["expectedUsbSerial"]=expected;
  var tool=new CubeProgrammer(cli);int memoryCommands=0,steps=0;bool checkedExit=false;
  string bootBefore=null,optionsBefore=null;
  var bridge=(BridgeService)FormatterServices.GetUninitializedObject(typeof(BridgeService));
  var tray=(TrayApp)FormatterServices.GetUninitializedObject(typeof(TrayApp));
  var status=new ToolStripMenuItem();
  typeof(TrayApp).GetField("_bridge",Fields).SetValue(tray,bridge);
  typeof(TrayApp).GetField("_statusItem",Fields).SetValue(tray,status);
  int threadExits=0;tray.ThreadExit+=delegate{threadExits++;};
  var ready=new ManualResetEvent(false);string version=null;
  var link=new SerialLink(port);
  typeof(BridgeService).GetField("_link",Fields).SetValue(bridge,link);
  typeof(BridgeService).GetField("_firmwareMinor",Fields).SetValue(bridge,9);
  link.Connected+=delegate{link.Send(FrameCodec.MsgCfgReq,new byte[0]);};
  link.FrameReceived+=delegate(byte type,byte[] data){if(type==FrameCodec.MsgInfo&&data.Length>=2){version=data[0]+"."+data[1];ready.Set();}};
  link.StatusChanged+=Console.WriteLine;
  Func<string[],int,string> run=null;
  run=delegate(string[] a,int timeout){
   int step=++steps;string stem=Path.Combine(output,step.ToString("D2"));
   File.WriteAllText(stem+"-args.json",new JavaScriptSerializer().Serialize(a),new UTF8Encoding(false));
   if(a.Contains("-c")){
    memoryCommands++;
    if(!a.Contains("sn="+expected))throw new IOException("Unbound device operation");
   }
   if(a.Contains("-w")){
    typeof(TrayApp).GetMethod("ExitApp",Fields).Invoke(tray,null);tray.ExitThread();
    checkedExit=threadExits==0 && !(bool)typeof(TrayApp).GetField("_exiting",Fields).GetValue(tray);
    if(!checkedExit)throw new Exception("Tray exit guard failed during upgrade");
   }
   if(a.Contains("-s")){
    string afterBoot=Path.Combine(output,"boot-after.bin"),afterOptions=Path.Combine(output,"options-after.bin");
    run(a.Take(3).Concat(new[]{"-u","0x08000000","8192",afterBoot}).ToArray(),15000);
    run(a.Take(3).Concat(new[]{"-u","0x1FFF7800","44",afterOptions}).ToArray(),15000);
    bool bootSame=File.ReadAllBytes(bootBefore).SequenceEqual(File.ReadAllBytes(afterBoot));
    bool optionsSame=File.ReadAllBytes(optionsBefore).SequenceEqual(File.ReadAllBytes(afterOptions));
    record["boot8192BytesUnchanged"]=bootSame;record["optionBytesUnchanged"]=optionsSame;
    if(!bootSame||!optionsSame)throw new IOException("Protected boot or options changed");
   }
   string text=tool.Run(a,timeout);
   if(a.Contains("-u")&&a.Contains("0x08000000")&&a.Last().EndsWith("installed-boot.bin"))bootBefore=a.Last();
   if(a.Contains("-u")&&a.Last().EndsWith("option-bytes.bin"))optionsBefore=a.Last();
   File.WriteAllText(stem+"-output.txt",text,new UTF8Encoding(false));return text;
  };
  int code=1;
  try {
   if(UsbDeviceIdentity.SerialForPort(port)!=expected)throw new IOException("CDC identity mismatch");
   record["cdcIdentityMatched"]=true;link.Start();
   if(!ready.WaitOne(10000)||version!="0.9")throw new IOException("Expected firmware INFO missing");
   record["initialFirmware"]=version;
   if(!bridge.TryBeginFirmwareUpdate())throw new IOException("Update already active");
   string[] applications=Directory.GetFiles(package,"Application-*.bin");
   if(applications.Length!=1)throw new IOException("Expected one application image");
   byte[] app=File.ReadAllBytes(applications[0]);
   string expectedVersion=FirmwareImage.Version(app);
   record["targetFirmware"]=expectedVersion;
   bool mismatchRejected=false;
   try {new UsbFirmwareUpdater(run,Thread.Sleep).Update(app,Path.Combine(output,"negative-backups"),"MISMATCH",delegate{bridge.EnterFirmwareUpdate(expected);},Console.WriteLine);}
   catch(IOException ex){mismatchRejected=ex.Message.Contains("不一致");Console.WriteLine(ex.Message);}
   record["actualDfuMismatchRejectedBeforeMemoryAccess"]=mismatchRejected&&memoryCommands==0;
   if(!mismatchRejected||memoryCommands!=0)throw new IOException("DFU identity negative check failed");
   record["backupDirectory"]=new UsbFirmwareUpdater(run,Thread.Sleep).Update(app,Path.Combine(output,"backups"),expected,null,Console.WriteLine);
   ready.Reset();link.Start();
   if(!ready.WaitOne(10000)||version!=expectedVersion)throw new IOException("USB update did not reconnect with expected firmware INFO");
   record["trayExitDeniedWhileActualUpdateActive"]=checkedExit;
   record["reconnectedFirmware"]=version;record["passed"]=true;code=0;
  } catch(Exception ex){record["passed"]=false;record["error"]=ex.ToString();Console.WriteLine(ex.Message);}
  finally{bridge.EndFirmwareUpdate();link.Dispose();status.Dispose();ready.Dispose();}
  record["steps"]=steps;record["finishedUtc"]=DateTime.UtcNow.ToString("o");
  File.WriteAllText(Path.Combine(output,"result.json"),new JavaScriptSerializer().Serialize(record),new UTF8Encoding(false));
  Console.WriteLine("RESULT "+record["passed"]);return code;
 }
}
