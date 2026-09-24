using System;
using System.Collections.Generic;
using System.Threading;

/* 模拟设备不会主动断开，接口事件仅为完整性保留 */
#pragma warning disable 67

namespace CodexToolsHost.Protocol
{
    /// <summary>进程内模拟固件：回应 INFO/PONG，接收 STATUS 并校验，可主动发按键/编码器事件</summary>
    public sealed class MockDeviceLink : IDeviceLink
    {
        private Timer _timer;
        private bool _started;

        public event Action Connected;
        public event Action Disconnected;
        public event Action<byte, byte[]> FrameReceived;
        public event Action<string> StatusChanged;
        public event Action<byte[]> StatusReceived;

        public bool IsConnected { get { return _started; } }
        public string PortName { get { return "MOCK"; } }

        public int CfgReqCount { get; private set; }
        public int StatusCount { get; private set; }
        public int RgbSetCount { get; private set; }
        public int OledPageCount { get; private set; }
        public int LastOledPage { get; private set; }
        public int OledTextCount { get; private set; }
        public byte[] LastStatus { get; private set; }
        public bool LastStatusValid { get; private set; }
        public int FirmwareMajor { get; set; }
        public int FirmwareMinor { get; set; }
        public bool EmitInfoOnConnect { get; set; }
        public string ConfiguredPort { get; private set; }
        public int PcMetricsCount { get; private set; }
        public byte[] LastPcMetrics { get; private set; }

        public MockDeviceLink()
        {
            FirmwareMajor = 0;
            FirmwareMinor = 1;
            EmitInfoOnConnect = true;
            LastOledPage = -1;
            ConfiguredPort = SerialPortSelection.Auto;
        }

        public void ConfigurePort(string portName)
        {
            ConfiguredPort = SerialPortSelection.Normalize(portName);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _timer = new Timer(delegate
            {
                var handler = Connected;
                if (handler != null) handler();
                if (EmitInfoOnConnect)
                    SendBack(FrameCodec.MsgInfo, BuildInfo()); /* 可模拟设备早于主机上电，INFO 已错过 */
            }, null, 50, Timeout.Infinite);
        }

        public void Reconnect()
        {
            /* 模拟设备始终在线 */
        }

        public bool Send(byte type, byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            switch (type)
            {
                case FrameCodec.MsgCfgReq:
                    CfgReqCount++;
                    SendBack(FrameCodec.MsgInfo, BuildInfo());
                    break;
                case FrameCodec.MsgPing:
                    SendBack(FrameCodec.MsgPong, payload);
                    break;
                case FrameCodec.MsgStatus:
                    StatusCount++;
                    LastStatus = (byte[])payload.Clone();
                    LastStatusValid = ValidateStatus(payload);
                    var statusHandler = StatusReceived;
                    if (statusHandler != null) statusHandler(payload);
                    break;
                case FrameCodec.MsgRgbSet:
                    RgbSetCount++;
                    break;
                case FrameCodec.MsgOledPage:
                    OledPageCount++;
                    LastOledPage = payload.Length > 0 ? payload[0] : -1;
                    break;
                case FrameCodec.MsgOledText:
                    OledTextCount++;
                    break;
                case FrameCodec.MsgPcMetrics:
                    PcMetricsCount++;
                    LastPcMetrics = (byte[])payload.Clone();
                    break;
            }
            return true;
        }

        public void EmitButton(byte index, byte kind)
        {
            SendBack(FrameCodec.MsgEvtButton, new byte[] { index, kind, 1 });
        }

        public void EmitEncoder(sbyte delta)
        {
            SendBack(FrameCodec.MsgEvtEncoder, new byte[] { unchecked((byte)delta) });
        }

        public void EmitInfo()
        {
            SendBack(FrameCodec.MsgInfo, BuildInfo());
        }

        private void SendBack(byte type, byte[] payload)
        {
            var handler = FrameReceived;
            if (handler != null) handler(type, payload);
        }

        private byte[] BuildInfo()
        {
            byte[] info = new byte[24];
            info[0] = (byte)Math.Max(0, Math.Min(255, FirmwareMajor));
            info[1] = (byte)Math.Max(0, Math.Min(255, FirmwareMinor));
            info[2] = 9; info[3] = 1; info[4] = 0x05; info[5] = 3;
            info[6] = 1; info[7] = 0;
            System.Text.Encoding.ASCII.GetBytes("CodeX Tools", 0, 11, info, 8);
            return info;
        }

        private static bool ValidateStatus(byte[] p)
        {
            if (p == null || p.Length < 22) return false;
            if (p[0] > 5) return false;
            if (p[2] != 255 && p[2] > 100) return false;
            if (p[3] != 255 && p[3] > 100) return false;
            if (p[21] != p.Length - 22) return false;
            for (int i = 22; i < p.Length; i++)
                if (p[i] < 0x20 || p[i] > 0x7E) return false;
            return true;
        }

        public void Dispose()
        {
            _started = false;
            if (_timer != null) _timer.Dispose();
            _timer = null;
        }
    }
}
