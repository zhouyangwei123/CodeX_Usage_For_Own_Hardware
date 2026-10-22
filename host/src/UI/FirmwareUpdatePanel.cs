using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Updates;

namespace CodexToolsHost.UI
{
    public sealed class FirmwareUpdatePanel : UserControl
    {
        private readonly BridgeService _bridge;
        private readonly Form _owner;
        private readonly TextBox _firmware = new TextBox { Dock=DockStyle.Fill, ReadOnly=true };
        private readonly TextBox _programmer = new TextBox { Dock=DockStyle.Fill, ReadOnly=true };
        private readonly Label _status = new Label { Dock=DockStyle.Fill, AutoSize=true };
        private readonly Button _update = new Button { Text="开始 USB 升级", AutoSize=true };
        private bool _busy;
        public FirmwareUpdatePanel(BridgeService bridge, Form owner)
        {
            _bridge=bridge; _owner=owner; Dock=DockStyle.Fill; Padding=new Padding(18);
            AutoScroll=true;
            _status.MaximumSize=new Size(640,0);
            var layout=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));
            var intro=new Label { AutoSize=true, MaximumSize=new Size(680,0), Margin=new Padding(3,3,3,18),
                Text="首次使用需用 ST-Link 安装完整镜像。之后可在此通过 USB 更新应用固件。\n"+
                     "设备无法正常连接时，按住 KEY1（PC3）上电进入恢复模式，再执行升级。"+
                     "更新前会备份原应用，写入后回读校验。"};
            layout.Controls.Add(intro,0,0); layout.SetColumnSpan(intro,2);
            layout.Controls.Add(new Label { Text="应用固件（Application-*.bin）", AutoSize=true },0,1);
            layout.Controls.Add(_firmware,0,2);
            var selectFirmware=new Button { Text="选择固件", Dock=DockStyle.Fill };
            selectFirmware.Click+=delegate { Choose(_firmware,"应用固件|Application-*.bin|BIN文件|*.bin"); };
            layout.Controls.Add(selectFirmware,1,2);
            layout.Controls.Add(new Label { Text="STM32CubeProgrammer 命令行工具",AutoSize=true,Margin=new Padding(3,14,3,3) },0,3);
            layout.Controls.Add(_programmer,0,4);
            var selectTool=new Button { Text="选择工具",Dock=DockStyle.Fill };
            selectTool.Click+=delegate { Choose(_programmer,"STM32_Programmer_CLI.exe|STM32_Programmer_CLI.exe"); };
            layout.Controls.Add(selectTool,1,4);
            _update.Margin=new Padding(3,20,3,12);
            _update.Click+=StartUpdate;
            layout.Controls.Add(_update,0,5);
            _status.Text="请选择固件与烧录工具；升级期间保持 USB 连接。";
            layout.Controls.Add(_status,0,6); layout.SetColumnSpan(_status,2);
            Controls.Add(layout);
            _owner.FormClosing+=PreventClose;
        }
        private void Choose(TextBox target,string filter)
        {
            if(_busy)return;
            using(var dialog=new OpenFileDialog { Filter=filter,CheckFileExists=true })
                if(dialog.ShowDialog(this)==DialogResult.OK)target.Text=dialog.FileName;
        }
        private void PreventClose(object sender,FormClosingEventArgs e)
        { if(_busy)e.Cancel=true; }
        private void ShowProgress(string text)
        {
            if(IsDisposed)return;
            if(InvokeRequired) { BeginInvoke(new Action<string>(ShowProgress),text); return; }
            _status.Text=text;
        }
        private async void StartUpdate(object sender,EventArgs e)
        {
            if(_busy)return;
            try
            {
                byte[] image=File.ReadAllBytes(_firmware.Text);
                FirmwareImage.Validate(image,false,true);
                var programmer=new CubeProgrammer(_programmer.Text);
                bool connected=_bridge.IsDeviceConnected;
                if(connected && !_bridge.SupportsUsbUpdate)
                    throw new InvalidOperationException("当前固件没有USB升级入口，请先用ST-Link安装首次完整镜像。");
                if(MessageBox.Show(this,"将升级至固件 "+FirmwareImage.Version(image)+
                    "。请只连接这一台待升级设备，并保持USB供电。是否开始？",
                    "USB 固件升级",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;
                _busy=true; _update.Enabled=false;
                string backupRoot=Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),"CodexToolsHost","firmware-backups");
                string backup=await Task.Run(delegate {
                    var updater=new UsbFirmwareUpdater(programmer.Run,Thread.Sleep);
                    return updater.Update(image,backupRoot,delegate {
                        if(connected)_bridge.EnterFirmwareUpdate();
                        else _bridge.PauseDeviceForUpdate();
                    },ShowProgress);
                });
                ShowProgress("写入及回读校验完成，已请求设备重启。备份："+backup);
            }
            catch(Exception ex) { ShowProgress(ex.Message); }
            finally {
                if(_busy)_bridge.ResumeDeviceAfterUpdate();
                _busy=false; _update.Enabled=true;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing)_owner.FormClosing-=PreventClose;
            base.Dispose(disposing);
        }
    }
}
