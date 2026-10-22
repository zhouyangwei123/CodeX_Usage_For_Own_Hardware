using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Protocol;

// Bounded real-device probe. It only sends PING and CFG_REQ, never settings,
// output commands, update requests, or reset commands. The port open is exclusive;
// an occupied port aborts this run without retrying or touching its owner.
internal static class SerialProbe
{
    static int Main(string[] args)
    {
        string portName=args[0], expected=args[1], output=args[2];
        int seconds=int.Parse(args[3]);
        var report=new Dictionary<string,object>();
        var frames=new List<object>();
        report["port"]=portName;report["expectedUsbIdentity"]=expected;
        report["startedUtc"]=DateTime.UtcNow.ToString("o");
        report["processId"]=Process.GetCurrentProcess().Id;
        report["commands"]=new[]{"PING","CFG_REQ"};
        int result=1;
        try
        {
            string pnp=null;
            using(var query=new ManagementObjectSearcher("SELECT DeviceID,PNPDeviceID FROM Win32_SerialPort"))
            using(var rows=query.Get())foreach(ManagementObject row in rows)
            {using(row){if(string.Equals((string)row["DeviceID"],portName,StringComparison.OrdinalIgnoreCase))pnp=(string)row["PNPDeviceID"];}}
            if(!string.Equals(pnp,expected,StringComparison.OrdinalIgnoreCase))throw new IOException("USB identity does not match the selected device; not opening port");
            report["identityMatched"]=true;
            using(var serial=new SerialPort(portName,115200,Parity.None,8,StopBits.One))
            {
                serial.DtrEnable=false;serial.RtsEnable=false;serial.Handshake=Handshake.None;
                serial.ReadTimeout=100;serial.WriteTimeout=500;
                serial.Open(); // Windows grants exclusive ownership or rejects this run.
                report["exclusiveOpenSucceeded"]=true;
                try
                {
                    var watch=Stopwatch.StartNew();var rx=new List<byte>();
                    var pending=new Dictionary<uint,long>();var latencies=new List<long>();
                    int sent=0,pongs=0,infos=0,unexpected=0,bytes=0;long nextPing=0,nextInfo=0;
                    uint sequence=0x22A00000;
                    while(watch.Elapsed.TotalSeconds<seconds)
                    {
                        if(watch.ElapsedMilliseconds>=nextPing)
                        {
                            uint current=++sequence;byte[] f=FrameCodec.Encode(FrameCodec.MsgPing,BitConverter.GetBytes(current));
                            pending[current]=watch.ElapsedMilliseconds;serial.Write(f,0,f.Length);sent++;nextPing=watch.ElapsedMilliseconds+250;
                        }
                        if(watch.ElapsedMilliseconds>=nextInfo)
                        {byte[] f=FrameCodec.Encode(FrameCodec.MsgCfgReq,new byte[0]);serial.Write(f,0,f.Length);nextInfo=watch.ElapsedMilliseconds+1000;}
                        int count=serial.BytesToRead;
                        if(count==0){Thread.Sleep(2);continue;}
                        byte[] chunk=new byte[Math.Min(count,4096)];int n=serial.Read(chunk,0,chunk.Length);bytes+=n;
                        for(int i=0;i<n;i++)rx.Add(chunk[i]);
                        while(rx.Count>=5)
                        {
                            if(rx[0]!=0xA5||rx[1]!=0x5A){rx.RemoveAt(0);continue;}
                            int length=rx[3];if(length>128){rx.RemoveAt(0);continue;}
                            int size=length+5;if(rx.Count<size)break;
                            byte[] f=rx.GetRange(0,size).ToArray();
                            if(FrameCodec.Crc8(f,2,length+2)!=f[size-1]){rx.RemoveAt(0);continue;}
                            rx.RemoveRange(0,size);
                            if(f[2]==FrameCodec.MsgPong&&length==4)
                            {uint id=BitConverter.ToUInt32(f,4);long at;if(pending.TryGetValue(id,out at)){pongs++;latencies.Add(watch.ElapsedMilliseconds-at);pending.Remove(id);}else unexpected++;}
                            else if(f[2]==FrameCodec.MsgInfo&&length>=24)
                            {
                                infos++;var item=new Dictionary<string,object>();
                                item["atMs"]=watch.ElapsedMilliseconds;item["firmware"]=f[4]+"."+f[5];
                                item["buttons"]=f[6];item["encoder"]=f[7];item["oledStatus"]=f[8];item["rgbCount"]=f[9];
                                item["uptimeSeconds"]=BitConverter.ToUInt16(f,10);item["model"]=Encoding.ASCII.GetString(f,12,16).TrimEnd('\0');
                                frames.Add(item);
                            }
                        }
                    }
                    long max=0;foreach(long latency in latencies)if(latency>max)max=latency;
                    report["seconds"]=watch.Elapsed.TotalSeconds;report["pingSent"]=sent;report["matchingPong"]=pongs;
                    report["unanswered"]=pending.Count;report["unexpectedPong"]=unexpected;report["maxRttMs"]=max;
                    report["infoCount"]=infos;report["receivedBytes"]=bytes;report["infoSamples"]=frames;
                    bool passed=pongs==sent&&infos>0;report["passed"]=passed;result=passed?0:1;
                }
                finally{serial.Close();report["ownPortClosed"]=!serial.IsOpen;}
            }
        }
        catch(Exception ex){report["passed"]=false;report["errorType"]=ex.GetType().Name;report["error"]=ex.Message;}
        string json=new JavaScriptSerializer().Serialize(report);File.WriteAllText(output,json,new UTF8Encoding(false));Console.WriteLine(json);return result;
    }
}
