using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.Tests;
using CodexToolsHost.Usage;

// Reads real local/account sources and transmits through the production bridge.
// Only availability and contract checks are recorded; never personal token counts.
internal static class LiveUsageBench
{
    sealed class Tap : IDeviceLink
    {
        readonly SerialLink link=new SerialLink("COM6");
        public event Action Connected {add{link.Connected+=value;}remove{link.Connected-=value;}}
        public event Action Disconnected {add{link.Disconnected+=value;}remove{link.Disconnected-=value;}}
        public event Action<byte,byte[]> FrameReceived {add{link.FrameReceived+=value;}remove{link.FrameReceived-=value;}}
        public event Action<string> StatusChanged {add{link.StatusChanged+=value;}remove{link.StatusChanged-=value;}}
        public bool IsConnected {get{return link.IsConnected;}}public string PortName{get{return link.PortName;}}
        public int LocalFrames,AccountFrames,Infos,Errors;public bool LocalKnown,AccountKnown,LocalNonzero,LocalCurveNonzero,LocalPage,AccountPage;
        public bool PcContract=true,SumContract=true;public string Firmware;public byte OledStatus;
        public void Start(){link.Start();}public void Pause(){link.Pause();}public void ConfigurePort(string port){link.ConfigurePort(port);}
        public void Reconnect(){link.Reconnect();}public void Dispose(){link.Dispose();}
        public bool Send(byte type,byte[] data)
        {
            bool accepted=link.Send(type,data);if(!accepted)return false;
            lock(this)
            {
                if(type==FrameCodec.MsgPcMetrics)PcContract&=data.Length==28&&data[0]==3;
                if(type==FrameCodec.MsgOledPage){LocalPage|=data[0]==4;AccountPage|=data[0]==0;}
                if(type==FrameCodec.MsgOledUsage||type==FrameCodec.MsgAccountUsage)
                {
                    bool known=(data[2]&OledUsageFrame.UsageKnown)!=0&&(data[2]&OledUsageFrame.UsageStale)==0;
                    if(type==FrameCodec.MsgOledUsage)
                    {
                        LocalFrames++;LocalKnown|=known;
                        ulong current=BitConverter.ToUInt64(data,16),input=BitConverter.ToUInt64(data,24),output=BitConverter.ToUInt64(data,32);
                        SumContract&=data[1]==0&&current==input+output;LocalNonzero|=known&&current>0;
                        LocalCurveNonzero|=known&&data.Skip(OledUsageFrame.HeaderSize).Any(x=>x>0&&x<=34);
                    }
                    else {AccountFrames++;AccountKnown|=known;}
                }
            }
            return true;
        }
    }
    static int Main(string[] args)
    {
        var result=new Dictionary<string,object>();int code=1;
        try
        {
            if(UsbDeviceIdentity.SerialForPort("COM6")!="20553872524E")throw new IOException("Device identity mismatch");
            var config=AppConfig.CreateDefault();config.SerialPort="COM6";
            config.Bindings.Clear();config.EncoderRotate=new ActionSpec("none","");
            var constructor=typeof(BridgeService).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==4);
            using(var usage=new LocalUsageService(null))
            using(var tap=new Tap())
            using(var source=CodexStatusProvider.CreateDefault(30))
            using(var bridge=(BridgeService)constructor.Invoke(new object[]{config,tap,source,new FakeDeepSeekSource()}))
            {
                tap.FrameReceived+=delegate(byte type,byte[] data){lock(tap){
                    if(type==FrameCodec.MsgInfo&&data.Length>=24){tap.Infos++;tap.Firmware=data[0]+"."+data[1];tap.OledStatus=data[4];}
                    if(type==FrameCodec.MsgAck&&data.Length>=2&&data[1]!=0)tap.Errors++;
                }};
                typeof(BridgeService).GetProperty("UsageSource",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(bridge,usage,null);
                usage.Start();bridge.Start();
                DateTime end=DateTime.UtcNow.AddSeconds(60);
                while(DateTime.UtcNow<end){lock(tap){if(tap.LocalKnown&&tap.AccountKnown)break;}Thread.Sleep(250);}
                bridge.SetUsageScope("account");Thread.Sleep(2200);
                bridge.SetUsageScope("local");Thread.Sleep(2200);
                lock(tap)
                {
                    var activity=UsageActivity.Create(usage.Report,DateTimeOffset.UtcNow);
                    bool history=activity.HourTokens>0&&activity.Points.Any(x=>x.Time<DateTimeOffset.UtcNow.AddMinutes(-1)&&x.InputTokens+x.OutputTokens>0);
                    bool passed=tap.LocalKnown&&tap.AccountKnown&&tap.LocalPage&&tap.AccountPage&&tap.SumContract&&tap.PcContract&&
                        tap.Firmware=="0.10"&&(tap.OledStatus&1)!=0&&tap.Errors==0&&tap.LocalFrames>1&&tap.AccountFrames>1;
                    if(args.Contains("--require-local-history"))passed&=history&&tap.LocalCurveNonzero;
                    result["passed"]=passed;result["hostVersion"]=typeof(BridgeService).Assembly.GetName().Version.ToString();
                    result["localKnown"]=tap.LocalKnown;result["accountKnown"]=tap.AccountKnown;result["localCurrentNonzero"]=tap.LocalNonzero;
                    result["localHistoryRestored"]=history;result["nonzeroCurveSentToDevice"]=tap.LocalCurveNonzero;
                    result["localFrames"]=tap.LocalFrames;result["accountFrames"]=tap.AccountFrames;
                    result["localAndAccountPagesSent"]=tap.LocalPage&&tap.AccountPage;result["endedOnLocal"]=config.UsageDisplayScope=="local";
                    result["pcMetrics28Bytes"]=tap.PcContract;result["localSumMatches"]=tap.SumContract;
                    result["firmware"]=tap.Firmware;result["oledOnline"]=(tap.OledStatus&1)!=0;result["errorAcks"]=tap.Errors;
                    result["personalValuesOrIdentityRecorded"]=false;code=passed?0:1;
                }
            }
        }
        catch(Exception ex){result["passed"]=false;result["errorType"]=ex.GetBaseException().GetType().Name;}
        string json=new JavaScriptSerializer().Serialize(result);File.WriteAllText(args[0],json);Console.WriteLine(json);return code;
    }
}
