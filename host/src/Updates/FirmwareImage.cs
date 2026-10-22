using System;
using System.IO;

namespace CodexToolsHost.Updates
{
    public static class FirmwareImage
    {
        public const uint BootBase = 0x08000000;
        public const uint AppBase = 0x08002000;
        public const int BootSize = 8192;
        public const int AppCapacity = 57344;
        public const int Header = 512;
        public const uint Target = 0x04690080;

        public static uint Read32(byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset+1]<<8 |
                data[offset+2]<<16 | data[offset+3]<<24);
        }
        public static uint Crc(byte[] data, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i=0; i<length; i++)
            {
                crc ^= (i>=Header+16 && i<Header+20) ? (byte)0 : data[i];
                for (int bit=0; bit<8; bit++)
                    crc = (crc>>1) ^ ((crc&1)!=0 ? 0xEDB88320u : 0u);
            }
            return ~crc;
        }
        public static void Validate(byte[] data, bool boot, bool exactLength)
        {
            if (data == null || data.Length < 1024)
                throw new InvalidDataException("固件文件过短。");
            uint address = boot ? BootBase : AppBase;
            uint magic = boot ? 0x42544D4Eu : 0x41544D4Eu;
            int capacity = boot ? BootSize : AppCapacity;
            uint length = Read32(data,Header+12);
            if (Read32(data,Header)!=magic || Read32(data,Header+4)!=1 ||
                Read32(data,Header+8)!=address || Read32(data,Header+24)!=Target ||
                Read32(data,Header+28)!=0 || length<1024 || length>capacity ||
                length>data.Length || (length&7)!=0 || (exactLength && length!=data.Length))
                throw new InvalidDataException("固件型号或分区不匹配；USB升级仅接受本设备的新格式应用BIN。");
            uint stack = Read32(data,0), entry = Read32(data,4);
            if (stack<=0x20000000 || stack>0x2001FFF8 || (stack&7)!=0 ||
                (entry&1)==0 || entry<address+1024 || entry>=address+length)
                throw new InvalidDataException("固件启动地址无效。");
            if (Read32(data,Header+16)!=Crc(data,(int)length))
                throw new InvalidDataException("固件校验失败，文件可能已损坏。");
        }
        public static string Version(byte[] data)
        {
            uint version=Read32(data,Header+20);
            return (version>>16)+"."+(version&65535);
        }
        public static int ProtectedEndPage(byte[] options)
        {
            if (options==null || options.Length<44)
                throw new InvalidDataException("无法读取设备启动区保护配置。");
            uint optr=Read32(options,0), wrp=Read32(options,24);
            int end=(optr & (1u<<22))!=0 ? 3 : 1;
            if ((optr&255)!=0xAA || (optr&(1u<<20))!=0 ||
                (Read32(options,40)&0x10000)!=0 ||
                (wrp&127)!=0 || ((wrp>>16)&127)!=end)
                throw new InvalidDataException("设备启动区保护或启动配置不匹配，请使用首次ST-Link安装流程。");
            return end;
        }
    }
}
