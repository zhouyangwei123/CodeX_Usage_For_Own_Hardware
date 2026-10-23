using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.UI;

class UiLifetimeRepro
{
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread] static int Main()
    {
        Application.EnableVisualStyles();
        // No service constructor or external source is started. Use the real
        // application exit path and its real, shared update state.
        var bridge=(BridgeService)FormatterServices.GetUninitializedObject(typeof(BridgeService));
        var tray=(TrayApp)FormatterServices.GetUninitializedObject(typeof(TrayApp));
        var status=new ToolStripMenuItem();
        typeof(TrayApp).GetField("_bridge",Fields).SetValue(tray,bridge);
        typeof(TrayApp).GetField("_statusItem",Fields).SetValue(tray,status);
        int exited=0;
        tray.ThreadExit+=delegate { exited++; };
        bool acquired=bridge.TryBeginFirmwareUpdate() && !bridge.TryBeginFirmwareUpdate();
        using(var owner=new Form {ShowInTaskbar=false,Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual})
        {
            var panel=new FirmwareUpdatePanel(bridge,owner);
            owner.Controls.Add(panel);owner.Show();Application.DoEvents();
            typeof(FirmwareUpdatePanel).GetField("_busy",Fields).SetValue(panel,true);
            owner.Close();
            bool closeBlocked=!owner.IsDisposed;
            typeof(TrayApp).GetMethod("ExitApp",Fields).Invoke(tray,null);
            tray.ExitThread();
            bool exitBlocked=exited==0 && !(bool)typeof(TrayApp).GetField("_exiting",Fields).GetValue(tray);
            bool keptAlive=!owner.IsDisposed && !panel.IsDisposed;
            Console.WriteLine("One update owner only: "+acquired);
            Console.WriteLine("Normal Close blocked: "+closeBlocked);
            Console.WriteLine("Real tray exit and ExitThread blocked before cleanup: "+exitBlocked);
            Console.WriteLine("Update window and panel still alive: "+keptAlive);
            bridge.EndFirmwareUpdate();
            Console.WriteLine("Update state released after completion: "+!bridge.IsFirmwareUpdateInProgress);
            typeof(FirmwareUpdatePanel).GetField("_busy",Fields).SetValue(panel,false);
            status.Dispose();
            return acquired && closeBlocked && exitBlocked && keptAlive && !bridge.IsFirmwareUpdateInProgress ? 0:1;
        }
    }
}
