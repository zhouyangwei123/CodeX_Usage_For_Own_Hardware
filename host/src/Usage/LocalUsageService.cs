using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodexToolsHost.Usage
{
    public sealed class LocalUsageService : IDisposable
    {
        private readonly object gate = new object();
        private readonly UsageScanner scanner;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private System.Threading.Timer timer;
        private Task refresh;
        private UsageReport report = new UsageReport();
        private bool disposed;
        public event Action Changed;
        public UsageReport Report { get { lock (gate) return report; } }
        public LocalUsageService(string codexHome)
        {
            scanner = new UsageScanner(ResolveHome(codexHome));
        }
        internal static string ResolveHome(string explicitHome)
        {
            string path = explicitHome;
            if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        public void Start()
        {
            lock (gate)
            {
                if (disposed || timer != null) return;
                timer = new System.Threading.Timer(delegate { RefreshAsync(); }, null, 1500, 5000);
            }
        }
        public Task RefreshAsync()
        {
            lock (gate)
            {
                if (disposed) return Task.FromResult(false);
                if (refresh != null && !refresh.IsCompleted) return refresh;
                refresh = Task.Run((Action)Refresh);
                return refresh;
            }
        }
        private void Refresh()
        {
            try
            {
                UsageReport next = scanner.Scan(stopping.Token);
                lock (gate) { if (disposed) return; report = next; }
                Notify();
                lock (gate) { if (!disposed && timer != null) timer.Change(next.FilesPending > 0 ? 1000 : 5000, 5000); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Never log exception text: I/O exceptions may include private paths or content.
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OverflowException)) throw;
                lock (gate)
                {
                    if (disposed) return;
                    report = new UsageReport { Source = report.Source, Rows = report.Rows, UpdatedAt = report.UpdatedAt,
                        RecentActivity = report.RecentActivity, LastObservedEventAt = report.LastObservedEventAt,
                        ActivitySourceAvailable = report.ActivitySourceAvailable, IsScanComplete = false, ScanFailed = true,
                        FilesDiscovered = report.FilesDiscovered, IsImported = report.IsImported, PeriodKind = report.PeriodKind,
                        ImportedApiEstimateUsd = report.ImportedApiEstimateUsd,
                        Warnings = new System.Collections.Generic.List<string> { "本次用量读取未完成，保留上次结果；可稍后刷新。" }.AsReadOnly(), FilesPending = report.FilesPending };
                }
                Notify();
            }
        }
        private void Notify()
        {
            Action handlers;
            lock (gate) { if (disposed) return; handlers = Changed; }
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                lock (gate) { if (disposed) return; }
                try { handler(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopping.Cancel();
                if (timer != null) timer.Dispose();
                Changed = null;
                // Do not block the UI while a disk read exits. Dispose CTS after the worker finishes.
                if (refresh == null || refresh.IsCompleted) stopping.Dispose();
                else refresh.ContinueWith(delegate { stopping.Dispose(); }, TaskScheduler.Default);
            }
        }
    }
}
