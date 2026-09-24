using System;

namespace CodexToolsHost.Quota
{
    public interface IJsonLineTransport : IDisposable
    {
        event Action<byte[]> MessageReceived;
        event Action<Exception> Faulted;
        void Start();
        void Send(byte[] message);
    }
}
