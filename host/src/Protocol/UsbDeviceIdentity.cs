using System;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace CodexToolsHost.Protocol
{
    public static class UsbDeviceIdentity
    {
        public static string SerialForPort(string port)
        {
            if (!Regex.IsMatch(port ?? "", @"^COM[0-9]+$", RegexOptions.IgnoreCase))
                throw new IOException("设备串口无效，无法核对升级目标。");
            using (var query = new ManagementObjectSearcher(
                "SELECT PNPDeviceID FROM Win32_SerialPort WHERE DeviceID='" + port + "'"))
            using (var devices = query.Get())
                foreach (ManagementObject device in devices)
                using (device)
                {
                    string id = Convert.ToString(device["PNPDeviceID"]);
                    Match match = Regex.Match(id, @"^USB\\VID_0483&PID_5740\\([A-Za-z0-9]+)$",
                        RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups[1].Value;
                }
            throw new IOException("无法取得当前板卡的USB序列号，已停止升级。");
        }
    }
}
