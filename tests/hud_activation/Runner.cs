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
                Check(SendMessage(hud.Handle,0x21,IntPtr.Zero,IntPtr.Zero)==new IntPtr(1),prefix+"unpinned clicks request normal activation");
                blocker.Activate();Pump(10);hud.ShowFromTray();Pump(10);
                Check(GetActiveWindow()==hud.Handle,prefix+"explicit tray recovery activates covered HUD");
                Check(!hud.TopMost&&!config.QuotaHudTopMost,prefix+"recovery never turns always-on-top back on");
                hud.Location=new Point(100,180);Call(hud,"SavePosition");int? savedX=config.QuotaHudX,savedY=config.QuotaHudY;
                hud.WindowState=FormWindowState.Minimized;Pump(25);
                Check(IsIconic(hud.Handle),prefix+"native minimized state reproduced");
                Call(hud,"SavePosition");
                Check(config.QuotaHudX==savedX&&config.QuotaHudY==savedY,prefix+"iconic coordinates never overwrite saved position");
                if(activity)Check(!((System.Windows.Forms.Timer)Field(hud,"_activityTimer")).Enabled,prefix+"minimized view stops activity timer");
                blocker.Activate();
                var passiveMinimized=typeof(QuotaHudForm).GetMethod("ShowPassive");
                if(passiveMinimized!=null){passiveMinimized.Invoke(hud,null);Pump(10);Check(IsIconic(hud.Handle)&&GetActiveWindow()==blocker.Handle,prefix+"passive synchronization preserves minimized state and focus");}
                hud.ShowFromTray();Pump(20);
                Check(hud.WindowState==FormWindowState.Normal&&!IsIconic(hud.Handle)&&hud.Visible,prefix+"tray recovery restores minimized HUD");
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
                config.QuotaHudTopMost=true;hud.ApplyDisplaySettings();Pump(10);
                Check((GetWindowLong(hud.Handle,-20)&0x08000000)!=0,prefix+"repin restores no-activate style");
                hud.WindowState=FormWindowState.Minimized;blocker.Activate();Pump(10);hud.ShowFromTray();Pump(10);
                Check(!IsIconic(hud.Handle)&&GetActiveWindow()==blocker.Handle,prefix+"pinned recovery restores without activating");
                Check(hud.TopMost&&(GetWindowLong(hud.Handle,-20)&8)!=0,prefix+"pinned recovery retains native topmost band");
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
            Check(restore!=null,"tray exposes a deterministic restore command");
            if(restore!=null){restore.Invoke(tray,null);Check(hud.Visible&&trayConfig.QuotaHudVisible,"restore command shows hidden HUD");}
            FieldInfo item=typeof(TrayApp).GetField("_restoreQuotaHudItem",Flags);
            Check(item!=null,"restore command has a visible tray menu entry");
            if(item!=null){hud.WindowState=FormWindowState.Minimized;Pump(10);((ToolStripMenuItem)item.GetValue(tray)).PerformClick();Pump(10);Check(hud.WindowState==FormWindowState.Normal,"restore menu restores rather than hides minimized HUD");}
            MethodInfo click=typeof(TrayApp).GetMethod("OnTrayMouseClick",Flags);
            Check(click!=null,"tray left click has restore handler");
            if(click!=null){hud.HideFromTray();click.Invoke(tray,new object[]{null,new MouseEventArgs(MouseButtons.Left,1,0,0,0)});Pump(10);Check(hud.Visible&&GetActiveWindow()==hud.Handle,"tray left click raises HUD");}
            hud.ShowFromTray();hud.WindowState=FormWindowState.Minimized;Pump(10);Call(tray,"ToggleQuotaHud");Pump(10);
            Check(hud.Visible&&hud.WindowState==FormWindowState.Normal&&trayConfig.QuotaHudVisible,"visibility toggle recovers minimized HUD in one click");
        }
    }
    sealed class Metrics:IPcMetricsProvider{public PcMetricsSnapshot Read(){return PcMetricsSnapshot.CreateUnavailable("fixture",DateTimeOffset.UtcNow);}}
}
