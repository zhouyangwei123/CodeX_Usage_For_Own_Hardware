using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Quota;
using CodexToolsHost.Usage;

namespace CodexToolsHost.UI
{
    public sealed partial class QuotaHudForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int MA_ACTIVATE = 1;
        private const int MA_NOACTIVATE = 3;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ApiGap = 6;
        /* API 区域现在承载两行摘要；42 只够旧版单行显示。 */
        private const int ApiHeight = 54;

        private readonly AppConfig _config;
        private readonly ICodexStatusSource _source;
        private readonly IDeepSeekSource _apiSource;
        private readonly IOpenCodeGoQuotaSource _openCodeGoSource;
        private readonly Action _refreshAction;
        private readonly BridgeService _bridge;
        private readonly object _pcMetricsSync = new object();
        private readonly int _uiThreadId;
        private readonly QuotaHudRenderer _renderer = new QuotaHudRenderer();
        private readonly Timer _animationTimer = new Timer();
        private readonly Timer _displayTimer = new Timer();
        private readonly Timer _positionTimer = new Timer();
        private readonly Font _apiFont = new Font("Segoe UI", 8f,
            FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _apiGoFont = new Font("Segoe UI", 7f,
            FontStyle.Bold, GraphicsUnit.Point);
        private QuotaSnapshot _snapshot;
        private string _apiDisplayText;
        private bool _apiStale;
        private bool _apiAvailable;
        private bool _apiUnlimited;
        private bool _apiIsOpenCodeGo;
        private string _networkDisplayText;
        private PcMetricsSnapshot _latestPcMetrics;
        private bool _networkRefreshQueued;
        private float _animationAngle;
        private bool _refreshing;
        private bool _initialized;
        private Bitmap _frame;
        private bool _frameDirty = true;
        private bool _nativeFallback;
        private bool _presenting;
        private DateTimeOffset _refreshDeadline;
        private volatile bool _shutdown;

        public QuotaHudForm(AppConfig config, ICodexStatusSource source,
            IDeepSeekSource apiSource, Action refreshAction, BridgeService bridge)
            : this(config, source, apiSource, refreshAction, bridge, null) { }

        public QuotaHudForm(AppConfig config, ICodexStatusSource source,
            IDeepSeekSource apiSource, Action refreshAction, BridgeService bridge, LocalUsageService usage)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (source == null) throw new ArgumentNullException("source");
            if (apiSource == null) throw new ArgumentNullException("apiSource");
            if (bridge == null) throw new ArgumentNullException("bridge");
            _config = config;
            _source = source;
            _apiSource = apiSource;
            _openCodeGoSource = bridge.OpenCodeGo;
            _refreshAction = refreshAction;
            _bridge = bridge;
            _usage = usage;
            _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _snapshot = source.Quota ?? QuotaSnapshot.EmptyStale();

            Text = "Codex Quota HUD";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = _config.QuotaHudTopMost;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(22, 27, 36);
            AutoScaleMode = AutoScaleMode.None;
            ApplyWindowSize(GetScaledWindowSize());
            MaximizeBox = false;
            MinimizeBox = false;
            DoubleBuffered = true;
            SetInitialLocation();
            UpdateApiSnapshot();
            RefreshNetworkFromBridgeOnUiThread();

            _animationTimer.Interval = 33;
            _animationTimer.Tick += delegate
            {
                if (!_refreshing || !Visible || _shutdown)
                {
                    _animationTimer.Stop();
                    return;
                }
                if (DateTimeOffset.UtcNow >= _refreshDeadline)
                {
                    _refreshing = false;
                    _animationTimer.Stop();
                    RequestRender();
                    return;
                }
                _animationAngle = (_animationAngle + 15f) % 360f;
                RequestRender();
            };
            _positionTimer.Interval = 300;
            _positionTimer.Tick += delegate
            {
                _positionTimer.Stop();
                SavePosition();
            };
            _displayTimer.Interval = 1000;
            _displayTimer.Tick += delegate
            {
                if (_shutdown || !Visible || !_apiIsOpenCodeGo) return;
                if (UpdateApiSnapshot()) RequestRender();
            };

            _source.Changed += OnSourceChanged;
            _source.StatusChanged += OnStatusChanged;
            _apiSource.Changed += OnApiSourceChanged;
            _openCodeGoSource.Changed += OnOpenCodeGoSourceChanged;
            _bridge.PcMetricsChanged += OnPcMetricsChanged;
            InitializeActivity();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_TOOLWINDOW;
                if (_config == null || _config.QuotaHudTopMost) parameters.ExStyle |= WS_EX_NOACTIVATE;
                else parameters.ExStyle &= ~WS_EX_NOACTIVATE;
                if (!UsesOpaqueFallback) parameters.ExStyle |= LayeredWindowSurface.ExtendedStyle;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RefreshNetworkFromBridgeOnUiThread();
            RequestRender();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _animationTimer.Stop();
            _displayTimer.Stop();
            _activityTimer.Stop();
            lock (_pcMetricsSync) _networkRefreshQueued = false;
            _frameDirty = true;
            base.OnHandleDestroyed(e);
        }

