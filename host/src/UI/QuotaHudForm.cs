using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Quota;

namespace CodexToolsHost.UI
{
    public sealed class QuotaHudForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
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
        private volatile bool _shutdown;

        public QuotaHudForm(AppConfig config, ICodexStatusSource source,
            IDeepSeekSource apiSource, Action refreshAction, BridgeService bridge)
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
            _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _snapshot = source.Quota ?? QuotaSnapshot.EmptyStale();

            Text = "Codex Quota HUD";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = _config.QuotaHudTopMost;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(22, 27, 36);
            Opacity = QuotaHudPresentation.NormalizeOpacity(_config.QuotaHudOpacity);
            AutoScaleMode = AutoScaleMode.None;
            ApplyWindowSize(GetScaledWindowSize());
            MaximizeBox = false;
            MinimizeBox = false;
            DoubleBuffered = true;
            SetInitialLocation();
            UpdateWindowRegion();
            UpdateApiSnapshot();
            RefreshNetworkFromBridgeOnUiThread();

            _animationTimer.Interval = 33;
            _animationTimer.Tick += delegate
            {
                if (!_refreshing)
                {
                    _animationTimer.Stop();
                    return;
                }
                _animationAngle = (_animationAngle + 15f) % 360f;
                Invalidate();
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
                if (_shutdown || !_apiIsOpenCodeGo) return;
                UpdateApiSnapshot();
                Invalidate();
            };
            _displayTimer.Start();

