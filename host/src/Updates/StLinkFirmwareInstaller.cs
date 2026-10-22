using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexToolsHost.Updates
{
    public sealed class StLinkFirmwareInstaller
    {
        private readonly Func<string[],int,string> _run;
        public StLinkFirmwareInstaller(Func<string[],int,string> run) { _run=run; }
        public StLinkFirmwareInstaller(CubeProgrammer programmer) : this(programmer.Run) { }

        public string Install(byte[] boot, byte[] app, string backupRoot)
        {
            boot=(byte[])boot.Clone(); app=(byte[])app.Clone();
            FirmwareImage.Validate(boot,true,true);
            FirmwareImage.Validate(app,false,true);
            string listing=_run(new[]{"-l","st-link-only"},15000);
            string[] serials=Regex.Matches(listing,@"ST-LINK SN\s*:\s*([A-Za-z0-9]+)",RegexOptions.IgnoreCase)
                .Cast<Match>().Select(m=>m.Groups[1].Value).Distinct().ToArray();
            if(serials.Length!=1)throw new IOException("首次安装需要且只能连接一个ST-Link。");
            string directory=Path.Combine(backupRoot,"first-install-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+
                "-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(directory);
            string serial="sn="+serials[0];
            Func<string[],string> command=delegate(string[] args) {
                return _run(new[]{"-c","port=SWD",serial}.Concat(args).ToArray(),120000);
            };
            Func<string,int,byte[]> read=delegate(string address,int count) {
                string path=Path.Combine(directory,"read-"+address+"-"+count+".bin");
                command(new[]{"-u",address,count.ToString(),path});
                byte[] data=File.ReadAllBytes(path);
                if(data.Length!=count)throw new IOException("设备回读长度不匹配，停止安装。");
                return data;
            };
            if((FirmwareImage.Read32(read("0xE0042000",4),0)&0xFFF)!=0x469 ||
                BitConverter.ToUInt16(read("0x1FFF75E0",2),0)!=128)
                throw new IOException("此安装包仅适用于STM32G474的128KiB型号。");
            byte[] options=read("0x1FFF7800",44);
            uint optr=FirmwareImage.Read32(options,0), wrp=FirmwareImage.Read32(options,24);
            if((optr&255)!=0xAA || (optr&(1u<<20))!=0 ||
               (FirmwareImage.Read32(options,40)&0x10000)!=0)
                throw new IOException("读保护或Bank启动选项不匹配；没有自动修改这些选项。");
            if((wrp&127)<=((wrp>>16)&127))
                throw new IOException("启动区已设置写保护。已安装本引导程序时请使用USB应用升级，不要重复完整安装。");
            /* Preserve the original application in bank 1. No config pages or
               bank 2 are erased; their mapping varies with device geometry. */
            byte[] original=read("0x08000000",65536);
            File.WriteAllBytes(Path.Combine(directory,"original-bank1.bin"),original);
            File.WriteAllText(Path.Combine(directory,"option-bytes.txt"),command(new[]{"-ob","displ"}));
            string bootPath=Path.Combine(directory,"new-boot.bin");
            string appPath=Path.Combine(directory,"new-app.bin");
            File.WriteAllBytes(bootPath,boot); File.WriteAllBytes(appPath,app);
            command(new[]{"-w",bootPath,"0x08000000","-v"});
            command(new[]{"-w",appPath,"0x08002000","-v"});
            if(!read("0x08000000",boot.Length).SequenceEqual(boot) ||
               !read("0x08002000",app.Length).SequenceEqual(app))
                throw new IOException("安装镜像回读校验失败；未启用启动区写保护。");
            int end=(optr&(1u<<22))!=0 ? 3 : 1;
            command(new[]{"-ob","WRP1A_STRT=0","WRP1A_END="+end});
            FirmwareImage.ProtectedEndPage(read("0x1FFF7800",44));
            command(new[]{"-rst"});
            return directory;
        }
    }
}
