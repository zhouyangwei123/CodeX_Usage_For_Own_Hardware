using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.Tests;
using CodexToolsHost.UI;
using CodexToolsHost.Usage;

internal static class OledUsageTests
{
    static int checks, goldenCount;
    static string output, native;
    static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static readonly Type Pixel = typeof(AppConfig).Assembly.GetType("CodexToolsHost.UI.OledPixelRenderer");
    static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    static void Set(object item, string property, object value) { item.GetType().GetProperty(property, Instance).SetValue(item, value, null); }
    static AccountUsageSnapshot Account(string json, DateTimeOffset now)
    { return AccountUsageSnapshot.Parse((Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(json), now); }
    static OledUsageFrame Copy(OledUsageFrame frame)
    {
        var result = new OledUsageFrame();
        foreach(var field in typeof(OledUsageFrame).GetFields())if(!field.IsStatic)field.SetValue(result,field.GetValue(frame));
        result.Points=(byte[])frame.Points.Clone();return result;
    }
    static void Golden(string name, OledUsageFrame frame)
    {
        string stem=Path.Combine(output,name);
        File.WriteAllBytes(stem+".payload",frame.Encode());
        byte[] expected=(byte[])Pixel.GetMethod("RenderBits",Static).Invoke(null,new object[]{frame,"U80K","D1.2M"});
        var start=new ProcessStartInfo(native,"\""+stem+".payload\" \""+stem+".native-bits\"") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        using(var process=Process.Start(start))
        { if(!process.WaitForExit(10000)){process.Kill();throw new Exception("Native renderer timeout");}Check(process.ExitCode==0,"Native renderer accepted "+name+": "+process.StandardError.ReadToEnd()); }
        Check(expected.SequenceEqual(File.ReadAllBytes(stem+".native-bits")),"Host/firmware pixel equality "+name);
        Check(expected.Take(128).All(value=>(value&3)==0),"Two blank top rows "+name);
        Check(frame.Encode().Length<=FrameCodec.MaxPayload,"Frame size "+name);
        Check(frame.DetailText().Length<=21,"Footer fits "+name);
        Check(frame.ApiText().StartsWith("API ",StringComparison.Ordinal),"API spacing "+name);
        goldenCount++;
    }
    static Bitmap Render(OledUsageFrame frame,bool expanded)
    {
        var pc=new PcMetricsSnapshot { SampledAtUtc=DateTimeOffset.UtcNow,NetworkSpeedAvailable=true,NetworkUploadKiBPerSecond=80,NetworkDownloadKiBPerSecond=1234 };
        return (Bitmap)Pixel.GetMethod("Render",Static).Invoke(null,new object[]{new Size(408,expanded?216:108),frame,pc,expanded});
    }
    [STAThread] static int Main(string[] args)
    {
        try {output=Path.GetFullPath(args[0]);native=args[1];Run();Console.WriteLine("PASS "+checks+" account/OLED/skin checks, "+goldenCount+" exact C/C# frame comparisons");return 0;}
        catch(Exception ex){Console.Error.WriteLine("FAIL "+ex.GetBaseException().Message);return 1;}
    }
    static void Run()
    {
        var now=DateTimeOffset.UtcNow;
        var quota=new QuotaSnapshot(11,78,now.AddHours(1),now.AddDays(4).AddHours(8),now,false,300,10080,"");
        var list=new List<UsageActivityPoint>();
        for(int i=0;i<=360;i++)
        {var point=new UsageActivityPoint();Set(point,"Time",now.AddSeconds((i-360)*10));Set(point,"InputTokens",(long)(3000+1500*Math.Sin(i*.075)));Set(point,"OutputTokens",(long)(650+300*Math.Cos(i*.04)));Set(point,"HasData",true);list.Add(point);}
        var local=new UsageActivitySnapshot();
        Set(local,"AsOf",now);Set(local,"IsReady",true);Set(local,"Status","正常");Set(local,"InputTokensPerMinute",5300L);Set(local,"OutputTokensPerMinute",2000L);Set(local,"HourTokens",120000L);Set(local,"Points",list.AsReadOnly());
        var buckets=new List<object>();
        for(int i=0;i<14;i++)buckets.Add(new Dictionary<string,object>{{"startDate",DateTime.Today.AddDays(i-14).ToString("yyyy-MM-dd")},{"tokens",(long)(1800000+900000*Math.Sin(i*.7))}});
        var data=new Dictionary<string,object>{{"summary",new Dictionary<string,object>{{"lifetimeTokens",12345678901L}}},{"dailyUsageBuckets",buckets}};
        var account=AccountUsageSnapshot.Parse(data,now);
        Check(account.LifetimeTokens==12345678901L&&account.Days.Count==14,"Account counts retain 64 bits");
        var projector=new OledUsageProjector();
        var accountFrame=projector.Create(true,quota,local,account,678,true,false,false,now);
        var localFrame=projector.Create(false,quota,local,account,678,true,false,false,now);
        Check(accountFrame.View==1&&localFrame.View==0,"Distinct source identity");
        Check(localFrame.Remaining==78&&accountFrame.Remaining==78,"7D selected by window duration");
        Check(localFrame.Points.Length==76&&localFrame.Encode().Length==128,"Bounded one-hour peak envelope");
        Check(localFrame.Current==7300&&localFrame.Input==5300&&localFrame.Output==2000,"Current input/output total");
        Check(localFrame.Points.Last()==(byte)Math.Round(localFrame.Current*34m/localFrame.Upper,MidpointRounding.AwayFromZero),"Current matches last point");
        Check(accountFrame.Points.Length==14&&accountFrame.Current==(ulong)account.Days.Last().Tokens,"Daily current is last service bucket");
        Check(accountFrame.Input==0&&accountFrame.Output==0,"No invented account input/output");
        Golden("account-default",accountFrame);Golden("local-switch",localFrame);
        using(var image=Render(accountFrame,true))image.Save(Path.Combine(output,"pixel-account.png"),ImageFormat.Png);
        using(var image=Render(localFrame,true))image.Save(Path.Combine(output,"pixel-local.png"),ImageFormat.Png);
        using(var image=Render(accountFrame,false))
        {
            image.Save(Path.Combine(output,"pixel-collapsed.png"),ImageFormat.Png);
            Check(image.Height==108,"Collapsed skin retains 28 content rows plus bezel");
            bool empty=true;for(int y=54;y<72;y++)for(int x=12;x<396;x++)empty&=image.GetPixel(x,y).ToArgb()==Color.Black.ToArgb();
            Check(empty,"Collapsed gap has no chart fragments");
            Check(image.GetPixel(0,0).A==0&&image.GetPixel(60,0).A==255,"Stepped corners and solid rim");
        }
        foreach(ulong top in new ulong[]{1,2,5,10,50,1000,2000,5000,100000,1000000,5000000000,10000000000000000000UL})
        {
            var f=Copy(localFrame);f.Upper=top;f.Current=top/3;f.Input=f.Current;f.Output=0;
            f.Points=Enumerable.Range(0,76).Select(i=>(byte)(i%35)).ToArray();f.Points[75]=(byte)Math.Round(f.Current*34m/f.Upper,MidpointRounding.AwayFromZero);
            Golden("scale-"+top,f);
        }
        foreach(ulong money in new ulong[]{0,678,9999,10000,104950,99995000,ulong.MaxValue})
        {var f=Copy(localFrame);f.BalanceCents=money;Golden("money-"+money,f);}
        foreach(byte remaining in new byte[]{0,100}){var f=Copy(localFrame);f.Remaining=remaining;Golden("quota-"+remaining,f);}
        var stale=Copy(accountFrame);stale.Flags|=OledUsageFrame.QuotaStale|OledUsageFrame.UsageStale|OledUsageFrame.ApiStale;Golden("stale",stale);
        var missing=Copy(localFrame);missing.Flags=0;missing.Points=new byte[0];Golden("unknown",missing);
        var partial=Copy(accountFrame);partial.Flags|=OledUsageFrame.Partial;partial.Points[5]=255;Golden("partial-gap",partial);
        var none=projector.Create(true,quota,local,AccountUsageSnapshot.Empty(),678,true,false,false,now);
        Check(!none.Has(OledUsageFrame.UsageKnown),"Missing account does not fall back to local");
        var fiveHourOnly=new QuotaSnapshot(50,null,now.AddHours(1),null,now,false,300,null,"");
        Check(!projector.Create(true,fiveHourOnly,local,account,0,false,false,false,now).Has(OledUsageFrame.QuotaKnown),"5h is not relabeled as 7D");
        foreach(var pair in new[]{new[]{0UL,1UL},new[]{1000UL,1000UL},new[]{1001UL,2000UL},new[]{20001UL,50000UL}})Check(OledUsageProjector.NiceCeiling(pair[0])==pair[1],"Nice ceiling "+pair[0]);
        Check(AppConfig.CreateDefault().UsageDisplayScope=="local"&&AppConfig.CreateDefault().DefaultOledPage==4,"Local usage and local OLED page are defaults");
        Check(AppConfig.NormalizeQuotaHudStyle("pixel")=="pixel","Pixel skin config recognized");
        Check(AppConfig.NormalizeUsageScope(null)=="local"&&AppConfig.NormalizeUsageScope("account")=="account","Scope normalization preserves account selection");
        string[] invalid={"{\"dailyUsageBuckets\":[{\"startDate\":\"bad\",\"tokens\":1}]}","{\"summary\":{\"lifetimeTokens\":-1}}","{\"dailyUsageBuckets\":[{\"startDate\":\"2026-01-01\",\"tokens\":1.5}]}"};
        foreach(string json in invalid){bool rejected=false;try{Account(json,now);}catch(InvalidDataException){rejected=true;}Check(rejected,"Invalid account count/date rejected");}
        Check(Account("{\"summary\":null,\"dailyUsageBuckets\":null}",now).Days.Count==0,"Nullable account metrics remain unavailable");
        var aged=AccountUsageSnapshot.Parse(data,now.AddMinutes(-16));
        var agedFrame=projector.Create(true,quota,local,aged,0,false,false,false,now);
        Check(!agedFrame.Has(OledUsageFrame.UsageKnown)&&agedFrame.Has(OledUsageFrame.UsageStale),"Expired account data is explicitly stale");
        list[123].GetType().GetProperty("InputTokens",Instance).SetValue(list[123],90000L,null);
        var spiked=projector.Create(false,quota,local,account,0,false,false,false,now);
        Check(spiked.Upper==100000&&spiked.Points.Max()>=30,"One-sample peak survives downsampling");
        WireChecks(account,quota);
        UiChecks(account,local,quota);
    }
    sealed class Source : ICodexStatusSource,IAccountUsageSource
    {
        public event Action Changed;public event Action<string> StatusChanged;
        public QuotaSnapshot Quota{get;set;} public AccountUsageSnapshot AccountUsage{get;set;}
        public int State{get{return 1;}}public string StatusText{get{return "fixture";}}
        public Task StartAsync(){return Task.FromResult(0);}public Task RefreshQuotaAsync(){return Task.FromResult(0);}
        public void Stop(){}public void Dispose(){}public void Notify(){if(Changed!=null)Changed();if(StatusChanged!=null)StatusChanged("fixture");}
    }
    sealed class CaptureLink : IDeviceLink
    {
        public event Action Connected { add {} remove {} }
        public event Action Disconnected { add {} remove {} }
        public event Action<byte,byte[]> FrameReceived { add {} remove {} }
        public event Action<string> StatusChanged { add {} remove {} }
        public bool IsConnected { get { return true; } }
        public string PortName { get { return "OFFLINE"; } }
        public readonly List<KeyValuePair<byte,byte[]>> Frames=new List<KeyValuePair<byte,byte[]>>();
        public void Start(){}public void Pause(){}public void ConfigurePort(string port){}public void Reconnect(){}public void Dispose(){}
        public bool Send(byte type,byte[] payload){Frames.Add(new KeyValuePair<byte,byte[]>(type,payload));return true;}
    }
    static void WireChecks(AccountUsageSnapshot account,QuotaSnapshot quota)
    {
        var config=AppConfig.CreateDefault();config.DefaultOledPage=4;
        var link=new CaptureLink();var source=new Source{AccountUsage=account,Quota=quota};
        var constructor=typeof(BridgeService).GetConstructors(Instance).Single(c=>c.GetParameters().Length==4);
        using(var bridge=(BridgeService)constructor.Invoke(new object[]{config,link,source,new FakeDeepSeekSource()}))
        {
            typeof(BridgeService).GetField("_firmwareMinor",Instance).SetValue(bridge,9);
            typeof(BridgeService).GetMethod("SendInitial",Instance).Invoke(bridge,null);
            Check(!link.Frames.Any(f=>f.Key==FrameCodec.MsgOledUsage||f.Key==FrameCodec.MsgAccountUsage),"Old firmware receives no new messages");
            Check(link.Frames.Single(f=>f.Key==FrameCodec.MsgPcMetrics).Value.Length==28,"PC_METRICS stays 28 bytes on old firmware");
            Check(link.Frames.Single(f=>f.Key==FrameCodec.MsgOledPage).Value[0]==0,"Old firmware page 4 has a supported fallback");
            byte[] oldStatus=link.Frames.Single(f=>f.Key==FrameCodec.MsgStatus).Value;
            link.Frames.Clear();
            typeof(BridgeService).GetField("_firmwareMinor",Instance).SetValue(bridge,10);
            typeof(BridgeService).GetMethod("SendInitial",Instance).Invoke(bridge,null);
            Check(link.Frames.Single(f=>f.Key==FrameCodec.MsgOledUsage).Value[1]==0&&link.Frames.Single(f=>f.Key==FrameCodec.MsgAccountUsage).Value[1]==1,"New firmware receives separately identified sources");
            Check(link.Frames.Single(f=>f.Key==FrameCodec.MsgStatus).Value.SequenceEqual(oldStatus),"STATUS bytes remain unchanged");
            Check(link.Frames.Single(f=>f.Key==FrameCodec.MsgPcMetrics).Value.Length==28,"PC_METRICS stays 28 bytes on new firmware");
            Check(link.Frames.Single(f=>f.Key==FrameCodec.MsgOledPage).Value[0]==4,"New firmware supports local page 4");
        }
    }
    static void UiChecks(AccountUsageSnapshot account,UsageActivitySnapshot local,QuotaSnapshot quota)
    {
        string home=Path.Combine(output,"empty-local");Directory.CreateDirectory(Path.Combine(home,"sessions"));
        string settings=Path.Combine(output,"test-settings");Directory.CreateDirectory(settings);
        var config=AppConfig.Load(settings,settings);config.QuotaHudStyle="pixel";config.UsageDisplayScope="local";
        var source=new Source{AccountUsage=account,Quota=quota};
        var constructor=typeof(BridgeService).GetConstructors(Instance).Single(c=>c.GetParameters().Length==4);
        using(var service=new LocalUsageService(home))
        using(var bridge=(BridgeService)constructor.Invoke(new object[]{config,new MockDeviceLink(),source,new FakeDeepSeekSource()}))
        {
            typeof(BridgeService).GetProperty("UsageSource",Instance).SetValue(bridge,service,null);
            service.RefreshAsync().Wait();
            using(var panel=new UsagePanel(service,bridge,config))
            using(var window=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ClientSize=new Size(900,650)})
            {
                window.Controls.Add(panel);window.Show();Application.DoEvents();panel.PerformLayout();
                var selector=panel.Controls.Find("usageSource",true).OfType<ComboBox>().Single();
                var grid=panel.Controls.Find("usageGrid",true).OfType<DataGridView>().Single();
                Check(selector.SelectedIndex==1&&grid.Columns[2].Visible,"Usage panel defaults to local");
                selector.SelectedIndex=0;Check(config.UsageDisplayScope=="account"&&!grid.Columns[2].Visible,"Account source is selectable without false splits");
                Check(selector.Text=="整个账号 · 服务端","Account source is visibly identified");
                selector.SelectedIndex=1;Check(config.UsageDisplayScope=="local"&&grid.Columns[2].Visible,"Local source can be selected");
                selector.SelectedIndex=0;Check(config.UsageDisplayScope=="account","Account source can be restored");
                Check(AppConfig.Load(settings,settings).UsageDisplayScope=="account","Scope persists across restart");
                using(var image=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(image,panel.ClientRectangle);image.Save(Path.Combine(output,"account-usage-panel.png"));}
            }
            using(var hud=new QuotaHudForm(config,source,new FakeDeepSeekSource(),delegate{},bridge,service))
            {
                using(var frame=(Bitmap)typeof(QuotaHudForm).GetMethod("RenderFrame",Instance).Invoke(hud,null))
                {Check(frame.Width==408&&frame.Height==216,"75 percent pixel skin keeps content at 3x with bezel");frame.Save(Path.Combine(output,"pixel-hud-form.png"));}
                Check(hud.ToggleChart()&&hud.ClientSize==new Size(408,108),"Pixel skin folds to footer without losing scale");
                Check(hud.ToggleChart()&&hud.ClientSize==new Size(408,216),"Pixel skin expands back to 35-pixel curve");
            }
            Type renderer=typeof(AppConfig).Assembly.GetType("CodexToolsHost.UI.QuotaHudRenderer");
            using(var instance=(IDisposable)Activator.CreateInstance(renderer,true))
                foreach(string style in new[]{"glass","minimal","classic"})
                {
                    using(var image=(Bitmap)renderer.GetMethod("RenderUsageFrame",Instance).Invoke(instance,new object[]{new Size(420,300),quota,style,0f,false,"CNY 6.78\r\n已更新", "U80K D1.2M",false,true,false,false,local,true,bridge.GetOledUsage(true),account}))
                        image.Save(Path.Combine(output,"account-"+style+".png"));
                    Check(true,"Account rendering "+style);
                }
        }
    }
}
