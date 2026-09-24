using System;
using CodexToolsHost.Model;

namespace CodexToolsHost.Quota
{
    public interface IOpenCodeGoQuotaSource : IDisposable
    {
        event Action Changed;

        OpenCodeGoQuotaSnapshot Quota { get; }
        bool IsConfigured { get; }
        bool IsStale { get; }
        string LastError { get; }

        void Start();
        void Stop();
        string Fetch();
        void UpdateCredentials(string apiKey, int refreshSeconds);
    }
}