            _source.Changed += OnSourceChanged;
            _source.StatusChanged += OnStatusChanged;
            _apiSource.Changed += OnApiSourceChanged;
            _openCodeGoSource.Changed += OnOpenCodeGoSourceChanged;
            _bridge.PcMetricsChanged += OnPcMetricsChanged;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RefreshNetworkFromBridgeOnUiThread();
        }

        public void ShowFromTray()
        {
            if (_shutdown || IsDisposed) return;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                try
                {
                    if (IsHandleCreated) BeginInvoke(new Action(ShowFromTray));
                }
                catch (InvalidOperationException) { }
                return;
            }
            ApplyDisplaySettings();
            _snapshot = _source.Quota ?? QuotaSnapshot.EmptyStale();
            UpdateApiSnapshot();
            RefreshNetworkFromBridgeOnUiThread();
            if (!Visible) Show();
            Invalidate();
        }

        public void ApplyDisplaySettings()
        {
            if (_shutdown || IsDisposed) return;
            Point anchor = new Point(Right, Bottom);
            Size target = GetScaledWindowSize();
            bool sizeChanged = ClientSize != target;
            ApplyWindowSize(target);
            Opacity = QuotaHudPresentation.NormalizeOpacity(_config.QuotaHudOpacity);
            TopMost = _config.QuotaHudTopMost;
            if (sizeChanged)
                Location = new Point(anchor.X - Width, anchor.Y - Height);
            Bounds = QuotaHudPresentation.EnsureVisible(Bounds, GetWorkingAreas());
            UpdateWindowRegion();
            if (_initialized) SavePosition();
            Invalidate();
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
            SavePosition();
        }

        public void BeginRefreshVisual()
        {
            if (_shutdown) return;
            _refreshing = true;
            _animationAngle = 0f;
            _animationTimer.Start();
            Invalidate();
            try
            {
                if (_refreshAction != null) _refreshAction();
            }
            catch (Exception)
            {
                _refreshing = false;
                _animationTimer.Stop();
                Invalidate();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _initialized = true;
            _snapshot = _source.Quota ?? QuotaSnapshot.EmptyStale();
            UpdateApiSnapshot();
            UpdateWindowRegion();
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!_initialized || _shutdown) return;
            _positionTimer.Stop();
            _positionTimer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateWindowRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float sx = ClientSize.Width / (float)CanonicalWindowSize().Width;
            float sy = ClientSize.Height / (float)CanonicalWindowSize().Height;
            GraphicsState state = e.Graphics.Save();
            try
            {
                e.Graphics.ScaleTransform(sx, sy);
                _renderer.Draw(e.Graphics,
                    new Rectangle(Point.Empty, _renderer.PreferredSize), _snapshot,
                    _animationAngle, _refreshing, _snapshot.IsStale);
                DrawApiBalance(e.Graphics, new Rectangle(0,
                    _renderer.PreferredSize.Height + ApiGap,
                    CanonicalWindowSize().Width, ApiHeight));
            }
            finally
            {
                e.Graphics.Restore(state);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || _shutdown) return;
            Rectangle refresh = _renderer.RefreshHitBounds(
                new Rectangle(Point.Empty, _renderer.PreferredSize));
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
            if (message.Msg == WM_MOUSEACTIVATE)
            {
                message.Result = (IntPtr)MA_NOACTIVATE;
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
                Rectangle refresh = _renderer.RefreshHitBounds(
                    new Rectangle(Point.Empty, _renderer.PreferredSize));
                refresh = ScaleToClient(refresh);
                message.Result = (IntPtr)(refresh.Contains(client)
                    ? HTCLIENT : HTCAPTION);
                return;
            }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                BeginShutdown();
                _animationTimer.Dispose();
                _displayTimer.Dispose();
                _positionTimer.Dispose();
                _apiFont.Dispose();
                _apiGoFont.Dispose();
                _renderer.Dispose();
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
                UpdateApiSnapshot();
                Invalidate();
            });
        }

        private void OnOpenCodeGoSourceChanged()
        {
            RunOnUiThread(delegate
            {
                UpdateApiSnapshot();
                Invalidate();
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
            _snapshot = _source.Quota ?? QuotaSnapshot.EmptyStale();
            _refreshing = false;
            _animationTimer.Stop();
            Invalidate();
        }

        private void UpdateApiSnapshot()
        {
            if (_shutdown || IsDisposed) return;
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
            UpdateNetworkSnapshot(snapshot);
            Invalidate();
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
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            ClientSize = target;
            MinimumSize = Size;
            MaximumSize = Size;
        }

        private Size CanonicalWindowSize()
        {
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
            if (_shutdown && IsDisposed) return;
            try
            {
                _config.QuotaHudX = Left;
                _config.QuotaHudY = Top;
                _config.Save();
            }
            catch (Exception) { }
        }

        private void UpdateWindowRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath path = new GraphicsPath())
            {
                Size standard = CanonicalWindowSize();
                float sx = ClientSize.Width / (float)standard.Width;
                float sy = ClientSize.Height / (float)standard.Height;
                int radius = Math.Max(1, (int)Math.Round(13f * Math.Min(sx, sy)));
                radius = Math.Min(radius, Math.Min(Width, Height) / 2);
                int diameter = Math.Max(2, radius * 2);
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(Width - diameter, Height - diameter,
                    diameter, diameter, 0, 90);
                path.AddArc(0, Height - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        private void DrawApiBalance(Graphics graphics, Rectangle bounds)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush panel = new SolidBrush(Color.FromArgb(18, 22, 30)))
            using (Brush text = new SolidBrush(Color.FromArgb(245, 247, 251)))
            using (Brush dot = new SolidBrush(_apiStale
                ? Color.FromArgb(145, 151, 164)
                : _apiAvailable || _apiUnlimited
                    ? Color.FromArgb(118, 255, 167)
                    : Color.FromArgb(255, 135, 105)))
            {
                int contentWidth = Math.Max(1, (bounds.Width - 8) / 2);
                Rectangle apiBounds = new Rectangle(bounds.X, bounds.Y, contentWidth, bounds.Height);
                Rectangle networkBounds = new Rectangle(apiBounds.Right + 8, bounds.Y,
                    Math.Max(1, bounds.Right - apiBounds.Right - 8), bounds.Height);
                FillRounded(graphics, panel,
                    new Rectangle(apiBounds.X + 1, apiBounds.Y + 1,
                        Math.Max(1, apiBounds.Width - 2), Math.Max(1, apiBounds.Height - 2)), 12);
                FillRounded(graphics, panel,
                    new Rectangle(networkBounds.X + 1, networkBounds.Y + 1,
                        Math.Max(1, networkBounds.Width - 2), Math.Max(1, networkBounds.Height - 2)), 12);
                graphics.FillEllipse(dot, apiBounds.Right - 16, apiBounds.Top + 17, 6, 6);
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Near;
                    format.LineAlignment = _apiIsOpenCodeGo
                        ? StringAlignment.Near : StringAlignment.Center;
                    Font apiFont = _apiIsOpenCodeGo ? _apiGoFont : _apiFont;
                    graphics.DrawString(_apiDisplayText ?? "API · --", apiFont, text,
                        new Rectangle(apiBounds.X + 10, apiBounds.Top + 4,
                            apiBounds.Width - 30, _apiIsOpenCodeGo
                                ? apiBounds.Height - 5 : apiBounds.Height - 8), format);
                    format.Alignment = StringAlignment.Center;
                    graphics.DrawString(_networkDisplayText ?? "↑-- ↓--", _apiFont, text,
                        new Rectangle(networkBounds.X + 6, networkBounds.Top + 4,
                            networkBounds.Width - 12, networkBounds.Height - 8), format);
                }
            }
        }

        private static void FillRounded(Graphics graphics, Brush brush,
            Rectangle rectangle, int radius)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            int safeRadius = Math.Max(1,
                Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2));
            using (GraphicsPath path = new GraphicsPath())
            {
                int diameter = safeRadius * 2;
                path.AddArc(rectangle.Left, rectangle.Top,
                    diameter, diameter, 180, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Top,
                    diameter, diameter, 270, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter,
                    diameter, diameter, 0, 90);
                path.AddArc(rectangle.Left, rectangle.Bottom - diameter,
                    diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }
}
