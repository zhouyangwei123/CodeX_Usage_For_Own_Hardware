using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.UI;
class UiRunner {
 [STAThread] static int Main(string[] args) {
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  var config=AppConfig.CreateDefault();
  using(var bridge=new BridgeService(config))
  using(var form=new SettingsForm(config,bridge)) {
   form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);
   form.Show();Application.DoEvents();
   var tabs=(TabControl)typeof(SettingsForm).GetField("_tabs",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
   tabs.SelectedIndex=4;
   var sections=(TabControl)typeof(SettingsForm).GetField("_diagnosticsSections",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
   foreach(TabPage page in sections.TabPages)if(page.Text=="设备固件升级")sections.SelectedTab=page;
   Application.DoEvents();
   using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(Point.Empty,form.Size));bmp.Save(args[0]);}
   Console.WriteLine("Firmware update UI rendered "+form.Width+"x"+form.Height);
   form.Close();
  }
  return 0;
 }
}
