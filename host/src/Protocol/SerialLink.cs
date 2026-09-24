using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace CodexToolsHost.Protocol
{
    /// <summary>CDC 串口链路：自动探测、断线重连、帧收发</summary>
    public sealed class SerialLink : IDeviceLink
    {
        private const int HeartbeatPeriodMs = 2500;
        private const int HeartbeatMissLimit = 2;
        private readonly object _sync = new object();
        private string _preferredPort;
        private string _lastVerifiedPort;
        private SerialPort _port;
        private readonly List<byte> _rxBuffer = new List<byte>();
        private Timer _scanTimer;
        private Timer _pingTimer;
        private volatile bool _disposed;
        private int _scanInProgress;
        private int _scanGeneration;
        private int _connectionEpoch;
        private bool _verified;
        private bool _gotPong;
        private byte[] _expectedPong;
        private readonly SerialHeartbeatTracker _heartbeat =
            new SerialHeartbeatTracker(HeartbeatMissLimit);
        private readonly Dictionary<string, DateTime> _failedPorts = new Dictionary<string, DateTime>();

        public event Action Connected;
        public event Action Disconnected;
        public event Action<byte, byte[]> FrameReceived;
        public event Action<string> StatusChanged;

        public SerialLink()
            : this(null)
        {
        }

        public SerialLink(string preferredPort)
        {
            _preferredPort = SerialPortSelection.Normalize(preferredPort);
        }

        public string PortName { get; private set; }
        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _verified && _port != null && IsPortOpen(_port);
                }
            }
        }

        public void ConfigurePort(string portName)
        {
            string normalized = SerialPortSelection.Normalize(portName);
            lock (_sync)
            {
                _preferredPort = normalized;
                Interlocked.Increment(ref _scanGeneration);
            }
            lock (_failedPorts) _failedPorts.Clear();
        }

        public void Start()
        {
            if (_scanTimer != null) return;
            _scanTimer = new Timer(delegate { ScanForDevice(); }, null, 500, 2000);
            _pingTimer = new Timer(delegate { PingOnce(); }, null,
                HeartbeatPeriodMs, HeartbeatPeriodMs);
        }

        /* 断开当前连接并立即触发一次扫描（自动重连仍由定时器兜底） */
        public void Reconnect()
        {
            if (_disposed) return;
            Interlocked.Increment(ref _scanGeneration);
            HandleDisconnect(null, true);
            lock (_failedPorts) _failedPorts.Clear();
            RaiseStatus("正在重新搜索设备…");
            RequestScan(300);
        }

        public bool Send(byte type, byte[] payload)
        {
            byte[] frame;
            try { frame = FrameCodec.Encode(type, payload); }
            catch (ArgumentException) { return false; }

            SerialPort failedPort = null;
            lock (_sync)
            {
                if (_port == null || !_verified || !IsPortOpen(_port)) return false;
                try
                {
                    _port.Write(frame, 0, frame.Length);
                    return true;
                }
                catch (Exception)
                {
                    failedPort = _port;
                }
            }
            if (failedPort != null) HandleDisconnect(failedPort, true);
            return false;
        }

        private void ScanForDevice()
        {
            if (Interlocked.Exchange(ref _scanInProgress, 1) != 0) return;
            int generation = Interlocked.CompareExchange(ref _scanGeneration, 0, 0);
            try { ScanForDeviceCore(generation); }
            catch (Exception ex) { RaiseStatus("串口扫描失败: " + ex.Message); }
            finally { Interlocked.Exchange(ref _scanInProgress, 0); }
        }

        private void ScanForDeviceCore(int generation)
        {
            SerialPort stalePort = null;
            lock (_sync)
            {
                if (_port != null && !IsPortOpen(_port)) stalePort = _port;
            }
            if (stalePort != null) HandleDisconnect(stalePort, true);
            if (!IsScanCurrent(generation) || IsConnected) return;
            string[] ports;
            try { ports = SerialPort.GetPortNames(); }
            catch (Exception) { return; }
            if (!IsScanCurrent(generation)) return;

            string preferred;
            string lastVerified;
            lock (_sync)
            {
                preferred = _preferredPort;
                lastVerified = _lastVerifiedPort;
            }

            if (!SerialPortSelection.IsAuto(preferred))
            {
                string actualPort;
                if (SerialPortSelection.TryFindPort(ports, preferred, out actualPort)
                    && TryOpenAndVerify(actualPort, generation)
                    && IsConnectedOnPort(actualPort, generation))
                {
                    RaiseStatus("已连接 " + actualPort);
                    var handler = Connected;
                    try { if (handler != null) handler(); }
                    catch (Exception ex) { RaiseStatus("连接后处理失败: " + ex.Message); }
                }
                return;
            }

            foreach (string port in SerialPortSelection.OrderCandidates(ports, lastVerified))
            {
                if (!IsScanCurrent(generation) || IsConnected) return;
                if (IsCurrentPortName(port)) continue;
                lock (_failedPorts)
                {
                    DateTime until;
                    if (_failedPorts.TryGetValue(port, out until) && DateTime.UtcNow < until)
                        continue;
                }
                if (TryOpenAndVerify(port, generation)
                    && IsConnectedOnPort(port, generation))
                {
                    RaiseStatus("已连接 " + port);
                    var handler = Connected;
                    try { if (handler != null) handler(); }
                    catch (Exception ex) { RaiseStatus("连接后处理失败: " + ex.Message); }
                    return;
                }
            }
        }

        private bool TryOpenAndVerify(string port, int generation)
        {
            SerialPort candidate = null;
            bool opened = false;
            try
            {
                ThrowIfScanStale(generation);
                candidate = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 200,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true
                };
                candidate.Open();
                opened = true;
                candidate.DataReceived += OnDataReceived;

                lock (_sync)
                {
                    if (_disposed
                        || Interlocked.CompareExchange(ref _scanGeneration, 0, 0) != generation
                        || _port != null)
                        throw new OperationCanceledException();
                    _port = candidate;
                    _verified = false;
                    _connectionEpoch++;
                    _rxBuffer.Clear();
                    PortName = null;
                }

                /* 多次短握手：USB CDC 首次打开可能因 DTR 复位而延迟响应。 */
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    ThrowIfScanStale(generation, candidate);
                    try { candidate.DiscardInBuffer(); } catch (Exception) { }
                    byte[] ts = BitConverter.GetBytes((uint)Environment.TickCount);
                    if (BitConverter.IsLittleEndian) Array.Reverse(ts);
                    lock (_sync)
                    {
                        ThrowIfScanStaleLocked(generation, candidate);
                        _expectedPong = (byte[])ts.Clone();
                        _gotPong = false;
                        byte[] frame = FrameCodec.Encode(FrameCodec.MsgPing, ts);
                        candidate.Write(frame, 0, frame.Length);
                    }
                    for (int i = 0; i < 10; i++)
                    {
                        ThrowIfScanStale(generation, candidate);
                        lock (_sync)
                        {
                            if (_gotPong) break;
                        }
                        Thread.Sleep(50);
                    }
                    lock (_sync)
                    {
                        if (IsScanCurrentLocked(generation)
                            && ReferenceEquals(_port, candidate)
                            && IsPortOpen(candidate)
                            && _gotPong)
                        {
                            _verified = true;
                            PortName = port;
                            _lastVerifiedPort = port;
                            _expectedPong = null;
                            _gotPong = false;
                            _heartbeat.Reset();
                            lock (_failedPorts) _failedPorts.Remove(port);
                            return true;
                        }
                    }
                }
                throw new InvalidOperationException("未收到设备应答");
            }
            catch (OperationCanceledException)
            {
                CleanupCandidate(candidate);
                return false;
            }
            catch (Exception)
            {
                bool candidateOpen = false;
                candidateOpen = IsPortOpen(candidate);
                if (!candidateOpen)
                {
                    /* 打开失败（幽灵/占用端口）：短暂跳过，避免阻塞扫描。 */
                    lock (_failedPorts) { _failedPorts[port] = DateTime.UtcNow.AddSeconds(5); }
                }
                else if (opened)
                {
                    /* 已打开但设备尚未响应：允许下一轮快速重试。 */
                    lock (_failedPorts) { _failedPorts[port] = DateTime.UtcNow.AddSeconds(2); }
                }
                CleanupCandidate(candidate);
                return false;
            }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort port = sender as SerialPort;
            if (port == null) return;
            try
            {
                var frames = new List<KeyValuePair<byte, byte[]>>();
                int connectionEpoch;
                lock (_sync)
                {
                    if (!ReferenceEquals(_port, port)) return;
                    connectionEpoch = _connectionEpoch;
                    int available = port.BytesToRead;
                    if (available <= 0) return;
                    byte[] chunk = new byte[available];
                    int read = port.Read(chunk, 0, available);
                    if (read <= 0) return;
                    for (int i = 0; i < read; i++) _rxBuffer.Add(chunk[i]);
                    while (true)
                    {
                        int offset = 0;
                        byte type;
                        byte[] frame;
                        bool found = FrameCodec.TryExtract(_rxBuffer.ToArray(),
                            ref offset, out type, out frame);
                        if (offset > 0 && offset <= _rxBuffer.Count)
                            _rxBuffer.RemoveRange(0, offset);
                        if (!found) break;
                        frames.Add(new KeyValuePair<byte, byte[]>(type, frame));
                    }
                }
                foreach (KeyValuePair<byte, byte[]> frame in frames)
                    Dispatch(port, connectionEpoch, frame.Key, frame.Value);
            }
            catch (Exception)
            {
                HandleDisconnect(port, true);
            }
        }

        private void Dispatch(SerialPort sourcePort, int connectionEpoch,
            byte type, byte[] payload)
        {
            bool publish;
            lock (_sync)
            {
                if (!ReferenceEquals(_port, sourcePort)
                    || connectionEpoch != _connectionEpoch)
                    return;
                if (type == FrameCodec.MsgPong)
                {
                    if (IsMatchingPong(_expectedPong, payload)) _gotPong = true;
                    _heartbeat.AcceptPong(payload);
                }
                publish = _verified;
            }
            if (!publish) return;
            var handler = FrameReceived;
            try { if (handler != null) handler(type, payload); }
            catch (Exception ex) { RaiseStatus("设备消息处理失败: " + ex.Message); }
        }

        internal static bool IsMatchingPong(byte[] expected, byte[] actual)
        {
            return SerialHeartbeatTracker.Matches(expected, actual);
        }

        private void PingOnce()
        {
            if (_disposed) return;
            try
            {
                byte[] token = BitConverter.GetBytes((uint)Environment.TickCount);
                if (BitConverter.IsLittleEndian) Array.Reverse(token);
                byte[] frame = FrameCodec.Encode(FrameCodec.MsgPing, token);
                SerialPort activePort = null;
                bool disconnect = false;
                lock (_sync)
                {
                    activePort = _port;
                    if (activePort == null || !_verified || !IsPortOpen(activePort)) return;
                    if (!_heartbeat.RegisterPing(token))
                        disconnect = true;
                    else
                    {
                        try { activePort.Write(frame, 0, frame.Length); }
                        catch (Exception) { disconnect = true; }
                    }
                }
                if (disconnect) HandleDisconnect(activePort, true);
            }
            catch (Exception ex)
            {
                RaiseStatus("串口心跳失败: " + ex.Message);
            }
        }

        private void HandleDisconnect()
        {
            HandleDisconnect(null, true);
        }

        private void HandleDisconnect(SerialPort expectedPort)
        {
            HandleDisconnect(expectedPort, true);
        }

        private void HandleDisconnect(SerialPort expectedPort, bool asyncCleanup)
        {
            SerialPort old = null;
            bool wasVerified = false;
            lock (_sync)
            {
                if (expectedPort != null && !ReferenceEquals(_port, expectedPort)) return;
                old = _port;
                wasVerified = _verified;
                _port = null;
                _verified = false;
                _connectionEpoch++;
                PortName = null;
                _rxBuffer.Clear();
                _expectedPong = null;
                _gotPong = false;
                _heartbeat.Reset();
            }
            if (old == null) return;
            try { old.DataReceived -= OnDataReceived; } catch (Exception) { }
            if (asyncCleanup)
            {
                try
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        CloseAndDispose(old);
                        RequestScan(100);
                    });
                }
                catch (Exception)
                {
                    CloseAndDispose(old);
                    RequestScan(100);
                }
            }
            else CloseAndDispose(old);
            if (wasVerified)
            {
                RaiseStatus("设备已断开，自动重连中…");
                var handler = Disconnected;
                try { if (handler != null) handler(); }
                catch (Exception ex) { RaiseStatus("断线处理失败: " + ex.Message); }
            }
        }

        private void RaiseStatus(string text)
        {
            var handler = StatusChanged;
            try { if (handler != null) handler(text); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _scanGeneration);
            if (_scanTimer != null) _scanTimer.Dispose();
            if (_pingTimer != null) _pingTimer.Dispose();
            HandleDisconnect(null, false);
        }

        private bool IsScanCurrent(int generation)
        {
            return !_disposed
                && Interlocked.CompareExchange(ref _scanGeneration, 0, 0) == generation;
        }

        private bool IsScanCurrentLocked(int generation)
        {
            return !_disposed
                && Interlocked.CompareExchange(ref _scanGeneration, 0, 0) == generation;
        }

        private void ThrowIfScanStale(int generation, SerialPort candidate = null)
        {
            lock (_sync) ThrowIfScanStaleLocked(generation, candidate);
        }

        private void ThrowIfScanStaleLocked(int generation, SerialPort candidate)
        {
            if (!IsScanCurrentLocked(generation)
                || (candidate != null && !ReferenceEquals(_port, candidate)))
                throw new OperationCanceledException();
        }

        private bool IsCurrentPortName(string portName)
        {
            lock (_sync)
            {
                return string.Equals(PortName, portName, StringComparison.OrdinalIgnoreCase)
                    && _verified && IsPortOpen(_port);
            }
        }

        private bool IsConnectedOnPort(string portName, int generation)
        {
            lock (_sync)
            {
                return IsScanCurrentLocked(generation)
                    && string.Equals(PortName, portName, StringComparison.OrdinalIgnoreCase)
                    && _verified && IsPortOpen(_port);
            }
        }

        private void CleanupCandidate(SerialPort candidate)
        {
            if (candidate == null) return;
            lock (_sync)
            {
                if (ReferenceEquals(_port, candidate))
                {
                    _port = null;
                    _verified = false;
                    _connectionEpoch++;
                    PortName = null;
                    _rxBuffer.Clear();
                    _expectedPong = null;
                    _gotPong = false;
                    _heartbeat.Reset();
                }
            }
            try { candidate.DataReceived -= OnDataReceived; } catch (Exception) { }
            CloseAndDispose(candidate);
        }

        private static bool IsPortOpen(SerialPort port)
        {
            try { return port != null && port.IsOpen; }
            catch (Exception) { return false; }
        }

        private void RequestScan(int dueTimeMs)
        {
            if (_disposed) return;
            try
            {
                Timer timer = _scanTimer;
                if (timer == null) Start();
                else timer.Change(Math.Max(0, dueTimeMs), 2000);
            }
            catch (Exception) { }
        }

        private static void CloseAndDispose(SerialPort port)
        {
            if (port == null) return;
            try { port.Close(); } catch (Exception) { }
            try { port.Dispose(); } catch (Exception) { }
        }
    }
}
