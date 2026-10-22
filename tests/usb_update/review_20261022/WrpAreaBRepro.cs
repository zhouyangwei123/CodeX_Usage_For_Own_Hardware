using System;
using System.IO;
using System.Linq;
using CodexToolsHost.Updates;

class WrpAreaBRepro
{
    static byte[] flash = Enumerable.Repeat((byte)0xCC, 65536).ToArray();
    static byte[] options = new byte[44];
    static int bootWrites, blockedAppWrites;
    static string Run(string[] args, int timeout)
    {
        if (args[0] == "-l") return "ST-LINK SN : TESTPROBE123\n";
        int upload = Array.IndexOf(args, "-u");
        if (upload >= 0)
        {
            uint address = Convert.ToUInt32(args[upload + 1].Substring(2), 16);
            int count = int.Parse(args[upload + 2]);
            byte[] data;
            if (address == 0xE0042000) data = BitConverter.GetBytes(0x469u);
            else if (address == 0x1FFF75E0) data = BitConverter.GetBytes((ushort)128);
            else if (address == 0x1FFF7800) data = options;
            else data = flash.Skip((int)(address - 0x08000000)).Take(count).ToArray();
            File.WriteAllBytes(args[upload + 3], data);
            return "Read complete";
        }
        int ob = Array.IndexOf(args, "-ob");
        if (ob >= 0 && args[ob + 1] == "displ") return "WRP1A disabled; WRP1B_STRT=4 WRP1B_END=4";
        int write = Array.IndexOf(args, "-w");
        if (write >= 0)
        {
            uint address = Convert.ToUInt32(args[write + 2].Substring(2), 16);
            if (address == 0x08002000)
            {
                // DBANK=1 page 4 starts at AppBase, and WRP1B covers this page.
                blockedAppWrites++;
                throw new IOException("Application page is write-protected by WRP1B");
            }
            byte[] data = File.ReadAllBytes(args[write + 1]);
            Array.Copy(data, 0, flash, address - 0x08000000, data.Length);
            bootWrites++;
            return "Download verified";
        }
        throw new Exception("Unexpected command");
    }
    static int Main(string[] args)
    {
        byte[] app = File.ReadAllBytes(args[0]);
        byte[] boot = File.ReadAllBytes(args[1]);
        Array.Copy(BitConverter.GetBytes(0x004000AAu), options, 4);
        Array.Copy(BitConverter.GetBytes(127u), 0, options, 24, 4);
        Array.Copy(BitConverter.GetBytes(0x00040004u), 0, options, 32, 4);
        bool failed = false;
        try { new StLinkFirmwareInstaller(Run).Install(boot, app, args[2]); }
        catch (IOException ex) { failed = true; Console.WriteLine("Installer failure: " + ex.Message); }
        Console.WriteLine("Boot writes before pre-existing application protection was rejected: " + bootWrites);
        Console.WriteLine("Application writes blocked by WRP1B: " + blockedAppWrites);
        Console.WriteLine("Old reset vector overwritten: " + (FirmwareImage.Read32(flash, 4) != 0xCCCCCCCCu));
        return failed && bootWrites == 1 && blockedAppWrites == 1 ? 0 : 1;
    }
}
