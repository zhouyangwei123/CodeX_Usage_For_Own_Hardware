namespace CodexToolsHost.Actions
{
    /// <summary>重启 ChatGPT/Codex 前的保守状态判断。</summary>
    public static class ChatGptRestartPolicy
    {
        public const int StateOffline = 0;
        public const int StateIdle = 1;
        public const int StateRunning = 2;
        public const int StateWaiting = 3;
        public const int StateError = 4;
        public const int StateComplete = 5;

        public static bool ShouldBlock(int codexState, bool appRunning)
        {
            if (codexState == StateRunning || codexState == StateWaiting || codexState == StateError)
                return true;
            if (codexState == StateIdle || codexState == StateComplete)
                return false;
            return appRunning;
        }

        public static string GetBlockedMessage(int codexState, bool appRunning)
        {
            if (codexState == StateRunning || codexState == StateWaiting || codexState == StateError)
                return "Codex 当前有任务或等待确认，未重启 ChatGPT APP，请稍后重试。";
            return appRunning
                ? "Codex 状态暂时无法确认，未重启 ChatGPT APP，请稍后重试。"
                : "Codex 状态暂时无法确认，未重启 ChatGPT APP。";
        }
    }
}
