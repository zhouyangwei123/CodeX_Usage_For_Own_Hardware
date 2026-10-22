using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Protocol;

// Tests only the selected desktop board through the delivered SerialLink class.
// Own connections only; no device outputs, resets, config changes or port scans.
internal static class SerialLinkBench
{
    static readonly object Sync = new object();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly List<object> Events = new List<object>();
    static readonly Dictionary<string,long> Pending = new Dictionary<string,long>();
    static readonly List<object> Cycles = new List<object>();
    static int Connections, Disconnects, Infos, Sent, Pongs, Unexpected, MaxRtt;
    static bool FatalStatus;
    static uint LastUptime; static bool HasUptime, UptimeRegressed;
    static string Version;

    // Stop without touching another port if Windows no longer identifies COM8 as this board.
    static void CheckIdentity()
    {
        string identity=null;
        using(var q=new ManagementObjectSearcher("SELECT DeviceID,PNPDeviceID FROM Win32_SerialPort"))
        using(var rows=q.Get()) foreach(ManagementObject row in rows)
        {using(row){if((string)row["DeviceID"]=="COM8")identity=(string)row["PNPDeviceID"];}}
        if(identity!="USB\\VID_0483&PID_5740\\20553872524E") throw new IOException("COM8 identity mismatch");
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);

