using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexToolsHost.Updates
{
    public sealed class UsbFirmwareUpdater
    {
        private readonly Func<string[],int,string> _run;
        private readonly Action<int> _delay;
        public UsbFirmwareUpdater(Func<string[],int,string> run, Action<int> delay)
        { _run=run; _delay=delay; }

        internal static string[] UsbConnection(string output)
        {
            string[] ports=Regex.Matches(output ?? "",@"\bUSB\d+\b",RegexOptions.IgnoreCase)
                .Cast<Match>().Select(m=>m.Value.ToUpperInvariant()).Distinct().ToArray();
            if(ports.Length>1) throw new IOException("检测到多个DFU设备，请只连接要更新的这一台。");
            if(ports.Length==0)return null;
            Match serial=Regex.Match(output,@"Serial number\s*:\s*([A-Za-z0-9]+)",RegexOptions.IgnoreCase);
            if(!serial.Success)throw new IOException("DFU设备未提供可识别的序列号，停止升级。");
            return new[]{"-c","port="+ports[0],"sn="+serial.Groups[1].Value};
        }

        public string Update(byte[] image, string backupRoot, Action enterDfu, Action<string> progress)
        {
            /* Validation precedes any device command or reset. Freeze caller bytes. */
            image=(byte[])image.Clone();
            FirmwareImage.Validate(image,false,true);
            string directory=Path.Combine(backupRoot,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(directory);
            string staged=Path.Combine(directory,"new-application.bin");
            File.WriteAllBytes(staged,image);
            progress("正在进入USB升级模式…");
            if(enterDfu!=null) enterDfu();
            string[] connection=null;
            for(int attempt=0;attempt<20;attempt++)
            {
                connection=UsbConnection(_run(new[]{"-l","usb"},10000));
                if(connection!=null)break;
                _delay(500);
            }
            if(connection==null)throw new IOException("未发现DFU设备。请检查DFU驱动，或按住KEY1插入USB后重试。");
            Func<string[],int,string> command=delegate(string[] args,int timeout) {
                return _run(connection.Concat(args).ToArray(),timeout);
            };
            string bootFile=Path.Combine(directory,"installed-boot.bin");
            command(new[]{"-u","0x08000000","8192",bootFile},15000);
            FirmwareImage.Validate(File.ReadAllBytes(bootFile),true,false);
            string optionsFile=Path.Combine(directory,"option-bytes.bin");
            command(new[]{"-u","0x1FFF7800","44",optionsFile},15000);
            FirmwareImage.ProtectedEndPage(File.ReadAllBytes(optionsFile));

            progress("正在备份原应用…");
            string backup=Path.Combine(directory,"previous-application.bin");
            command(new[]{"-u","0x08002000","57344",backup},30000);
            if(new FileInfo(backup).Length!=FirmwareImage.AppCapacity)
                throw new IOException("原应用备份不完整，已停止升级。");

            progress("正在写入并校验固件 "+FirmwareImage.Version(image)+"…");
            command(new[]{"-w",staged,"0x08002000","-v"},120000);
            string readback=Path.Combine(directory,"readback.bin");
            command(new[]{"-u","0x08002000",image.Length.ToString(),readback},30000);
            if(!File.ReadAllBytes(readback).SequenceEqual(image))
                throw new IOException("固件回读校验不一致。请保留USB连接并重新升级，或用KEY1进入恢复模式。");
            progress("回读校验通过，正在重启…");
            try { command(new[]{"-s","0x08000000"},15000); }
            catch(Exception ex) {
                throw new IOException("固件已写入且回读校验通过，但自动重启未确认，请重新上电。备份："+directory,ex);
            }
            return directory;
        }
    }
}
