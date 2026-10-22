using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Protocol;

// Exercise production SerialLink with this bench's exact CDC identity. This
// sends only its existing handshake/INFO reads plus the documented DFU token.
// Pause and Dispose release only the SerialLink created by this process.
internal static class EnterDfu
{
    static int Main(string[] args)
    {
        string expected="USB\\VID_0483&PID_5740\\20553872524E";
        string identity=null;
        using(var q=new ManagementObjectSearcher("SELECT DeviceID,PNPDeviceID FROM Win32_SerialPort"))
        using(var rows=q.Get())foreach(ManagementObject row in rows)
        {using(row){if((string)row["DeviceID"]=="COM8")identity=(string)row["PNPDeviceID"];}}
        if(identity!=expected){Console.WriteLine("Selected USB identity missing; no command sent");return 2;}
        var result=new Dictionary<string,object>();var notes=new List<string>();object sync=new object();
        bool capable=false,ack=false;int returnCode=1;
        using(var link=new SerialLink("COM8"))
        {
            link.StatusChanged+=delegate(string s){lock(sync)notes.Add(s);};
            link.Connected+=delegate{link.Send(FrameCodec.MsgCfgReq,new byte[0]);};
            link.FrameReceived+=delegate(byte type,byte[] payload){lock(sync){
                if(type==FrameCodec.MsgInfo&&payload.Length>=2)capable=payload[0]>0||payload[1]>=9;
                if(type==FrameCodec.MsgAck&&payload.Length>=2&&payload[0]==FrameCodec.MsgEnterDfu&&payload[1]==0)ack=true;
            }};
            try
            {
                link.Start();var clock=Stopwatch.StartNew();
                while(clock.Elapsed.TotalSeconds<10){bool ready;lock(sync)ready=capable;if(link.IsConnected&&ready)break;Thread.Sleep(20);}
                if(!link.IsConnected||!capable)throw new IOException("No verified FW 0.9+ device on COM8");
                if(!link.Send(FrameCodec.MsgEnterDfu,Encoding.ASCII.GetBytes("NMTDFU01")))throw new IOException("DFU request write failed");
                Thread.Sleep(300);link.Pause();
                lock(sync){result["dfuAck"]=ack;returnCode=ack?0:1;}
                result["commandSent"]=true;result["verifiedVersionCapable"]=true;
            }
            catch(Exception ex){result["error"]=ex.ToString();}
            finally{link.Pause();}
        }
        lock(sync){result["statusEvents"]=notes.ToArray();}
        result["passed"]=returnCode==0;result["portReleased"]=true;result["expectedUsbIdentity"]=expected;
        string json=new JavaScriptSerializer().Serialize(result);File.WriteAllText(args[0],json,new UTF8Encoding(false));Console.WriteLine(json);return returnCode;
    }
}