    // A native exclusive handle checks ownership without starting .NET's event thread.
    // This avoids using an immediate no-traffic SerialPort.Close as the preflight itself.
    static void CheckAvailable()
    {
        using(var handle=CreateFile("\\\\.\\COM8",0xC0000000,0,IntPtr.Zero,3,0x40000000,IntPtr.Zero))
        {if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
    }

    // Await a fresh verified connection and its INFO response; fail after eight seconds.
    static void AwaitConnection(SerialLink link,int previousConnections,int previousInfos)
    {
        long end=Clock.ElapsedMilliseconds+8000;
        while(Clock.ElapsedMilliseconds<end)
        {
            bool ready;lock(Sync){if(FatalStatus)throw new IOException("SerialLink reported an error");ready=Connections>previousConnections&&Infos>previousInfos;}
            if(ready&&link.IsConnected)return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("No fresh verified connection plus INFO");
    }

    // Send a uniquely marked read-only echo; callbacks measure each response independently.
    static void Ping(SerialLink link,uint number)
    {
        byte[] token={(byte)0xD9,(byte)(number>>16),(byte)(number>>8),(byte)number};
        string key=BitConverter.ToString(token);
        lock(Sync){Pending.Add(key,Clock.ElapsedMilliseconds);Sent++;}
        if(!link.Send(FrameCodec.MsgPing,token))throw new IOException("Echo send failed");
    }

    // Exercise ten Reconnect and ten Pause/Start operations, then a bounded five-minute run.
    static int Main(string[] args)
    {
        var result=new Dictionary<string,object>();int code=1;SerialLink link=null;
        string assembly=typeof(SerialLink).Assembly.Location;
        using(var hash=SHA256.Create())result["assemblySha256"]=BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(assembly))).Replace("-","");
        result["startedUtc"]=DateTime.UtcNow.ToString("o");result["assembly"]=assembly;
        result["usbIdentity"]="USB\\VID_0483&PID_5740\\20553872524E";
        try
        {
            Console.WriteLine("Preflight identity");CheckIdentity();Console.WriteLine("Preflight exclusive open");CheckAvailable();Console.WriteLine("Preflight complete");result["exclusivePreflightPassed"]=true;
            link=new SerialLink("COM8");
            link.StatusChanged+=delegate(string s){Console.WriteLine("STATUS "+Clock.ElapsedMilliseconds+" "+s);lock(Sync){Events.Add(new{atMs=Clock.ElapsedMilliseconds,status=s});if(s.Contains("失败"))FatalStatus=true;}};
            link.Connected+=delegate{lock(Sync){Connections++;}link.Send(FrameCodec.MsgCfgReq,new byte[0]);};
            link.Disconnected+=delegate{lock(Sync){Disconnects++;}};
            link.FrameReceived+=delegate(byte type,byte[] payload){lock(Sync){
                if(type==FrameCodec.MsgInfo&&payload.Length>=24)
                {
                    Infos++;Version=payload[0]+"."+payload[1];
                    uint uptime=BitConverter.ToUInt16(payload,6);
                    if(HasUptime&&uptime<LastUptime)UptimeRegressed=true;LastUptime=uptime;HasUptime=true;
                }
                if(type==FrameCodec.MsgPong&&payload.Length==4&&payload[0]==0xD9)
                {
                    string key=BitConverter.ToString(payload);long start;
                    if(Pending.TryGetValue(key,out start)){Pending.Remove(key);Pongs++;MaxRtt=Math.Max(MaxRtt,(int)(Clock.ElapsedMilliseconds-start));}
                    else Unexpected++;
                }
            }};
            Console.WriteLine("Initial Start");link.Start();AwaitConnection(link,0,0);Console.WriteLine("Initial connection complete");
            for(int i=1;i<=20;i++)
            {
                int previousConnections,previousInfos;lock(Sync){previousConnections=Connections;previousInfos=Infos;}
                long start=Clock.ElapsedMilliseconds;string operation=i%2==1?"Reconnect":"Pause/Start";
                Console.WriteLine("Cycle "+i+" "+operation);
                if(i%2==1)link.Reconnect();else{link.Pause();Thread.Sleep(150);CheckIdentity();link.Start();}
                AwaitConnection(link,previousConnections,previousInfos);
                Ping(link,(uint)i);Thread.Sleep(100);
                lock(Sync){if(Pending.Count!=0)throw new IOException("Reconnect echo missing");}
                Cycles.Add(new{cycle=i,operation=operation,reconnectMs=Clock.ElapsedMilliseconds-start,passed=true});
                Console.WriteLine("Cycle "+i+" passed");
            }
            Console.WriteLine("20 reconnect/pause cycles complete");
            int baselineConnections,baselineDisconnects;lock(Sync){baselineConnections=Connections;baselineDisconnects=Disconnects;}
            long soakStart=Clock.ElapsedMilliseconds,nextPing=soakStart,nextInfo=soakStart,nextProgress=soakStart+30000;
            uint token=1000;
            while(Clock.ElapsedMilliseconds-soakStart<300000)
            {
                long now=Clock.ElapsedMilliseconds;
                lock(Sync){if(FatalStatus||Connections!=baselineConnections||Disconnects!=baselineDisconnects)throw new IOException("Unexpected link change during continuous run");}
                if(!link.IsConnected)throw new IOException("Link lost during continuous run");
                if(now>=nextPing){Ping(link,token++);nextPing=now+250;}
                if(now>=nextInfo){if(!link.Send(FrameCodec.MsgCfgReq,new byte[0]))throw new IOException("INFO request failed");nextInfo=now+1000;}
                if(now>=nextProgress){lock(Sync)Console.WriteLine("elapsed="+(now-soakStart)/1000+"s echo="+Pongs+"/"+Sent+" INFO="+Infos+" pending="+Pending.Count);nextProgress=now+30000;}
                Thread.Sleep(5);
            }
            result["continuousRunSeconds"]=(Clock.ElapsedMilliseconds-soakStart)/1000.0;
            Thread.Sleep(500); // Drain already-sent echoes; send no new commands.
            lock(Sync){if(Pending.Count!=0||Pongs!=Sent||Unexpected!=0||UptimeRegressed||Version!="0.9")throw new IOException("Final response or uptime check failed");}
            result["unexpectedDisconnectsDuringContinuousRun"]=0;result["passed"]=true;code=0;
        }
        catch(Exception ex){result["passed"]=false;result["error"]=ex.ToString();Console.WriteLine(ex.Message);}
        finally{if(link!=null)link.Dispose();result["ownLinkDisposed"]=true;}
        try{CheckIdentity();CheckAvailable();result["portAvailableAfterDispose"]=true;}catch(Exception ex){result["portAvailableAfterDispose"]=false;result["releaseCheckError"]=ex.Message;code=1;result["passed"]=false;}
        lock(Sync){result["connections"]=Connections;result["disconnectsIncludingFinalDispose"]=Disconnects;result["infoCount"]=Infos;result["lastFirmwareVersion"]=Version;result["lastUptimeSeconds"]=LastUptime;result["uptimeRegressed"]=UptimeRegressed;result["echoSent"]=Sent;result["matchingEcho"]=Pongs;result["unansweredEcho"]=Pending.Count;result["unexpectedEcho"]=Unexpected;result["maxRttMs"]=MaxRtt;result["statusEvents"]=Events;}
        result["cycles"]=Cycles;result["elapsedSeconds"]=Clock.Elapsed.TotalSeconds;
        string json=new JavaScriptSerializer().Serialize(result);File.WriteAllText(args[0],json,new UTF8Encoding(false));
        Console.WriteLine("RESULT "+result["passed"]+"; evidence="+args[0]);return code;
    }
}
