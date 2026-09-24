using System.Threading.Tasks;
using CodexToolsHost.Model;

namespace CodexToolsHost.Quota
{
    /// <summary>Codex 状态/额度数据源抽象（真实 app-server 与测试桩）</summary>
    public interface ICodexStatusSource
    {
        event System.Action Changed;
        event System.Action<string> StatusChanged;

        QuotaSnapshot Quota { get; }
        int State { get; }
        string StatusText { get; }

        Task StartAsync();
        Task RefreshQuotaAsync();
        void Stop();
        void Dispose();
    }
}
