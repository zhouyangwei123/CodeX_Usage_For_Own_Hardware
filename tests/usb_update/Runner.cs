using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Updates;

class Runner
{
 static int checks, failures;
 static byte[] app,boot;
 static string root;
 static void Check(string name,bool ok){checks++;if(!ok)failures++;Console.WriteLine((ok?"PASS ":"FAIL ")+name);}
 static bool Throws(Action action){try{action();return false;}catch{return true;}}
 static byte[] Mutate(int offset,byte xor=1){var b=(byte[])app.Clone();b[offset]^=xor;return b;}
 static void Put(byte[] b,int off,uint value){Array.Copy(BitConverter.GetBytes(value),0,b,off,4);}

 sealed class Device
 {
  public byte[] Flash=Enumerable.Repeat((byte)0xCC,65536).ToArray();
  public byte[] Options=new byte[44];
  public List<string[]> Commands=new List<string[]>();
  public string Listing="Device Index : USB1\nSerial number: TEST";
  public bool BadReadback,ShortBackup,FailWrite,FailProtect;
  public uint Id=0x469; public ushort Size=128;
  public int Writes,Starts,Protects;
  public Device(bool installed=true,bool dual=true){
   Put(Options,0,0xAAu|(dual?1u<<22:0));
   Put(Options,24,installed?(uint)((dual?3:1)<<16):127u);
   Put(Options,32,127u); Put(Options,8,0x7FFFu);
   if(installed){Array.Copy(boot,0,Flash,0,boot.Length);Array.Copy(app,0,Flash,8192,app.Length);}
  }
  public string Run(string[] a,int timeout){
   Commands.Add((string[])a.Clone());
   if(a[0]=="-l")return a[1]=="usb"?Listing:"ST-LINK SN : TESTPROBE123\n";
   int u=Array.IndexOf(a,"-u");
   if(u>=0){
    uint address=Convert.ToUInt32(a[u+1].Substring(2),16);int n=int.Parse(a[u+2]);
    byte[] data;
    if(address==0x1FFF7800)data=(byte[])Options.Clone();
    else if(address==0xE0042000)data=BitConverter.GetBytes(Id);
    else if(address==0x1FFF75E0)data=BitConverter.GetBytes(Size);
    else {
     if(address<0x08000000 || (long)address+n>0x08010000)throw new Exception("read outside bank 1");
     data=Flash.Skip((int)(address-0x08000000)).Take(n).ToArray();
     if(ShortBackup && n==57344)data=data.Take(n-1).ToArray();
     if(BadReadback && Writes>0 && address==0x08002000)data[1100]^=1;
    }
    File.WriteAllBytes(a[u+3],data.Take(n).ToArray());return "Read complete";
   }
   int w=Array.IndexOf(a,"-w");
   if(w>=0){
    Writes++;if(FailWrite)throw new IOException("write interrupted");
    uint address=Convert.ToUInt32(a[w+2].Substring(2),16);byte[] bytes=File.ReadAllBytes(a[w+1]);
    if(address<0x08000000 || address+bytes.Length>0x08010000)throw new Exception("unsafe write");
    Array.Copy(bytes,0,Flash,address-0x08000000,bytes.Length);return "Download verified";
   }
   int ob=Array.IndexOf(a,"-ob");
   if(ob>=0){
    if(a[ob+1]=="displ")return "RDP : 0xAA";
    Protects++;if(FailProtect)throw new IOException("protection failed");
    int end=int.Parse(a.First(x=>x.StartsWith("WRP1A_END=")).Split('=')[1]);Put(Options,24,(uint)(end<<16));
    return "Option bytes programmed";
   }
   if(Array.IndexOf(a,"-s")>=0 || Array.IndexOf(a,"-rst")>=0){Starts++;return "Started";}
   throw new Exception("Unexpected command");
  }
 }
 static void RunUpdate(Device d,byte[] image=null) {
  new UsbFirmwareUpdater(d.Run,delegate(int ms){}).Update(image??app,root,"TEST",delegate{},delegate(string s){});
 }
 static int Main(string[] args)
 {
  app=File.ReadAllBytes(Path.Combine(args[0],"Application-0.9.bin"));
  boot=File.ReadAllBytes(Path.Combine(args[0],"Bootloader-1.0.bin"));
  root=Path.Combine(args[1],"中文 空格备份");Directory.CreateDirectory(root);
  Check("actual application validated",!Throws(()=>FirmwareImage.Validate(app,false,true)));
  Check("actual boot image validated",!Throws(()=>FirmwareImage.Validate(boot,true,true)));
  foreach(int offset in new[]{0,4,512,516,520,524,528,532,536,540,1024})
   Check("reject corruption at "+offset,Throws(()=>FirmwareImage.Validate(Mutate(offset),false,true)));
  Check("reject boot as USB application",Throws(()=>FirmwareImage.Validate(boot,false,true)));
  byte[] large=new byte[app.Length+8];Array.Copy(app,large,app.Length);
  Check("reject appended payload",Throws(()=>FirmwareImage.Validate(large,false,true)));
  var d=new Device();RunUpdate(d);
  Check("normal update writes once and restarts once",d.Writes==1 && d.Starts==1);
  Check("USB commands never change protection or erase all",d.Protects==0 &&
   !d.Commands.Any(a=>a.Contains("-e")||a.Contains("-ob")||a.Contains("-rdu")));
  Check("USB write is confined to application",d.Commands.Where(a=>a.Contains("-w")).All(a=>a.Contains("0x08002000")));
  Check("DFU operations pinned to observed serial",d.Commands.Where(a=>a.Contains("-c")).All(a=>a.Contains("sn=TEST")));
  Check("protected boot bytes preserved",d.Flash.Take(boot.Length).SequenceEqual(boot));
  d=new Device();Check("reject invalid image before device operations",Throws(()=>RunUpdate(d,Mutate(1024)))&&d.Commands.Count==0);
  d=new Device { Listing="USB1 USB2" };Check("reject ambiguous DFU target",Throws(()=>RunUpdate(d))&&d.Writes==0);
  d=new Device { Listing="Device Index : USB1" };Check("reject absent DFU serial",Throws(()=>RunUpdate(d))&&d.Writes==0);
  d=new Device { Listing="Device Index : USB1\nSerial number: OTHER" };
  Check("reject another board before all memory operations",Throws(()=>RunUpdate(d))&&d.Commands.All(a=>a[0]=="-l"));
  d=new Device();
  Check("reject missing expected identity before device operations",Throws(()=>new UsbFirmwareUpdater(d.Run,delegate(int ms){}).Update(app,root,null,delegate{},delegate(string s){}))&&d.Commands.Count==0);
  d=new Device { Listing="No STM32 device in DFU mode connected" };Check("missing device is bounded",Throws(()=>RunUpdate(d))&&d.Commands.Count==20);
  d=new Device();d.Flash[512]^=1;Check("reject missing bootstrap",Throws(()=>RunUpdate(d))&&d.Writes==0);
  d=new Device();Put(d.Options,24,127);Check("reject unprotected bootstrap",Throws(()=>RunUpdate(d))&&d.Writes==0);
  d=new Device();Put(d.Options,40,0x10000);Check("reject BOOT_LOCK",Throws(()=>RunUpdate(d))&&d.Writes==0);
  d=new Device { ShortBackup=true };Check("no write after incomplete backup",Throws(()=>RunUpdate(d))&&d.Writes==0);
  d=new Device { BadReadback=true };Check("no restart after failed readback",Throws(()=>RunUpdate(d))&&d.Writes==1&&d.Starts==0);
  d=new Device { FailWrite=true };Check("no restart after interrupted programming",Throws(()=>RunUpdate(d))&&d.Starts==0);
  d=new Device(true,false);RunUpdate(d);Check("single-bank protected layout accepted",d.Starts==1);
  foreach(bool dual in new[]{true,false}) {
   d=new Device(false,dual);
   new StLinkFirmwareInstaller(d.Run).Install(boot,app,root);
   Check("first install and readback "+dual,d.Writes==2&&d.Protects==1&&d.Starts==1);
   Check("correct write protection "+dual,FirmwareImage.ProtectedEndPage(d.Options)==(dual?3:1));
  }
  d=new Device(false){Id=0x468};Check("installer rejects other MCU",Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Writes==0);
  d=new Device(false){Size=512};Check("installer rejects other Flash geometry",Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Writes==0);
  d=new Device();Check("installer will not overwrite protected boot",Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Writes==0);
  d=new Device(false){FailWrite=true};Check("no protection of failed install",Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Protects==0);
  foreach(bool dual in new[]{true,false}) {
   foreach(int page in new[]{0,dual?4:2}) {
    d=new Device(false,dual);Put(d.Options,32,(uint)(page|(page<<16)));
    Check("WRP1B overlap rejected before writing "+dual+" page "+page,Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Writes==0&&d.Protects==0);
   }
   d=new Device(false,dual);Put(d.Options,8,0);Put(d.Options,16,0);
   Check("PCROP overlap rejected before writing "+dual,Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Writes==0);
   d=new Device(false,dual);Put(d.Options,32,dual?0x001F001Fu:0x000F000Fu);
   new StLinkFirmwareInstaller(d.Run).Install(boot,app,root);
   Check("WRP1B outside image preserved "+dual,d.Writes==2);
  }
  d=new Device(false){BadReadback=true};Check("no protection before successful readback",Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Protects==0);
  d=new Device(false){FailProtect=true};Check("no claimed success after protection failure",Throws(()=>new StLinkFirmwareInstaller(d.Run).Install(boot,app,root))&&d.Starts==0);
  var cli=new CubeProgrammer(args[2]);
  var values=new[]{"含 空格路径.bin",@"C:\tail\","quote\"inside","", "正常"};
  string text=cli.Run(values,5000);
  string[] echoed=new JavaScriptSerializer().Deserialize<string[]>(text.Trim());
  Check("real child process Unicode and quoting",echoed.SequenceEqual(values));
  Check("zero-exit Error output rejected",Throws(()=>cli.Run(new[]{"--error"},5000)));
  Check("nonzero process exit rejected",Throws(()=>cli.Run(new[]{"--exit"},5000)));
  Check("hanging child bounded",Throws(()=>cli.Run(new[]{"--slow"},200)));
  Console.WriteLine(checks+" checks, "+failures+" failures");return failures==0?0:1;
 }
}
