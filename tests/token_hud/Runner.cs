using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Tests;
using CodexToolsHost.UI;
using CodexToolsHost.Usage;

internal static class TokenHudTests
{
    static int checks;
    static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
    static object Field(object o,string name){return o.GetType().GetField(name,Flags).GetValue(o);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,Flags).Invoke(o,args);}
    static void Pump(int ms){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process,int flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle,int msg,IntPtr w,IntPtr l);
    [STAThread] static int Main(string[] args)
    {
        try {Run(args[0]);Console.WriteLine("PASS "+checks+" Token HUD assertions");return 0;}
        catch(Exception ex){Console.Error.WriteLine("FAIL "+ex.Message);return 1;}
    }
    static void Run(string output)
    {
        var fold=typeof(AppConfig).GetProperty("QuotaHudChartExpanded");
        Check(fold!=null,"Missing persistent chart fold preference");
        string fixture=Path.Combine(output,"config");Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture,AppConfig.DefaultFileName),"{\"configVersion\":10,\"quotaHudStyle\":\"glass\",\"quotaHudScalePercent\":75,\"serialPort\":\"COM32\"}");
        var config=AppConfig.Load(fixture,fixture);
        Check((bool)fold.GetValue(config,null),"Existing configuration must introduce expanded chart by default");
        fold.SetValue(config,false,null);config.Save();
        var round=AppConfig.Load(fixture,fixture);
        Check(!(bool)fold.GetValue(round,null)&&round.SerialPort=="COM32","Fold roundtrip must preserve other preferences");
        var assembly=typeof(AppConfig).Assembly;
        var ensureVisible=assembly.GetType("CodexToolsHost.UI.QuotaHudPresentation").GetMethod("EnsureVisible",Flags);
        var areas=new[]{new Rectangle(0,0,1920,1080),new Rectangle(1920,0,1920,1080)};
        var clamped=(Rectangle)ensureVisible.Invoke(null,new object[]{new Rectangle(3364,-76,420,300),areas});
        Check(clamped==new Rectangle(3364,0,420,300),"Expansion at secondary monitor top must stay on that monitor");
        var stacked=new[]{new Rectangle(0,0,1920,1080),new Rectangle(0,-1080,1920,1080)};
        clamped=(Rectangle)ensureVisible.Invoke(null,new object[]{new Rectangle(1476,-180,420,300),stacked});
        Check(clamped==new Rectangle(1476,0,420,300),"Expansion keeps bottom-right anchor screen with vertically stacked monitors");
        var rendererType=assembly.GetType("CodexToolsHost.UI.QuotaHudRenderer");
        var sizeMethod=rendererType.GetMethod("ActivityWindowSize",Flags);
        var render=rendererType.GetMethod("RenderActivityFrame",Flags);
        var toggle=rendererType.GetMethod("ActivityToggleBounds",Flags);
        Check(sizeMethod!=null&&render!=null&&toggle!=null,"Missing activity renderer and geometry contract");
        var activityType=assembly.GetType("CodexToolsHost.Usage.UsageActivity");
        Check(activityType!=null,"Missing numeric activity projection");
        string home=Path.Combine(output,"logs");Directory.CreateDirectory(Path.Combine(home,"sessions"));
        var now=DateTimeOffset.UtcNow;
        string content="{\"type\":\"session_meta\",\"timestamp\":\""+now.AddHours(-1).ToString("o")+"\",\"payload\":{\"id\":\"token-hud-demo\"}}\n";
        long input=0,outputTokens=0;
        for(int i=0;i<180;i++){
            input+=1000+(i%7)*300;outputTokens+=300+(i%5)*100;
            content+="{\"type\":\"event_msg\",\"timestamp\":\""+now.AddSeconds(-3590+i*20).ToString("o")+"\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":"+input+",\"output_tokens\":"+outputTokens+",\"cached_input_tokens\":0,\"reasoning_output_tokens\":0}}}}\n";
        }
        File.WriteAllText(Path.Combine(home,"sessions","demo.jsonl"),content);
        using(var usage=new LocalUsageService(home))
        using(var renderer=(IDisposable)Activator.CreateInstance(rendererType,true))
        {
            usage.RefreshAsync().Wait();
            object activity=activityType.GetMethod("Create").Invoke(null,new object[]{usage.Report,now});
            var quota=new QuotaSnapshot(82,64,null,null,now,false,300,10080,"");
            foreach(string style in new[]{"glass","minimal","classic"})
            foreach(int scale in new[]{60,75,90,100})
            foreach(bool expanded in new[]{true,false})
            {
                var canonical=(Size)sizeMethod.Invoke(renderer,new object[]{expanded});
                var size=new Size((int)Math.Round(canonical.Width*scale/100d),(int)Math.Round(canonical.Height*scale/100d));
                using(var bitmap=(Bitmap)render.Invoke(renderer,new object[]{size,quota,style,0f,false,"GO 5h余100% 7d余60%\r\n月余42% 5h→02:12:34","↑128K ↓1.2M",false,true,true,false,activity,expanded}))
                {
                    bitmap.Save(Path.Combine(output,style+"-"+scale+"-"+(expanded?"expanded":"folded")+".png"),ImageFormat.Png);
                    Check(bitmap.Width==size.Width&&bitmap.Height==size.Height,"Rendering dimensions");
                    Check(bitmap.GetPixel(0,0).A<255&&bitmap.GetPixel(size.Width/2,size.Height/2).A>0,"Alpha edge and visible panel");
                }
            }
            var source=new FakeCodexSource();var api=new FakeDeepSeekSource();var go=new FakeOpenCodeGoSource();
            ConstructorInfo bridgeCtor=null;
            foreach(var c in typeof(BridgeService).GetConstructors(Flags))if(c.GetParameters().Length==8)bridgeCtor=c;
            using(var bridge=(BridgeService)bridgeCtor.Invoke(new object[]{config,new MockDeviceLink(),source,null,api,new PcMonitorService(new Metrics(),500),new FakeChatGptRestartService(),go}))
            {
                var constructor=typeof(QuotaHudForm).GetConstructor(new[]{typeof(AppConfig),typeof(CodexToolsHost.Quota.ICodexStatusSource),typeof(CodexToolsHost.Quota.IDeepSeekSource),typeof(Action),typeof(BridgeService),typeof(LocalUsageService)});
                Check(constructor!=null,"HUD must consume tray-owned usage service");
                int refresh=0;
                using(var form=(QuotaHudForm)constructor.Invoke(new object[]{config,source,api,new Action(delegate{refresh++;}),bridge,usage}))
                {
                    Check(!form.IsHandleCreated,"Construction must remain handle-free");
                    Check(!((System.Windows.Forms.Timer)Field(form,"_activityTimer")).Enabled,"Hidden activity timer disabled");
                    form.Location=new Point(-20000,-20000);form.Show();Pump(30);
                    Check(((System.Windows.Forms.Timer)Field(form,"_activityTimer")).Enabled,"Visible activity timer enabled");
                    form.Hide();
                    Rectangle area=Screen.PrimaryScreen.WorkingArea;
                    form.Location=new Point(area.Right-form.Width-24,area.Bottom-form.Height-24);
                    Size foldedSize=form.ClientSize;Point anchor=new Point(form.Right,form.Bottom);
                    Check((bool)Call(form,"ToggleChart"),"Toggle succeeds");
                    Check((bool)fold.GetValue(config,null)&&form.Height>foldedSize.Height,"Expansion increases only chart area");
                    Check(form.Right==anchor.X&&form.Bottom==anchor.Y,"Folding preserves bottom-right anchor");
                    Check(AppConfig.Load(fixture,fixture).QuotaHudChartExpandedForTest(fold),"Toggle is persisted");
                    form.Location=new Point(-20000,-20000);
                    using(var locked=new FileStream(config.ConfigPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        Size before=form.Size;
                        Check(!(bool)Call(form,"ToggleChart"),"Failed save must report failure");
                        Check((bool)fold.GetValue(config,null)&&form.Size==before,"Failed save rolls back live preference and size");
                    }
                    foreach(int scale in new[]{60,75,90,100}){
                        config.QuotaHudScalePercent=scale;form.ApplyDisplaySettings();form.Location=new Point(-20000,-20000);
                        Rectangle hit=(Rectangle)toggle.Invoke(renderer,null);
                        Size canonical=(Size)sizeMethod.Invoke(renderer,new object[]{true});
                        Point client=new Point((hit.Left+hit.Width/2)*form.ClientSize.Width/canonical.Width,(hit.Top+hit.Height/2)*form.ClientSize.Height/canonical.Height);
                        Point screen=form.PointToScreen(client);long packed=((long)(ushort)screen.Y<<16)|(ushort)screen.X;
                        Check(SendMessage(form.Handle,0x84,IntPtr.Zero,new IntPtr(packed))==new IntPtr(1),"Scaled toggle is a client hit, not drag");
                        Call(form,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,client.X,client.Y,0));
                        Check(!(bool)fold.GetValue(config,null)&&refresh==0,"Click folds without quota refresh");
                        Call(form,"ToggleChart");
                    }
                    form.Location=new Point(-20000,-20000);form.Hide();
                    Check(!((System.Windows.Forms.Timer)Field(form,"_activityTimer")).Enabled,"Hidden timer stops");
                    usage.RefreshAsync().Wait();Pump(20);
                    Check(usage.Report.FilesDiscovered>0,"Hiding leaves service available");
                    int beforeGdi=GetGuiResources(Process.GetCurrentProcess().Handle,0);
                    for(int i=0;i<150;i++)using(var b=(Bitmap)Call(form,"RenderFrame")){}
                    Check(GetGuiResources(Process.GetCurrentProcess().Handle,0)<=beforeGdi+2,"Repeated rendering does not grow GDI handles");
                    form.BeginShutdown();
                    Check(Field(usage,"Changed")==null,"Shutdown unsubscribes usage callback");
                }
            }
        }
    }
    static bool QuotaHudChartExpandedForTest(this AppConfig config,PropertyInfo property){return (bool)property.GetValue(config,null);}
    sealed class Metrics:IPcMetricsProvider {public PcMetricsSnapshot Read(){return PcMetricsSnapshot.CreateUnavailable("fixture",DateTimeOffset.UtcNow);}public void Dispose(){}}
}
