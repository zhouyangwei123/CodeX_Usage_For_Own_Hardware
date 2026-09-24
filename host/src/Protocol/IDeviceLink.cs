using System;

namespace CodexToolsHost.Protocol
{
    /// <summary>设备链路抽象：SerialLink（真实串口）与 MockDeviceLink（模拟设备）</summary>
    public interface IDeviceLink : IDisposable
    {
        event Action Connected;
        event Action Disconnected;
        event Action<byte, byte[]> FrameReceived;
        event Action<string> StatusChanged;

        bool IsConnected { get; }
        string PortName { get; }

        void Start();
        void ConfigurePort(string portName);
        void Reconnect();
        bool Send(byte type, byte[] payload);
    }
}
