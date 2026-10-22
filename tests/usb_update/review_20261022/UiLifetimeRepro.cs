using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CodexToolsHost.UI;

// Only a stub dependency. No BridgeService, serial port, config or process is started.
namespace CodexToolsHost.Core
{
    public class BridgeService
    {
        public bool IsDeviceConnected { get { return false; } }
        public bool SupportsUsbUpdate { get { return false; } }
        public void EnterFirmwareUpdate() { throw new InvalidOperationException(); }
        public void PauseDeviceForUpdate() { }
        public void ResumeDeviceAfterUpdate() { }
    }
}
class UiLifetimeRepro
{
    [STAThread] static int Main()
    {
        Application.EnableVisualStyles();
        using (var owner = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) })
        {
            var panel = new FirmwareUpdatePanel(new CodexToolsHost.Core.BridgeService(), owner);
            owner.Controls.Add(panel);
            owner.Show();
            Application.DoEvents();
            typeof(FirmwareUpdatePanel).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, true);
            owner.Close();
            bool closeWasBlocked = !owner.IsDisposed;
            Console.WriteLine("Normal Close while update busy was blocked: " + closeWasBlocked);
            // TrayApp.DisposeServices takes this path before ExitThread.
            owner.Dispose();
            Console.WriteLine("Tray-style Dispose while update busy disposed owner: " + owner.IsDisposed);
            Console.WriteLine("Tray-style Dispose while update busy disposed panel: " + panel.IsDisposed);
            return closeWasBlocked && panel.IsDisposed ? 0 : 1;
        }
    }
}
