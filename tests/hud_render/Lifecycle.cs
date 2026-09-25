using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.Tests;
using CodexToolsHost.UI;

internal static class Lifecycle
{
    private static int _checks;
    private static void Check(bool condition,string name){if(!condition)throw new Exception(name);_checks++;}
    private static object Field(object instance,string name){return instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(instance);}
    private static object Invoke(object instance,string name,params object[] args){return instance.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(instance,args);}
    private static void Pump(int ms){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")]private static extern int SetWindowLong(IntPtr window,int index,int value);
    [DllImport("user32.dll")]private static extern int GetGuiResources(IntPtr process,int flags);
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
    [STAThread]private static int Main(string[] args)
    {
        try
        {
            var config=AppConfig.Load(args[0],args[0]);
            config.QuotaDisplaySource="deepseek";
            config.QuotaHudOpacity=.35;
            var source=new MutableSource();var api=new FakeDeepSeekSource();var go=new FakeOpenCodeGoSource();
            var monitor=new PcMonitorService(new Metrics(),500);monitor.RefreshNow();
            using(var bridge=new BridgeService(config,new MockDeviceLink(),source,null,api,monitor,new FakeChatGptRestartService(),go))
            {
                int refreshCalls=0;
                var form=new QuotaHudForm(config,source,api,delegate{refreshCalls++;},bridge);
                try
                {
                    Check(form.Region==null,"fractional alpha must not be clipped by a Region");
                    Check(form.Opacity==1d,"Form.Opacity cannot be used with UpdateLayeredWindow");
                    Check(!form.IsHandleCreated,"constructor stays handle-free");
                    Check(!((System.Windows.Forms.Timer)Field(form,"_animationTimer")).Enabled,"hidden animation disabled");
                    Check(!((System.Windows.Forms.Timer)Field(form,"_displayTimer")).Enabled,"hidden countdown disabled");
                    Check((string)Field(form,"_networkDisplayText")=="↑2K ↓512K","network preserved");
                    var render=form.GetType().GetMethod("RenderFrame",BindingFlags.Instance|BindingFlags.NonPublic);
                    Check(render!=null,"offscreen RenderFrame hook");
                    foreach(var style in new[]{"glass","minimal","classic"})
                    foreach(var scale in new[]{60,75,90,100})
                    {
                        config.QuotaHudStyle=style;config.QuotaHudScalePercent=scale;
                        form.ApplyDisplaySettings();form.Location=new Point(-20000,-20000);
                        using(var bitmap=(Bitmap)render.Invoke(form,null))
                        {
                            Check(bitmap.Size==form.ClientSize,"frame matches client size");
                            Check(bitmap.PixelFormat==PixelFormat.Format32bppPArgb,"form premultiplied frame");
                            bitmap.Save(Path.Combine(args[0],"form-"+style+"-"+scale+".png"));
                        }
                    }
                    config.QuotaDisplaySource="opencodego";
                    form.ApplyDisplaySettings();form.Location=new Point(-20000,-20000);
                    Check((bool)Field(form,"_apiIsOpenCodeGo"),"display settings apply the selected source immediately");
                    config.QuotaDisplaySource="deepseek";
                    form.ApplyDisplaySettings();form.Location=new Point(-20000,-20000);
                    Check(!(bool)Field(form,"_apiIsOpenCodeGo"),"display settings switch back to API balance");
                    IntPtr foreground=GetForegroundWindow();
                    IntPtr handle=form.Handle;
                    Check((GetWindowLong(handle,-20)&0x80000)!=0,"layered native style");
                    Check((GetWindowLong(handle,-20)&0x8000000)!=0,"no-activate native style");
                    form.Show();Pump(80);
                    Check(form.Location.X<0&&form.Location.Y<0,"window remains offscreen");
                    Check(GetForegroundWindow()==foreground,"show does not activate");
                    Check((byte)form.GetType().GetProperty("EffectiveOpacityAlpha",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form,null)==89,"35 percent native opacity");
                    object frame=Field(form,"_frame");Pump(180);
                    Check(ReferenceEquals(frame,Field(form,"_frame")),"idle frame is not repainted");
                    source.Publish();Pump(30);
                    Check(ReferenceEquals(frame,Field(form,"_frame")),"identical quota event does not repaint");
                    // Real GO countdown is the sole idle-looking one-second timer: it reflects
                    // changing data and must stop when hidden, unavailable, or already expired.
                    form.Hide();config.QuotaDisplaySource="opencodego";
                    go.Quota=new OpenCodeGoQuotaSnapshot(new OpenCodeGoQuotaWindow("",0,DateTimeOffset.UtcNow.AddHours(1)),null,null,DateTimeOffset.UtcNow,false);
                    go.IsConfigured=true;
                    form.Location=new Point(-20000,-20000);form.Show();Pump(30);
                    Check(((System.Windows.Forms.Timer)Field(form,"_displayTimer")).Enabled,"future GO reset drives countdown");
                    form.Hide();
                    Check(!((System.Windows.Forms.Timer)Field(form,"_displayTimer")).Enabled,"hidden GO stops countdown");
                    go.Quota=OpenCodeGoQuotaSnapshot.EmptyStale();
                    form.Location=new Point(-20000,-20000);form.Show();Pump(30);
                    Check(!((System.Windows.Forms.Timer)Field(form,"_displayTimer")).Enabled,"unknown GO reset has no idle timer");
                    Invoke(form,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,295,39,0));
                    Check(refreshCalls==1,"refresh hit target preserved");
                    Check(((System.Windows.Forms.Timer)Field(form,"_animationTimer")).Enabled,"visible refresh animates");
                    form.HideFromTray();
                    Check(!((System.Windows.Forms.Timer)Field(form,"_animationTimer")).Enabled,"hidden refresh stops animation timer");
                    source.Publish();Pump(30);
                    Check(!(bool)Field(form,"_refreshing"),"source completion ends refresh");
                    form.Location=new Point(-20000,-20000);form.Show();Pump(30);
                    // Force UpdateLayeredWindow to fail on this own offscreen HWND by removing
                    // its layered style. This exercises the actual failure-to-fallback branch.
                    SetWindowLong(form.Handle,-20,GetWindowLong(form.Handle,-20)&~0x80000);
                    Invoke(form,"RequestRender");Pump(30);
                    Check((bool)Field(form,"_nativeFallback"),"native upload failure enters fallback");
                    Check((GetWindowLong(form.Handle,-20)&0x80000)==0,"fallback removes layered style");
                    using(var bitmap=(Bitmap)render.Invoke(form,null))Check(bitmap.GetPixel(0,0).A==255,"fallback frame is opaque");
                    form.BeginShutdown();
                    Check(!((System.Windows.Forms.Timer)Field(form,"_displayTimer")).Enabled,"shutdown timer disabled");
                    Check(source.Subscribers==0,"source unsubscribed");
                    Delegate subscribers=Field(bridge,"PcMetricsChanged") as Delegate;
                    Check(subscribers==null,"network unsubscribed");
                }
                finally{form.Dispose();}
                source.Publish();Application.DoEvents();
                Check(source.Subscribers==0,"disposed callback safe");
                using(var policy=new QuotaHudForm(config,source,api,delegate{},bridge))
                {
                    policy.Location=new Point(-20000,-20000);
                    IntPtr handle=policy.Handle;
                    // Simulate the effective opaque policy changing, without changing the
                    // user's desktop high-contrast setting. The native notification must
                    // reconcile the HWND style immediately even while hidden.
                    policy.GetType().GetField("_nativeFallback",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(policy,true);
                    SendMessage(handle,0x001A,IntPtr.Zero,IntPtr.Zero);
                    Check((GetWindowLong(handle,-20)&0x80000)==0,"system settings notification applies changed opaque policy");
                }
                int resources=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                for(int i=0;i<30;i++)
                {
                    using(var next=new QuotaHudForm(config,source,api,delegate{},bridge))
                    {
                        next.Location=new Point(-20000,-20000);
                        IntPtr ignored=next.Handle;
                        var render=next.GetType().GetMethod("RenderFrame",BindingFlags.Instance|BindingFlags.NonPublic);
                        using(var bitmap=(Bitmap)render.Invoke(next,null))Check(bitmap.Width>0,"repeated form render");
                        Thread worker=new Thread(source.Publish);worker.Start();worker.Join();
                        next.BeginShutdown();
                    }
                    Application.DoEvents();
                }
                GC.Collect();GC.WaitForPendingFinalizers();
                int after=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                Check(after-resources<=1,"30 create/render/dispose cycles release GDI resources");
                Check(source.Subscribers==0,"queued callbacks safe after repeated dispose");
                Console.WriteLine("GDI lifecycle before="+resources+" after30="+after);
            }
            Console.WriteLine("PASS HUD lifecycle assertions="+_checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("FAIL "+ex);return 1;}
    }
    private sealed class Metrics:IPcMetricsProvider
    {public PcMetricsSnapshot Read(){return new PcMetricsSnapshot{NetworkSpeedAvailable=true,NetworkDownloadKiBPerSecond=512,NetworkUploadKiBPerSecond=2};}}
    private sealed class MutableSource:ICodexStatusSource
    {
        public event Action Changed;
        public event Action<string> StatusChanged;
        public int Subscribers{get{return (Changed==null?0:Changed.GetInvocationList().Length)+(StatusChanged==null?0:StatusChanged.GetInvocationList().Length);}}
        public QuotaSnapshot Quota{get{return new QuotaSnapshot(50,50,null,null,DateTimeOffset.UtcNow,false);}}
        public int State{get{return 1;}} public string StatusText{get{return "mock";}}
        public void Publish(){if(Changed!=null)Changed();}
        public Task StartAsync(){return Task.FromResult(0);}public Task RefreshQuotaAsync(){return Task.FromResult(0);}
        public void Stop(){}public void Dispose(){}
    }
}
