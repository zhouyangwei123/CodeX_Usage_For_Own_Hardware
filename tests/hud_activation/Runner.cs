using System;
using System.Diagnostics;
using System.Drawing;
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

internal static class HudActivationTests
{
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    [StructLayout(LayoutKind.Sequential)] struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo{public NativePoint Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,int flags,uint access,IntPtr security);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr handle,int index);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle,uint command);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wparam,IntPtr lparam);
    static void Check(bool ok,string name){checks++;Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;}
    static object Field(object o,string n){return o.GetType().GetField(n,Flags).GetValue(o);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,Flags).Invoke(o,args);}
    static void Pump(int ms){var w=Stopwatch.StartNew();while(w.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(3);}}
    static BridgeService Bridge(AppConfig c){
        foreach(var ctor in typeof(BridgeService).GetConstructors(Flags))if(ctor.GetParameters().Length==8)
            return (BridgeService)ctor.Invoke(new object[]{c,new MockDeviceLink(),new FakeCodexSource(),null,new FakeDeepSeekSource(),
                new PcMonitorService(new Metrics(),500),new FakeChatGptRestartService(),new FakeOpenCodeGoSource()});
        throw new Exception("Missing mock bridge constructor");
    }
    [STAThread] static int Main(string[] args)
    {
        // All activation and native minimization occurs on a private desktop. It is
        // never switched into view and cannot steal focus from the user's apps.
        IntPtr desktop=CreateDesktop("CodeXHudTests-"+Guid.NewGuid().ToString("N"),IntPtr.Zero,IntPtr.Zero,0,0x01ff,IntPtr.Zero);
        if(desktop==IntPtr.Zero){Console.Error.WriteLine("Private test desktop unavailable: "+Marshal.GetLastWin32Error());return 2;}
        Exception error=null;
        var thread=new Thread(delegate(){try{if(!SetThreadDesktop(desktop))throw new Exception("SetThreadDesktop failed");Run(args[0]);}catch(Exception ex){error=ex;}});
        thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        var limit=Stopwatch.StartNew();
        // Pump the main STA for framework cleanup marshaling; no form is shown here.
        while(thread.IsAlive&&limit.Elapsed.TotalSeconds<25){Application.DoEvents();thread.Join(10);}
        if(thread.IsAlive){Console.Error.WriteLine("Harness cleanup timeout");Environment.Exit(2);}
        CloseDesktop(desktop);
        if(error!=null){Console.Error.WriteLine("Harness error: "+error);return 2;}
        Console.WriteLine("RESULT "+checks+" checks, "+failures+" failures; private desktop only");return failures==0?0:1;
    }
    static void Run(string root)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string home=Path.Combine(root,"codex-home");Directory.CreateDirectory(Path.Combine(home,"sessions"));
        Environment.SetEnvironmentVariable("CODEX_HOME",home);
        foreach(bool activity in new[]{false,true})
        {
            string fixture=Path.Combine(root,activity?"activity":"legacy");Directory.CreateDirectory(fixture);
            var config=AppConfig.Load(fixture,fixture);config.QuotaHudTopMost=true;config.QuotaHudVisible=true;config.Mijia.AutoRefresh=false;
            config.QuotaHudScalePercent=100;config.QuotaHudX=100;config.QuotaHudY=180;config.Save();
            using(var bridge=Bridge(config))
            using(var usage=new LocalUsageService(home))
            using(var blocker=new Form{Text="Activation fixture",StartPosition=FormStartPosition.Manual,Location=new Point(90,100),Size=new Size(500,400),ShowInTaskbar=false})
            using(var hud=new QuotaHudForm(config,bridge.Codex,bridge.DeepSeek,delegate{},bridge,activity?usage:null))
            {
                blocker.Show();blocker.Activate();hud.Show();Pump(20);
                string prefix=activity?"activity ":"legacy ";
                Check((GetWindowLong(hud.Handle,-20)&0x08000000)!=0,prefix+"pinned HUD is nonactivating");
                Check(SendMessage(hud.Handle,0x21,IntPtr.Zero,IntPtr.Zero)==new IntPtr(3),prefix+"pinned clicks preserve focus");
                config.QuotaHudTopMost=false;hud.ApplyDisplaySettings();Pump(10);
                Check((GetWindowLong(hud.Handle,-20)&0x08000000)==0,prefix+"unpin removes no-activate style on existing HWND");
                Check(((System.Windows.Forms.Timer)Field(hud,"_displayTimer")).Enabled,prefix+"desktop floor upkeep is independent of API balance provider");
                Check(SendMessage(hud.Handle,0x21,IntPtr.Zero,IntPtr.Zero)==new IntPtr(1),prefix+"unpinned clicks request normal activation");
                blocker.Activate();Pump(10);hud.ShowFromTray();Pump(10);
                Check(GetActiveWindow()==hud.Handle,prefix+"explicit visibility action activates unpinned HUD");
                Check(!hud.TopMost&&!config.QuotaHudTopMost,prefix+"show never turns always-on-top back on");
                hud.Location=new Point(100,180);Call(hud,"SavePosition");int? savedX=config.QuotaHudX,savedY=config.QuotaHudY;
                hud.WindowState=FormWindowState.Minimized;Pump(25);
                Check(!IsIconic(hud.Handle)&&hud.WindowState==FormWindowState.Normal,prefix+"managed minimization cannot leave HUD iconic");
                Call(hud,"SavePosition");
                Check(config.QuotaHudX==savedX&&config.QuotaHudY==savedY,prefix+"iconic coordinates never overwrite saved position");
                if(activity)Check(((System.Windows.Forms.Timer)Field(hud,"_activityTimer")).Enabled,prefix+"minimize attempt leaves activity view running");
                blocker.Activate();
                SendMessage(hud.Handle,0x112,new IntPtr(0xF020),IntPtr.Zero);Pump(10);
                Check(!IsIconic(hud.Handle)&&GetActiveWindow()==blocker.Handle,prefix+"system minimize is rejected without focus change");
                ShowWindow(hud.Handle,7);Pump(10);
                Check(!IsIconic(hud.Handle)&&GetActiveWindow()==blocker.Handle,prefix+"native nonactivating minimize returns to normal without focus change");
                var passiveMinimized=typeof(QuotaHudForm).GetMethod("ShowPassive");
                if(passiveMinimized!=null){passiveMinimized.Invoke(hud,null);Pump(10);Check(!IsIconic(hud.Handle)&&GetActiveWindow()==blocker.Handle,prefix+"passive synchronization preserves normal state and focus");}
                hud.ShowFromTray();Pump(20);
                Check(hud.WindowState==FormWindowState.Normal&&!IsIconic(hud.Handle)&&hud.Visible,prefix+"HUD remains visible in normal state");
                Check(hud.ClientSize==(activity?new Size(420,300):new Size(320,138)),prefix+"restored renderer retains full dimensions");
                if(activity)Check(((System.Windows.Forms.Timer)Field(hud,"_activityTimer")).Enabled,prefix+"restored view restarts activity timer");
                hud.HideFromTray();blocker.Activate();Pump(10);hud.ShowFromTray();Pump(10);
                Check(hud.Visible&&GetActiveWindow()==hud.Handle,prefix+"hidden HUD can be explicitly recovered");
                MethodInfo passive=typeof(QuotaHudForm).GetMethod("ShowPassive");
                Check(passive!=null,prefix+"passive display has separate entry");
                if(passive!=null){
                    hud.Hide();blocker.Activate();Pump(10);
                    passive.Invoke(hud,null);Pump(10);Check(GetActiveWindow()==blocker.Handle,prefix+"passive startup does not activate unpinned HUD");
                    hud.Hide();blocker.Activate();config.QuotaHudScalePercent=75;passive.Invoke(hud,null);Pump(10);
                    Check(GetActiveWindow()==blocker.Handle,prefix+"passive size change does not activate unpinned HUD");
                    config.QuotaHudScalePercent=100;hud.ApplyDisplaySettings();
                }
                IntPtr limits=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MinMaxInfo)));
                try{
                    Marshal.StructureToPtr(new MinMaxInfo(),limits,false);SendMessage(hud.Handle,0x24,IntPtr.Zero,limits);
                    var info=(MinMaxInfo)Marshal.PtrToStructure(limits,typeof(MinMaxInfo));
                    Check(info.MinTrack.X==hud.Width&&info.MinTrack.Y==hud.Height&&info.MaxTrack.X==hud.Width&&info.MaxTrack.Y==hud.Height,prefix+"native tracking remains fixed to configured size");
                }finally{Marshal.FreeHGlobal(limits);}
                blocker.Activate();Call(hud,"RequestRender");Pump(10);
                Check(GetActiveWindow()==blocker.Handle,prefix+"background repaint does not steal focus");
                TestDesktopFloor(hud,blocker,prefix);
                hud.Location=new Point(100,180);Call(hud,"SavePosition");
                hud.WindowState=FormWindowState.Minimized;
                hud.ApplyDisplaySettings();Pump(30);
                Check(hud.Location==new Point(100,180)&&hud.ClientSize==(activity?new Size(420,300):new Size(320,138)),prefix+"settings during pending normalization preserve full size and location");
                config.QuotaHudTopMost=true;hud.ApplyDisplaySettings();Pump(10);
                Check((GetWindowLong(hud.Handle,-20)&0x08000000)!=0,prefix+"repin restores no-activate style");
                hud.WindowState=FormWindowState.Minimized;blocker.Activate();Pump(10);hud.ShowFromTray();Pump(10);
                Check(!IsIconic(hud.Handle)&&GetActiveWindow()==blocker.Handle,prefix+"pinned recovery restores without activating");
                Check(hud.TopMost&&(GetWindowLong(hud.Handle,-20)&8)!=0,prefix+"pinned recovery retains native topmost band");
                hud.WindowState=FormWindowState.Minimized;hud.HideFromTray();Pump(30);
                Check(!hud.Visible,prefix+"queued normalization respects a subsequent intentional hide");
                Check(!((System.Windows.Forms.Timer)Field(hud,"_displayTimer")).Enabled,prefix+"hidden HUD stops periodic maintenance");
                hud.BeginShutdown();
            }
        }
        string trayFixture=Path.Combine(root,"tray");Directory.CreateDirectory(trayFixture);
        var trayConfig=AppConfig.Load(trayFixture,trayFixture);trayConfig.QuotaHudVisible=false;trayConfig.QuotaHudTopMost=false;trayConfig.Mijia.AutoRefresh=false;trayConfig.UpdateChecksEnabled=false;
        using(var bridge=Bridge(trayConfig))
        using(var tray=new TrayApp(trayConfig,bridge))
        {
            var hud=(QuotaHudForm)Field(tray,"_quotaHud");
            MethodInfo restore=typeof(TrayApp).GetMethod("RestoreQuotaHud",Flags);
            Check(restore==null,"no separate restore command");
            FieldInfo item=typeof(TrayApp).GetField("_restoreQuotaHudItem",Flags);
            Check(item==null,"no added restore menu entry");
            MethodInfo click=typeof(TrayApp).GetMethod("OnTrayMouseClick",Flags);
            Check(click==null,"no added tray single-click behavior");
            Call(tray,"ToggleQuotaHud");Pump(10);
            Check(hud.Visible&&trayConfig.QuotaHudVisible,"existing visibility toggle shows HUD");
            Call(tray,"ToggleQuotaHud");Pump(1100);
            Check(!hud.Visible&&!trayConfig.QuotaHudVisible,"intentional hide stays hidden after maintenance tick");
        }
    }
    static bool Above(IntPtr a,IntPtr b){for(IntPtr h=GetWindow(a,2);h!=IntPtr.Zero;h=GetWindow(h,2))if(h==b)return true;return false;}
    static void TestDesktopFloor(QuotaHudForm hud,Form blocker,string prefix)
    {
        Type layer=typeof(QuotaHudForm).Assembly.GetType("CodexToolsHost.UI.HudDesktopLayer");
        Check(layer!=null,prefix+"desktop floor policy exists");if(layer==null)return;
        MethodInfo raise=layer.GetMethod("RaiseAbove",Flags);
        using(var desktop=new Form{Text="Desktop surface fixture",ShowInTaskbar=false})
        {
            desktop.Show();blocker.Activate();
            SetWindowPos(desktop.Handle,blocker.Handle,0,0,0,0,0x13);
            SetWindowPos(hud.Handle,desktop.Handle,0,0,0,0,0x13);
            Check(Above(desktop.Handle,hud.Handle),prefix+"desktop coverage reproduced using real HWND order");
            raise.Invoke(null,new object[]{hud.Handle,desktop.Handle});
            Check(Above(hud.Handle,desktop.Handle),prefix+"HUD moves above desktop floor");
            Check(Above(blocker.Handle,hud.Handle),prefix+"ordinary app above desktop stays above HUD");
            Check(GetActiveWindow()==blocker.Handle&&(GetWindowLong(hud.Handle,-20)&8)==0,prefix+"desktop correction neither activates nor pins HUD");
            SetWindowPos(hud.Handle,IntPtr.Zero,0,0,0,0,0x13);
            raise.Invoke(null,new object[]{hud.Handle,desktop.Handle});
            Check(Above(hud.Handle,blocker.Handle),prefix+"valid HUD order is left alone");
            hud.Hide();raise.Invoke(null,new object[]{hud.Handle,desktop.Handle});
            Check(!hud.Visible,prefix+"desktop correction cannot undo intentional hide");hud.ShowPassive();
            desktop.TopMost=true;blocker.TopMost=true;
            SetWindowPos(desktop.Handle,blocker.Handle,0,0,0,0,0x13);
            raise.Invoke(null,new object[]{hud.Handle,desktop.Handle});
            Check((GetWindowLong(hud.Handle,-20)&8)==0,prefix+"unexpected topmost floor never promotes unpinned HUD");
            desktop.TopMost=false;blocker.TopMost=false;
            blocker.TopMost=true;
            SetWindowPos(desktop.Handle,IntPtr.Zero,0,0,0,0,0x13);
            SetWindowPos(hud.Handle,desktop.Handle,0,0,0,0,0x13);
            raise.Invoke(null,new object[]{hud.Handle,desktop.Handle});
            Check(Above(blocker.Handle,hud.Handle)&&Above(hud.Handle,desktop.Handle)&&(GetWindowLong(hud.Handle,-20)&8)==0,prefix+"floor next to topmost band keeps HUD non-topmost");
            blocker.TopMost=false;
        }
    }
    sealed class Metrics:IPcMetricsProvider{public PcMetricsSnapshot Read(){return PcMetricsSnapshot.CreateUnavailable("fixture",DateTimeOffset.UtcNow);}}
}
