using System;
using System.Threading.Tasks;
using CodexToolsHost.Actions;
using CodexToolsHost.Model;
using CodexToolsHost.Quota;

/* 测试桩的事件仅为接口完整性保留，故意不触发 */
#pragma warning disable 67

namespace CodexToolsHost.Tests
{
    /// <summary>模拟测试用的固定数据源</summary>
    public sealed class FakeCodexSource : ICodexStatusSource
    {
        public event Action Changed;
        public event Action<string> StatusChanged;

        public QuotaSnapshot Quota { get { return new QuotaSnapshot(58, null, null, null, DateTimeOffset.UtcNow, false); } }
        public int State { get { return CodexStatusProvider.CodexStateRunning; } }
        public string StatusText { get { return "RUN MOCK-THREAD"; } }

        public Task StartAsync() { return Task.FromResult(0); }
        public Task RefreshQuotaAsync() { return Task.FromResult(0); }
        public void Stop() { }
        public void Dispose() { }
    }

    public sealed class FakeDeepSeekSource : IDeepSeekSource
    {
        public event Action Changed;
        public long BalanceCents { get { return 12345; } }
        public string Currency { get { return "CNY"; } }
        public bool Available { get { return true; } }
        public bool IsStale { get { return false; } }
        public bool Unlimited { get { return false; } }
        public string ProviderName { get { return "DeepSeek"; } }
        public string LastError { get { return ""; } }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int FetchCalls { get; private set; }

        public void Start() { StartCalls++; }
        public void Stop() { StopCalls++; }
        public string Fetch() { FetchCalls++; return "OK 123.45 CNY"; }
        public void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds) { }
        public void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds, string provider) { }
        public void Dispose() { }
    }

    public sealed class FakeOpenCodeGoSource : IOpenCodeGoQuotaSource
    {
        public event Action Changed;
        public OpenCodeGoQuotaSnapshot Quota { get; set; }
        public bool IsConfigured { get; set; }
        public bool IsStale { get { return Quota == null || Quota.IsStale; } }
        public string LastError { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int FetchCalls { get; private set; }

        public FakeOpenCodeGoSource()
        {
            Quota = OpenCodeGoQuotaSnapshot.EmptyStale();
            LastError = "";
        }

        public void Start() { StartCalls++; }
        public void Stop() { StopCalls++; }
        public string Fetch() { FetchCalls++; return "OK"; }
        public void UpdateCredentials(string apiKey, int refreshSeconds) { IsConfigured = !string.IsNullOrWhiteSpace(apiKey); }
        public void Dispose() { }
    }

    public sealed class FakeChatGptRestartService : IChatGptRestartService
    {
        public int RestartCalls { get; private set; }
        public int LastState { get; private set; }

        public void RestartAsync(int codexState, Action<string> onResult)
        {
            RestartCalls++;
            LastState = codexState;
            if (onResult != null) onResult("fake restart");
        }
    }
}
