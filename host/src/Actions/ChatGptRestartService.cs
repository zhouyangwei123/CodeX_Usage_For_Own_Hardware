using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexToolsHost.Actions
{
    public interface IChatGptRestartService
    {
        void RestartAsync(int codexState, Action<string> onResult);
    }

    /// <summary>协调状态检查、正常关闭、启动和结果通知的 ChatGPT 重启服务。</summary>
    public sealed class ChatGptRestartService : IChatGptRestartService
    {
        private const int CloseTimeoutMs = 8000;
        private const int TerminateTimeoutMs = 3000;
        private const int LaunchTimeoutMs = 12000;
        private readonly IChatGptAppRuntime _runtime;
        private int _busy;

        public ChatGptRestartService()
            : this(new WindowsChatGptAppRuntime())
        {
        }

        internal ChatGptRestartService(IChatGptAppRuntime runtime)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            _runtime = runtime;
        }

        public void RestartAsync(int codexState, Action<string> onResult)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                if (onResult != null) onResult("ChatGPT APP 正在重启，请勿重复操作。");
                return;
            }

            Task.Run(delegate
            {
                try
                {
                    string result = RestartCore(codexState);
                    if (onResult != null) onResult(result);
                }
                catch (Exception)
                {
                    if (onResult != null) onResult("重启 ChatGPT APP 失败，请稍后重试。");
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }

        internal string RestartCore(int codexState)
        {
            bool appRunning = _runtime.IsRunning;
            if (ChatGptRestartPolicy.ShouldBlock(codexState, appRunning))
                return ChatGptRestartPolicy.GetBlockedMessage(codexState, appRunning);

            if (appRunning)
            {
                string error;
                if (!_runtime.TryClose(out error))
                    return "ChatGPT APP 无法正常关闭，未执行重启。";
                if (!_runtime.WaitForExit(CloseTimeoutMs))
                {
                    string terminateError;
                    if (!_runtime.TryTerminate(out terminateError)
                        || !_runtime.WaitForExit(TerminateTimeoutMs))
                        return "ChatGPT APP 仍未完全退出，未执行重启。";
                }
            }

            string launchError;
            if (!_runtime.TryLaunch(out launchError))
                return "ChatGPT APP 启动失败，请检查应用是否已安装。";
            if (!_runtime.WaitForWindow(LaunchTimeoutMs))
                return "已请求启动 ChatGPT APP，但未确认窗口。";
            return "ChatGPT APP 已重启。";
        }
    }
}
