namespace CodexToolsHost.Quota
{
    /// <summary>API 余额数据源抽象（真实 HTTP 与测试桩）</summary>
    public interface IDeepSeekSource
    {
        event System.Action Changed;

        long BalanceCents { get; }
        string Currency { get; }
        bool Available { get; }
        bool IsStale { get; }
        bool Unlimited { get; }
        string ProviderName { get; }
        string LastError { get; }

        void Start();
        void Stop();
        string Fetch();
        void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds);
        void UpdateCredentials(string apiKey, string baseUrl, int refreshSeconds, string provider);
        void Dispose();
    }
}
