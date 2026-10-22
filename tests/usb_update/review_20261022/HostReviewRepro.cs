using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexToolsHost.Updates;

class HostReviewRepro
{
    static byte[] flash = new byte[65536];
    static byte[] options = new byte[44];
    static List<string[]> commands = new List<string[]>();
    static int writes;
    static string Run(string[] args, int timeout)
    {
        commands.Add((string[])args.Clone());
        // The requested CDC board A never appeared in DFU. Only board B is listed.
        if (args[0] == "-l") return "Device Index : USB1\nSerial number : BOARD_B".Replace("BOARD_B", "BBBB2222");
        int upload = Array.IndexOf(args, "-u");
        if (upload >= 0)
        {
            uint address = Convert.ToUInt32(args[upload + 1].Substring(2), 16);
            int count = int.Parse(args[upload + 2]);
            byte[] data = address == 0x1FFF7800 ? options : flash.Skip((int)(address - 0x08000000)).Take(count).ToArray();
            File.WriteAllBytes(args[upload + 3], data);
            return "Read complete";
        }
        int write = Array.IndexOf(args, "-w");
        if (write >= 0)
        {
            if (!args.Contains("sn=BBBB2222")) throw new Exception("Unexpected board selection");
            uint address = Convert.ToUInt32(args[write + 2].Substring(2), 16);
            byte[] data = File.ReadAllBytes(args[write + 1]);
            Array.Copy(data, 0, flash, address - 0x08000000, data.Length);
            writes++;
            return "Download verified";
        }
        if (Array.IndexOf(args, "-s") >= 0) return "Started";
        throw new Exception("Unexpected command");
    }
    static int Main(string[] args)
    {
        byte[] app = File.ReadAllBytes(args[0]);
        byte[] boot = File.ReadAllBytes(args[1]);
        Array.Copy(boot, flash, boot.Length);
        Array.Copy(app, 0, flash, 8192, app.Length);
        Array.Copy(BitConverter.GetBytes(0x004000AAu), options, 4);
        Array.Copy(BitConverter.GetBytes(0x00030000u), 0, options, 24, 4);
        bool requestedBoardA = false;
        string result = new UsbFirmwareUpdater(Run, delegate(int delay) {}).Update(
            app, args[2], delegate { requestedBoardA = true; }, delegate(string message) {});
        Console.WriteLine("Requested CDC board A transition: " + requestedBoardA);
        Console.WriteLine("DFU listing contains only board B: BBBB2222");
        Console.WriteLine("Writes to board B: " + writes);
        Console.WriteLine("All device operations pinned to board B: " + commands.Where(a => a.Contains("-c")).All(a => a.Contains("sn=BBBB2222")));
        Console.WriteLine("Updater returned successful completion: " + Directory.Exists(result));
        File.WriteAllLines(Path.Combine(args[2], "commands.txt"), commands.Select(a => string.Join(" ", a)));
        return requestedBoardA && writes == 1 ? 0 : 1;
    }
}