        public void ShowFromTray()
        { ShowHud(true); }

        // Startup/settings synchronization must not take focus from another app.
        public void ShowPassive()
        { ShowHud(false); }

        private void ShowHud(bool bringForward)
        {
            if (_shutdown || IsDisposed) return;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke(new Action(delegate { ShowHud(bringForward); }));
                }
                catch (InvalidOperationException) { }
                return;
            }
            if (WindowState != FormWindowState.Normal)
            {
                // Passive synchronization must respect the user's minimized window.
                // Form.WindowState=Normal activates even a no-activate HWND; use the
                // native nonactivating restore, then activate only explicit unpinned
                // recovery below. WM_SIZE synchronizes the managed window state.
                if (!bringForward) return;
                if (IsHandleCreated) ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
                else WindowState = FormWindowState.Normal;
            }
            ApplyDisplaySettings();
            _snapshot = _source.Quota ?? QuotaSnapshot.EmptyStale();
            UpdateApiSnapshot();
            RefreshNetworkFromBridgeOnUiThread();
            if (!Visible) Show();
            if (bringForward)
            {
                // Form.BringToFront also activates a top-level window. Raise within
                // the existing z-order band without focusing a pinned HUD.
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013 /* NOSIZE | NOMOVE | NOACTIVATE */);
                if (!_config.QuotaHudTopMost) Activate();
            }
            RequestRender();
        }

        public void ApplyDisplaySettings()
        {
            if (_shutdown || IsDisposed) return;
            // Apply only changed window preferences during passive synchronization.
            if (TopMost != _config.QuotaHudTopMost) TopMost = _config.QuotaHudTopMost;
            if (WindowState != FormWindowState.Normal)
            {
                // An iconic window's Bounds are not its desktop position. Reconcile
                // styles now; ShowHud applies geometry after restoring normal state.
                UpdateStyles();
                return;
            }
            Point anchor = new Point(Right, Bottom);
            Size target = GetScaledWindowSize();
            bool sizeChanged = ClientSize != target;
            if (sizeChanged) ApplyWindowSize(target);
            if (sizeChanged)
                Location = new Point(anchor.X - Width, anchor.Y - Height);
            Bounds = QuotaHudPresentation.EnsureVisible(Bounds, GetWorkingAreas());
            UpdateStyles();
            UpdateApiSnapshot();
            if (_initialized) SavePosition();
            RequestRender();
        }

        public void HideFromTray()
        {
            if (_shutdown || IsDisposed) return;
            Hide();
            SavePosition();
        }

        public void BeginShutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            _animationTimer.Stop();
            _displayTimer.Stop();
            _positionTimer.Stop();
            _source.Changed -= OnSourceChanged;
            _source.StatusChanged -= OnStatusChanged;
            _apiSource.Changed -= OnApiSourceChanged;
            _openCodeGoSource.Changed -= OnOpenCodeGoSourceChanged;
            _bridge.PcMetricsChanged -= OnPcMetricsChanged;
            _activityTimer.Stop();
            if (_usage != null) _usage.Changed -= OnUsageChanged;
            SavePosition();
        }

        public void BeginRefreshVisual()
        {
            if (_shutdown) return;
            _refreshing = true;
            _animationAngle = 0f;
            _refreshDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            UpdateTimers();
            RequestRender();
            try
            {
                if (_refreshAction != null) _refreshAction();
            }
            catch (Exception)
            {
                _refreshing = false;
                _animationTimer.Stop();
                RequestRender();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _initialized = true;
            _snapshot = _source.Quota ?? QuotaSnapshot.EmptyStale();
            UpdateApiSnapshot();
            RequestRender();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_config == null || _shutdown) return;
            if (Visible)
            {
                _snapshot = _source.Quota ?? QuotaSnapshot.EmptyStale();
                UpdateApiSnapshot();
                RefreshNetworkFromBridgeOnUiThread();
                RefreshActivity(true);
            }
            UpdateTimers();
            if (Visible) RequestRender();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!_initialized || _shutdown) return;
            _positionTimer.Stop();
            if (WindowState != FormWindowState.Normal) return;
            _positionTimer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_config != null) UpdateTimers();
            RequestRender();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The frame contains the background. A separate opaque clear would erase alpha.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_shutdown || _config == null) return;
            EnsureFrame();
            if (_frame != null) e.Graphics.DrawImageUnscaled(_frame, Point.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || _shutdown) return;
            if (_usage != null && ScaleToClient(QuotaHudRenderer.ActivityToggleBounds()).Contains(e.Location))
            { ToggleChart(); return; }
            Rectangle refresh = _renderer.RefreshHitBounds(QuotaBounds());
            refresh = ScaleToClient(refresh);
            if (refresh.Contains(e.Location)) BeginRefreshVisual();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_shutdown)
            {
                e.Cancel = true;
                Hide();
                SavePosition();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WM_GETMINMAXINFO && _config != null && message.LParam != IntPtr.Zero)
            {
                base.WndProc(ref message);
                var limits = (HudMinMaxInfo)Marshal.PtrToStructure(message.LParam, typeof(HudMinMaxInfo));
                Size fixedSize = GetScaledWindowSize();
                limits.MinTrack = new HudNativePoint { X = fixedSize.Width, Y = fixedSize.Height };
                limits.MaxTrack = limits.MinTrack;
                Marshal.StructureToPtr(limits, message.LParam, false);
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == WM_MOUSEACTIVATE)
            {
                message.Result = (IntPtr)(_config == null || _config.QuotaHudTopMost ? MA_NOACTIVATE : MA_ACTIVATE);
                return;
            }
            if (message.Msg == WM_NCLBUTTONDBLCLK)
            {
                /* HTCAPTION 用于拖动，但不能把双击转成系统最大化/全屏动作。 */
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == WM_NCHITTEST)
            {
                long packed = message.LParam.ToInt64();
                Point screen = new Point(
                    unchecked((short)(packed & 0xffff)),
                    unchecked((short)((packed >> 16) & 0xffff)));
                Point client = PointToClient(screen);
                Rectangle refresh = _renderer.RefreshHitBounds(QuotaBounds());
                refresh = ScaleToClient(refresh);
                bool activityHit = _usage != null && (ScaleToClient(QuotaHudRenderer.ActivityToggleBounds()).Contains(client)
                    || ScaleToClient(QuotaHudRenderer.ActivityFooterBounds(_config.QuotaHudChartExpanded)).Contains(client));
                message.Result = (IntPtr)(refresh.Contains(client) || activityHit
                    ? HTCLIENT : HTCAPTION);
                return;
            }
            base.WndProc(ref message);
            // System high-contrast/theme changes arrive on the owning UI thread.
            // Reconcile the HWND as well as the pixels, including while the HUD is hidden.
            if ((message.Msg == 0x001A || message.Msg == 0x031A) && _config != null && !_shutdown)
            {
                UpdateStyles();
                RequestRender();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                BeginShutdown();
                _animationTimer.Dispose();
                _displayTimer.Dispose();
                _positionTimer.Dispose();
                _activityTimer.Dispose();
                _activityTooltip.Dispose();
                _apiFont.Dispose();
                _apiGoFont.Dispose();
                _renderer.Dispose();
                if (_frame != null) { _frame.Dispose(); _frame = null; }
            }
            base.Dispose(disposing);
        }

        private void OnSourceChanged()
        {
            RunOnUiThread(UpdateSnapshot);
        }

        private void OnStatusChanged(string text)
        {
            RunOnUiThread(UpdateSnapshot);
        }

        private void OnApiSourceChanged()
        {
            RunOnUiThread(delegate
            {
                if (UpdateApiSnapshot()) RequestRender();
            });
        }

        private void OnOpenCodeGoSourceChanged()
        {
            RunOnUiThread(delegate
            {
                if (UpdateApiSnapshot()) RequestRender();
            });
        }

        private void OnPcMetricsChanged(PcMetricsSnapshot snapshot)
        {
            lock (_pcMetricsSync)
            {
                _latestPcMetrics = snapshot;
            }
            QueueNetworkRefresh();
        }

        private void UpdateSnapshot()
        {
            if (_shutdown || IsDisposed) return;
            QuotaSnapshot next = _source.Quota ?? QuotaSnapshot.EmptyStale();
            bool changed = _refreshing || _snapshot == null
                || _snapshot.PrimaryRemainingPercent != next.PrimaryRemainingPercent
                || _snapshot.SecondaryRemainingPercent != next.SecondaryRemainingPercent
                || _snapshot.PrimaryWindowMinutes != next.PrimaryWindowMinutes
                || _snapshot.SecondaryWindowMinutes != next.SecondaryWindowMinutes
                || _snapshot.IsStale != next.IsStale;
            _snapshot = next;
            _refreshing = false;
            _animationTimer.Stop();
            if (changed) RequestRender();
        }

        private bool UpdateApiSnapshot()
        {
            if (_shutdown || IsDisposed) return false;
            string previous = _apiDisplayText;
            bool stale = _apiStale, available = _apiAvailable, unlimited = _apiUnlimited;
            bool isGo = _apiIsOpenCodeGo;
            _apiIsOpenCodeGo = string.Equals(
                AppConfig.NormalizeQuotaDisplaySource(_config.QuotaDisplaySource),
                "opencodego", StringComparison.OrdinalIgnoreCase);
            if (_apiIsOpenCodeGo)
            {
                _apiDisplayText = QuotaHudPresentation.FormatOpenCodeGoSummary(
                    _openCodeGoSource.Quota, DateTimeOffset.UtcNow);
                _apiStale = _openCodeGoSource.IsStale;
                _apiAvailable = _openCodeGoSource.IsConfigured && !_apiStale;
                _apiUnlimited = false;
            }
            else
            {
                _apiDisplayText = QuotaHudPresentation.FormatDeepSeekSummary(
                    _apiSource.ProviderName, _apiSource.Currency, _apiSource.BalanceCents,
                    _apiSource.Available, _apiSource.IsStale, _apiSource.Unlimited,
                    DateTimeOffset.UtcNow);
                _apiStale = _apiSource.IsStale;
                _apiAvailable = _apiSource.Available;
                _apiUnlimited = _apiSource.Unlimited;
            }
            UpdateTimers();
            return previous != _apiDisplayText || stale != _apiStale
                || available != _apiAvailable || unlimited != _apiUnlimited || isGo != _apiIsOpenCodeGo;
        }

        private void UpdateNetworkSnapshot(PcMetricsSnapshot snapshot)
        {
            _networkDisplayText = QuotaHudPresentation.FormatNetworkSpeed(
                snapshot != null && snapshot.NetworkSpeedAvailable,
                snapshot == null ? 0u : snapshot.NetworkDownloadKiBPerSecond,
                snapshot == null ? 0u : snapshot.NetworkUploadKiBPerSecond);
        }

        private void RefreshNetworkFromBridgeOnUiThread()
        {
            if (_shutdown || IsDisposed) return;
            PcMetricsSnapshot snapshot = _bridge.PcMetrics;
            lock (_pcMetricsSync)
            {
                _latestPcMetrics = snapshot;
            }
            UpdateNetworkSnapshot(snapshot);
        }

        private void QueueNetworkRefresh()
        {
            if (_shutdown || IsDisposed || !IsHandleCreated) return;
            lock (_pcMetricsSync)
            {
                if (_networkRefreshQueued) return;
                _networkRefreshQueued = true;
            }
            try
            {
                BeginInvoke(new Action(ApplyQueuedNetworkRefresh));
            }
            catch (InvalidOperationException)
            {
                lock (_pcMetricsSync) _networkRefreshQueued = false;
            }
        }

        private void ApplyQueuedNetworkRefresh()
        {
            PcMetricsSnapshot snapshot;
            lock (_pcMetricsSync)
            {
                _networkRefreshQueued = false;
                snapshot = _latestPcMetrics;
            }
            if (_shutdown || IsDisposed) return;
            string previous = _networkDisplayText;
            UpdateNetworkSnapshot(snapshot);
            if (previous != _networkDisplayText) RequestRender();
        }

        private void RunOnUiThread(Action action)
        {
            if (_shutdown || IsDisposed) return;
            try
            {
                if (InvokeRequired)
                {
                    if (IsHandleCreated) BeginInvoke(action);
                    return;
                }
                action();
            }
            catch (InvalidOperationException) { }
        }

        private void SetInitialLocation()
        {
            Rectangle workArea = Screen.PrimaryScreen == null
                ? new Rectangle(0, 0, 1280, 720)
                : Screen.PrimaryScreen.WorkingArea;
            if (_config.QuotaHudX.HasValue && _config.QuotaHudY.HasValue)
            {
                Rectangle saved = new Rectangle(_config.QuotaHudX.Value,
                    _config.QuotaHudY.Value, Width, Height);
                Location = QuotaHudPresentation.EnsureVisible(saved,
                    GetWorkingAreas()).Location;
            }
            else
            {
                Location = QuotaHudPresentation.DefaultHudLocation(workArea, Size);
            }
        }

        private Size GetScaledWindowSize()
        {
            return QuotaHudPresentation.ScaleSize(CanonicalWindowSize(),
                _config.QuotaHudScalePercent);
        }

        private void ApplyWindowSize(Size target)
        {
            // Framework's MinimumSize setter activates an existing HWND. Fixed
            // native tracking limits are supplied via WM_GETMINMAXINFO instead.
            MaximumSize = Size.Empty;
            ClientSize = target;
            MaximumSize = Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HudNativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HudMinMaxInfo
        { public HudNativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private Size CanonicalWindowSize()
        {
            if (_usage != null) return QuotaHudRenderer.ActivityWindowSize(_config.QuotaHudChartExpanded);
            return new Size(_renderer.PreferredSize.Width,
                _renderer.PreferredSize.Height + ApiGap + ApiHeight);
        }

        private Rectangle ScaleToClient(Rectangle canonical)
        {
            Size standard = CanonicalWindowSize();
            float sx = ClientSize.Width / (float)standard.Width;
            float sy = ClientSize.Height / (float)standard.Height;
            return Rectangle.FromLTRB(
                (int)Math.Round(canonical.Left * sx),
                (int)Math.Round(canonical.Top * sy),
                (int)Math.Round(canonical.Right * sx),
                (int)Math.Round(canonical.Bottom * sy));
        }

        private Rectangle[] GetWorkingAreas()
        {
            Screen[] screens = Screen.AllScreens;
            if (screens == null || screens.Length == 0)
                return new[] { new Rectangle(0, 0, 1280, 720) };
            Rectangle[] areas = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) areas[i] = screens[i].WorkingArea;
            return areas;
        }

        private void SavePosition()
        {
            if ((_shutdown && IsDisposed) || WindowState != FormWindowState.Normal) return;
            try
            {
                _config.QuotaHudX = Left;
                _config.QuotaHudY = Top;
                _config.Save();
            }
            catch (Exception) { }
        }

        private bool UsesOpaqueFallback
        {
            get { return _nativeFallback || SystemInformation.HighContrast; }
        }

        internal byte EffectiveOpacityAlpha
        {
            get { return UsesOpaqueFallback ? (byte)255 : (byte)Math.Round(
                QuotaHudPresentation.NormalizeOpacity(_config.QuotaHudOpacity) * 255d); }
        }

        // Shared by native composition, ordinary fallback painting and offscreen verification.
        // Caller owns the returned bitmap; rendering never creates a native window.
        internal Bitmap RenderFrame()
        {
            if (_usage != null) return _renderer.RenderActivityFrame(ClientSize, _snapshot, _config.QuotaHudStyle,
                _animationAngle, _refreshing, _apiDisplayText, _networkDisplayText,
                _apiStale, _apiAvailable || _apiUnlimited, _apiIsOpenCodeGo, UsesOpaqueFallback,
                _activitySnapshot, _config.QuotaHudChartExpanded);
            return _renderer.RenderFrame(ClientSize, _snapshot, _config.QuotaHudStyle,
                _animationAngle, _refreshing, _apiDisplayText, _networkDisplayText,
                _apiStale, _apiAvailable || _apiUnlimited, _apiIsOpenCodeGo, UsesOpaqueFallback);
        }

        private void EnsureFrame()
        {
            if (!_frameDirty && _frame != null) return;
            Bitmap next = RenderFrame();
            Bitmap previous = _frame;
            _frame = next;
            _frameDirty = false;
            if (previous != null) previous.Dispose();
        }

        private void RequestRender()
        {
            _frameDirty = true;
            if (_config == null || _shutdown || IsDisposed || !Visible || WindowState != FormWindowState.Normal || !IsHandleCreated
                || _presenting || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            _presenting = true;
            try
            {
                EnsureFrame();
                if (UsesOpaqueFallback)
                {
                    Invalidate();
                }
                else if (!LayeredWindowSurface.TryPresent(Handle, Location, _frame, EffectiveOpacityAlpha))
                {
                    ActivateOpaqueFallback();
                }
            }
            finally { _presenting = false; }
        }

        private void ActivateOpaqueFallback()
        {
            // Removing WS_EX_LAYERED restores ordinary WM_PAINT after a native failure.
            // Do not call SetLayeredWindowAttributes or Form.Opacity on this window.
            _nativeFallback = true;
            _frameDirty = true;
            UpdateStyles();
            EnsureFrame();
            Invalidate();
        }

        private void UpdateTimers()
        {
            if (_shutdown || IsDisposed) return;
            bool presented = Visible && WindowState == FormWindowState.Normal;
            _animationTimer.Enabled = presented && _refreshing;
            _activityTimer.Enabled = presented && _usage != null;
            OpenCodeGoQuotaSnapshot go = _openCodeGoSource == null ? null : _openCodeGoSource.Quota;
            _displayTimer.Enabled = presented && _apiIsOpenCodeGo && go != null
                && go.Rolling.ResetsAt.HasValue && go.Rolling.ResetsAt.Value > DateTimeOffset.UtcNow;
        }
    }
}
