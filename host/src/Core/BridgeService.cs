using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexToolsHost.Actions;
using CodexToolsHost.Mijia;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;

namespace CodexToolsHost.Core
{
    /// <summary>桥接服务：串口设备 <-> Codex/DeepSeek 数据源，执行按键/编码器动作</summary>
    public sealed class BridgeService : IDisposable, IHostService
    {
        private readonly AppConfig _config;
        private readonly IDeviceLink _link;
        private readonly ICodexStatusSource _codex;
        private readonly DesktopLogStatusMonitor _status;
        private readonly IDeepSeekSource _deepseek;
        private readonly IOpenCodeGoQuotaSource _openCodeGo;
        private readonly PcMonitorService _pcMonitor;
        private readonly DeepSeekConfigManager _deepSeekConfig;
        private readonly IChatGptRestartService _chatGptRestart;
        private readonly MijiaService _mijia;
        private Timer _pushTimer;
        private volatile bool _dirty = true;
        private volatile bool _started;
        private long _lastPushMs;
        private int _oledPage;
        private int _firmwareMajor;
        private int _firmwareMinor;
        private string _appliedSerialPort;
        private readonly object _pageSync = new object();
        private const int OledPageCooldownMs = 500;
        private int _lastOledPageChangeMs;
        private bool _hasOledPageChange;

        public event Action<string> StatusChanged;
        public event Action<string> Balloon;
        public event Action ShowSettingsRequested;
        public event Action DeviceConnected;
        public event Action DeviceDisconnected;
        public event Action<PcMetricsSnapshot> PcMetricsChanged;

        public BridgeService(AppConfig config)
            : this(config, new SerialLink(config.SerialPort),
                   CodexStatusProvider.CreateDefault(config.CodexRefreshSeconds),
                   new DesktopLogStatusMonitor(),
                   new ApiBalanceProvider(config.DeepSeekApiKey, config.DeepSeekBaseUrl,
                                          config.DeepSeekRefreshSeconds, config.ApiBalanceProvider),
                   new PcMonitorService(new WindowsPcMetricsProvider(), 2000), null, null,
                   new OpenCodeGoQuotaProvider(config.OpenCodeGoApiKey,
                       config.OpenCodeGoRefreshSeconds))
        {
        }

        internal BridgeService(AppConfig config, IDeviceLink link,
                               ICodexStatusSource codex, IDeepSeekSource deepseek)
            : this(config, link, codex, null, deepseek,
                   new PcMonitorService(new WindowsPcMetricsProvider(), 2000), null, null)
        {
        }

        internal BridgeService(AppConfig config, IDeviceLink link,
                               ICodexStatusSource codex, DesktopLogStatusMonitor status,
                               IDeepSeekSource deepseek)
            : this(config, link, codex, status, deepseek,
                   new PcMonitorService(new WindowsPcMetricsProvider(), 2000), null, null)
        {
        }

        internal BridgeService(AppConfig config, IDeviceLink link,
                               ICodexStatusSource codex, DesktopLogStatusMonitor status,
                               IDeepSeekSource deepseek, PcMonitorService pcMonitor)
            : this(config, link, codex, status, deepseek, pcMonitor, null, null)
        {
        }

        internal BridgeService(AppConfig config, IDeviceLink link,
                               ICodexStatusSource codex, DesktopLogStatusMonitor status,
                               IDeepSeekSource deepseek, PcMonitorService pcMonitor,
                               DeepSeekConfigManager deepSeekConfig)
            : this(config, link, codex, status, deepseek, pcMonitor,
                   deepSeekConfig, null)
        {
        }

        internal BridgeService(AppConfig config, IDeviceLink link,
                               ICodexStatusSource codex, DesktopLogStatusMonitor status,
                               IDeepSeekSource deepseek, PcMonitorService pcMonitor,
                               DeepSeekConfigManager deepSeekConfig,
                               IChatGptRestartService chatGptRestart)
            : this(config, link, codex, status, deepseek, pcMonitor,
                   deepSeekConfig, chatGptRestart, null)
        {
        }

        internal BridgeService(AppConfig config, IDeviceLink link,
                               ICodexStatusSource codex, DesktopLogStatusMonitor status,
                               IDeepSeekSource deepseek, PcMonitorService pcMonitor,
                               DeepSeekConfigManager deepSeekConfig,
                               IChatGptRestartService chatGptRestart,
                               IOpenCodeGoQuotaSource openCodeGo)
        {
            if (pcMonitor == null) throw new ArgumentNullException("pcMonitor");
            _config = config;
            _link = link;
            _codex = codex;
            _status = status;
            _deepseek = deepseek;
            _openCodeGo = openCodeGo ?? new OpenCodeGoQuotaProvider(
                config.OpenCodeGoApiKey, config.OpenCodeGoRefreshSeconds);
            _pcMonitor = pcMonitor;
            _deepSeekConfig = deepSeekConfig ?? new DeepSeekConfigManager();
            _chatGptRestart = chatGptRestart ?? new ChatGptRestartService();
            _mijia = new MijiaService(config.Mijia);
            _oledPage = config.DefaultOledPage;
            _appliedSerialPort = SerialPortSelection.Normalize(config.SerialPort);
            _config.SerialPort = _appliedSerialPort;
        }

        public bool IsDeviceConnected { get { return _link.IsConnected; } }
        public string DevicePort { get { return _link.PortName; } }
        public ICodexStatusSource Codex { get { return _codex; } }
        public IDeepSeekSource DeepSeek { get { return _deepseek; } }
        public IOpenCodeGoQuotaSource OpenCodeGo { get { return _openCodeGo; } }
        public DeepSeekConfigManager DeepSeekConfig { get { return _deepSeekConfig; } }
        public PcMetricsSnapshot PcMetrics { get { return _pcMonitor.Current; } }
        public MijiaService Mijia { get { return _mijia; } }

        public void ReconnectDevice()
        {
            ReconnectDevice(_config.SerialPort);
        }

        public void ReconnectDevice(string portName)
        {
            string normalized = SerialPortSelection.Normalize(portName);
            _config.SerialPort = normalized;
            _appliedSerialPort = normalized;
            _link.ConfigurePort(normalized);
            _link.Reconnect();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            _link.Connected += OnDeviceConnected;
            _link.Disconnected += OnDeviceDisconnected;
            _link.FrameReceived += OnFrame;
            _link.StatusChanged += RaiseStatus;
            _codex.Changed += delegate { _dirty = true; RaiseStatus(BuildSummaryText()); };
            if (_status != null)
            {
                _status.Changed += delegate { _dirty = true; RaiseStatus(BuildSummaryText()); };
                _status.StatusChanged += delegate { _dirty = true; RaiseStatus(BuildSummaryText()); };
            }
            _deepseek.Changed += delegate { _dirty = true; RaiseStatus(BuildSummaryText()); };
            _openCodeGo.Changed += delegate { _dirty = true; RaiseStatus(BuildSummaryText()); };
            _pcMonitor.Changed += OnPcMetricsChanged;

            _link.Start();
            _pcMonitor.Start();
            try { ObserveBackground(_codex.StartAsync(), "Codex 连接失败"); }
            catch (Exception ex) { RaiseStatus("Codex 连接失败: " + ex.Message); }
            if (_status != null)
            {
                try { ObserveBackground(_status.StartAsync(), "Codex 状态监控启动失败"); }
                catch (Exception) { }
            }
            StartSelectedQuotaSource();
            _mijia.Start();

            _pushTimer = new Timer(delegate { SafePushTick(); }, null, 1000, 1000);
        }

        private void SafePushTick()
        {
            try { PushTick(); }
            catch (Exception ex) { RaiseStatus("设备状态推送失败: " + ex.Message); }
        }

        private void OnDeviceConnected()
        {
            _dirty = true;
            SendInitial();
            RaiseStatus(BuildSummaryText());
            var balloon = Balloon;
            if (balloon != null) balloon("设备已连接: " + _link.PortName);
            var connected = DeviceConnected;
            if (connected != null) connected();
        }

        private void OnDeviceDisconnected()
        {
            RaiseStatus(BuildSummaryText());
            var balloon = Balloon;
            if (balloon != null) balloon("设备已断开，正在自动重连…");
            var disconnected = DeviceDisconnected;
            if (disconnected != null) disconnected();
        }

        private void PushTick()
        {
            if (!_link.IsConnected) return;
            long now = Environment.TickCount;
            if (!_dirty && now - _lastPushMs < 2000) return; /* 2s 心跳，防设备误判离线 */
            _dirty = false;
            _lastPushMs = now;
            SendStatus();
        }

        private void OnPcMetricsChanged(PcMetricsSnapshot snapshot)
        {
            _dirty = true;
            var handler = PcMetricsChanged;
            try { if (handler != null) handler(snapshot); }
            catch (Exception ex) { RaiseStatus("PC 监控通知失败: " + ex.Message); }
        }

        private void SendInitial()
        {
            /* 主机可能晚于 MCU 启动，主动请求 INFO，避免错过设备上电时的一次性上报。 */
            _link.Send(FrameCodec.MsgCfgReq, new byte[0]);
            SendRgb(_config.Rgb.Mode);
            SendRgbStatusColors();
            _link.Send(FrameCodec.MsgOledCfg, new byte[] { (byte)_config.OledAddress, (byte)_config.OledDriver });
            _oledPage = Math.Max(0, Math.Min(2, _config.DefaultOledPage));
            _link.Send(FrameCodec.MsgOledPage, new byte[] { (byte)_oledPage, 0 });
            _link.Send(FrameCodec.MsgOledText, EncodeText(0, "CodeX Tools Host"));
            _link.Send(FrameCodec.MsgOledText, EncodeText(1, "https://codex.openai.com"));
            SendStatus();
        }

        private void SendStatus()
        {
            if (!_link.IsConnected) return;
            var payload = new List<byte>();
            payload.Add((byte)Math.Max(0, Math.Min(5, _status == null ? _codex.State : _status.State)));
            payload.Add(_deepseek.Unlimited ? (byte)0x01 : (byte)0); /* flags bit0: API 无限额度 */
            payload.Add((byte)(_codex.Quota.PrimaryRemainingPercent.HasValue ? Math.Max(0, Math.Min(100, _codex.Quota.PrimaryRemainingPercent.Value)) : 255));
            payload.Add((byte)(_codex.Quota.SecondaryRemainingPercent.HasValue ? Math.Max(0, Math.Min(100, _codex.Quota.SecondaryRemainingPercent.Value)) : 255));
            payload.AddRange(BitConverter.GetBytes((uint)ToUnix(_codex.Quota.PrimaryResetsAt)));
            payload.AddRange(BitConverter.GetBytes((uint)ToUnix(_codex.Quota.SecondaryResetsAt)));
            payload.Add((byte)(_deepseek.Available && !_deepseek.IsStale ? 1 : 0));
            payload.AddRange(BitConverter.GetBytes((uint)Math.Max(0, _deepseek.BalanceCents)));
            string currencyName = _deepseek.Currency ?? "CNY";
            if (currencyName.Length > 3) currencyName = currencyName.Substring(0, 3);
            byte[] currency = Encoding.ASCII.GetBytes(currencyName + "\0");
            payload.AddRange(currency);
            string text = SanitizeAscii(_status == null ? _codex.StatusText : _status.StatusText, 47);
            payload.Add((byte)text.Length);
            payload.AddRange(Encoding.ASCII.GetBytes(text));
            _link.Send(FrameCodec.MsgStatus, payload.ToArray());
            SendPcMetrics();
        }

        private void SendPcMetrics()
        {
            if (!_link.IsConnected) return;
            /* 当前固件统一接收一个 28 字节 v3 帧，不再依赖版本握手决定是否发送。 */
            _link.Send(FrameCodec.MsgPcMetrics, PcMetricsProtocol.EncodeV3(_pcMonitor.Current));
        }

        private void OnFrame(byte type, byte[] payload)
        {
            switch (type)
            {
                case FrameCodec.MsgEvtButton:
                    HandleButtonEvent(payload);
                    break;
                case FrameCodec.MsgEvtEncoder:
                    HandleEncoderEvent(payload);
                    break;
                case FrameCodec.MsgInfo:
                    HandleInfo(payload);
                    break;
                case FrameCodec.MsgPong:
                    break;
                case FrameCodec.MsgAck:
                    break;
            }
        }

        private void HandleButtonEvent(byte[] payload)
        {
            if (payload == null || payload.Length < 3) return;
            byte index = payload[0];
            byte kind = payload[1];
            string kindName;
            switch (kind)
            {
                case 3: kindName = "click"; break;
                case 4: kindName = "double"; break;
                case 5: kindName = "long"; break;
                default: return;
            }

            string key;
            if (index >= 8)
                key = "enc_" + kindName;
            else
                key = "btn" + index.ToString(CultureInfo.InvariantCulture) + "_" + kindName;

            ActionSpec spec;
            if (_config.Bindings != null && _config.Bindings.TryGetValue(key, out spec))
                ActionDispatcher.Execute(spec, this);
        }

        private void HandleEncoderEvent(byte[] payload)
        {
            if (payload == null || payload.Length < 1) return;
            sbyte delta = unchecked((sbyte)payload[0]);
            if (delta == 0) return;
            string action = (_config.EncoderRotate == null ? "volume" : _config.EncoderRotate.Action);
            string param = _config.EncoderRotate == null ? "1" : _config.EncoderRotate.Param;
            int abs = Math.Abs((int)delta);
            for (int i = 0; i < abs; i++)
                ActionDispatcher.Execute(new ActionSpec(action, param), this);
        }

        private void HandleInfo(byte[] payload)
        {
            if (payload == null || payload.Length < 9) return;
            _firmwareMajor = payload[0];
            _firmwareMinor = payload[1];
            string model = "unknown";
            if (payload.Length > 8)
                model = Encoding.ASCII.GetString(payload, 8, Math.Min(16, payload.Length - 8)).TrimEnd('\0', ' ');
            string oledText = "OLED:?";
            if (payload.Length >= 5)
            {
                byte st = payload[4];
                string addr = (st & 0x02) != 0 ? "0x3D" : (st & 0x04) != 0 ? "0x3C" : "无";
                string drv = (st & 0x08) != 0 ? "SH1106" : "SSD1306";
                oledText = (st & 0x01) != 0 ? "OLED:" + addr + "/" + drv : "OLED:未检测到";
            }
            RaiseStatus("设备: " + model + " (FW " + payload[0] + "." + payload[1] + ") " + oledText);
            SendPcMetrics();
        }

        /* ---------- IHostService ---------- */

        public void CycleOledPage()
        {
            int page;
            lock (_pageSync)
            {
                int now = Environment.TickCount;
                if (_hasOledPageChange &&
                    (int)(now - _lastOledPageChangeMs) < OledPageCooldownMs)
                    return;
                _hasOledPageChange = true;
                _lastOledPageChangeMs = now;
                _oledPage = (_oledPage + 1) % 3;
                page = _oledPage;
            }
            _link.Send(FrameCodec.MsgOledPage, new byte[] { (byte)page, 0 });
        }

        public void SetOledPage(int page)
        {
            page = Math.Max(0, Math.Min(2, page));
            lock (_pageSync)
            {
                _oledPage = page;
                _hasOledPageChange = true;
                _lastOledPageChangeMs = Environment.TickCount;
            }
            _link.Send(FrameCodec.MsgOledPage, new byte[] { (byte)page, 0 });
        }

        public void SetRgbMode(string mode)
        {
            SendRgb(mode);
            SendRgbStatusColors();
            _config.Rgb.Mode = mode;
        }

        public void RefreshQuota()
        {
            try { ObserveBackground(_codex.RefreshQuotaAsync(), "Codex 额度刷新失败"); }
            catch (Exception) { }
            try
            {
                if (_status != null)
                    ObserveBackground(_status.RefreshQuotaAsync(), "Codex 状态刷新失败");
            }
            catch (Exception) { }
            try
            {
                if (IsOpenCodeGoSelected()) _openCodeGo.Fetch();
                else _deepseek.Fetch();
            }
            catch (Exception) { }
            _dirty = true;
        }

        public void ShowSettings()
        {
            var handler = ShowSettingsRequested;
            if (handler != null) handler();
        }

        public void ToggleDeepSeek()
        {
            DeepSeekActionResult result = _deepSeekConfig.Toggle();
            if (result == null) return;
            Toast(result.Message);
            RaiseStatus(result.Message);
        }

        public void RestartChatGpt()
        {
            int state = _status == null ? _codex.State : _status.State;
            _chatGptRestart.RestartAsync(state, Toast);
        }

        public void RunMijiaShortcut(int index)
        {
            Task execution = Task.Run(async delegate
            {
                MijiaShortcutExecutionResult result = await _mijia
                    .ExecuteShortcutWithResultAsync(index);
                if (!result.Succeeded && !string.IsNullOrEmpty(result.Message))
                    Toast(result.Message);
            });
            ObserveBackground(execution, "米家场景执行失败");
        }

        public void Toast(string message)
        {
            var balloon = Balloon;
            if (balloon != null) balloon(message);
        }

        private void ObserveBackground(Task task, string failurePrefix)
        {
            if (task == null) return;
            task.ContinueWith(delegate(Task failed)
            {
                AggregateException error = failed.Exception;
                string message = error == null ? failurePrefix : failurePrefix + ": "
                    + error.GetBaseException().Message;
                RaiseStatus(message);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /* ---------- 内部工具 ---------- */

        private void SendRgb(string mode)
        {
            if (!_link.IsConnected) return;
            byte m;
            switch ((mode ?? "status").ToLowerInvariant())
            {
                case "off": m = 0; break;
                case "solid": m = 1; break;
                case "breath": m = 2; break;
                case "rainbow": m = 3; break;
                case "wave": m = 5; break;
                case "blink": m = 6; break;
                case "test": m = 7; break;
                default: m = 4; break;
            }
            var payload = new List<byte>();
            payload.Add(m);
            payload.Add((byte)Math.Max(1, Math.Min(16, _config.Rgb.Count)));
            payload.Add((byte)Math.Max(0, Math.Min(255, _config.Rgb.Brightness)));
            payload.AddRange(BitConverter.GetBytes((ushort)Math.Max(100, _config.Rgb.PeriodMs)));
            payload.AddRange(ParseColor(_config.Rgb.Color1));
            payload.AddRange(ParseColor(_config.Rgb.Color2));
            _link.Send(FrameCodec.MsgRgbSet, payload.ToArray());
        }

        private void SendRgbStatusColors()
        {
            if (!_link.IsConnected) return;
            var payload = new List<byte>();
            payload.AddRange(ParseColor(_config.Rgb.StatusIdle));
            payload.AddRange(ParseColor(_config.Rgb.StatusRunning));
            payload.AddRange(ParseColor(_config.Rgb.StatusWaiting));
            payload.AddRange(ParseColor(_config.Rgb.StatusError));
            payload.AddRange(ParseColor(_config.Rgb.StatusComplete));
            payload.AddRange(ParseColor(_config.Rgb.StatusOffline));
            _link.Send(FrameCodec.MsgRgbStatus, payload.ToArray());
        }

        public void ApplyDeviceConfig()
        {
            string requestedPort = SerialPortSelection.Normalize(_config.SerialPort);
            bool portChanged = !string.Equals(requestedPort, _appliedSerialPort,
                StringComparison.OrdinalIgnoreCase);
            _config.SerialPort = requestedPort;
            if (portChanged)
            {
                _link.ConfigurePort(requestedPort);
                _appliedSerialPort = requestedPort;
                _link.Reconnect();
            }
            else
            {
                SendInitial();
            }
            _dirty = true;
            _codex.Stop();
            try { ObserveBackground(_codex.StartAsync(), "Codex 连接失败"); }
            catch (Exception) { }
            if (_status != null)
            {
                _status.Stop();
                try { ObserveBackground(_status.StartAsync(), "Codex 状态监控启动失败"); }
                catch (Exception) { }
            }
            _deepseek.Stop();
            _openCodeGo.Stop();
            _deepseek.UpdateCredentials(_config.DeepSeekApiKey, _config.DeepSeekBaseUrl,
                                         _config.DeepSeekRefreshSeconds, _config.ApiBalanceProvider);
            _openCodeGo.UpdateCredentials(_config.OpenCodeGoApiKey,
                _config.OpenCodeGoRefreshSeconds);
            if (_started) StartSelectedQuotaSource();
            _mijia.ApplySettings();
        }

        private bool IsOpenCodeGoSelected()
        {
            return string.Equals(
                AppConfig.NormalizeQuotaDisplaySource(_config.QuotaDisplaySource),
                "opencodego", StringComparison.OrdinalIgnoreCase);
        }

        private void StartSelectedQuotaSource()
        {
            _deepseek.Stop();
            _openCodeGo.Stop();
            if (IsOpenCodeGoSelected()) _openCodeGo.Start();
            else _deepseek.Start();
        }

        private static byte[] ParseColor(string color)
        {
            try
            {
                string c = (color ?? "#000000").TrimStart('#');
                if (c.Length == 6)
                    return new byte[] {
                        byte.Parse(c.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(c.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(c.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) };
            }
            catch (Exception) { }
            return new byte[] { 0, 0, 0 };
        }

        private static byte[] EncodeText(byte slot, string text)
        {
            text = SanitizeAscii(text, 60);
            var payload = new List<byte>();
            payload.Add(slot);
            payload.Add((byte)text.Length);
            payload.AddRange(Encoding.ASCII.GetBytes(text));
            return payload.ToArray();
        }

        private static string SanitizeAscii(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder();
            foreach (char ch in text)
            {
                if (ch >= 0x20 && ch <= 0x7E) sb.Append(ch);
                else sb.Append(' '); /* OLED 只支持 ASCII：非 ASCII 转空格，避免 ??? */
            }
            if (sb.Length > max) return sb.ToString(0, max);
            return sb.ToString();
        }

        private static long ToUnix(DateTimeOffset? value)
        {
            if (!value.HasValue) return 0;
            try { return value.Value.ToUnixTimeSeconds(); }
            catch (Exception) { return 0; }
        }

        public string BuildSummaryText()
        {
            string device = _link.IsConnected ? (_link.PortName ?? "COM?") : "无设备";
            string codex;
            int state = _status == null ? _codex.State : _status.State;
            switch (state)
            {
                case 1: codex = "空闲"; break;
                case 2: codex = "运行中"; break;
                case 3: codex = "等待中"; break;
                case 4: codex = "错误"; break;
                case 5: codex = "已完成"; break;
                default: codex = "离线"; break;
            }
            string quota = _codex.Quota.PrimaryRemainingPercent.HasValue
                ? _codex.Quota.PrimaryRemainingPercent.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : "不可用";
            string api = "不可用";
            if (IsOpenCodeGoSelected())
            {
                OpenCodeGoQuotaSnapshot go = _openCodeGo.Quota;
                if (!go.IsStale)
                {
                    api = "Go " + FormatGoWindow("5h", go.Rolling) + " "
                        + FormatGoWindow("7d", go.Weekly) + " "
                        + FormatGoWindow("月", go.Monthly);
                }
                else if (go.Rolling.RemainingPercent.HasValue
                    || go.Weekly.RemainingPercent.HasValue
                    || go.Monthly.RemainingPercent.HasValue)
                {
                    api = "Go " + FormatGoWindow("5h", go.Rolling) + " "
                        + FormatGoWindow("7d", go.Weekly) + " "
                        + FormatGoWindow("月", go.Monthly) + " STALE";
                }
            }
            else if (_deepseek.Available && !_deepseek.IsStale)
                api = _deepseek.Unlimited ? "无限" :
                    (_deepseek.BalanceCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
            return "设备 " + device + " | Codex " + codex + " " + quota + " | API " + api;
        }

        private static string FormatGoWindow(string label, OpenCodeGoQuotaWindow window)
        {
            if (window != null && window.IsRateLimited) return label + "已达上限";
            if (window == null || !window.RemainingPercent.HasValue) return label + "余--%";
            return label + "余" + window.RemainingPercent.Value.ToString(
                CultureInfo.InvariantCulture) + "%";
        }

        private void RaiseStatus(string text)
        {
            var handler = StatusChanged;
            try { if (handler != null) handler(text); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_pushTimer != null) _pushTimer.Dispose();
            _codex.Dispose();
            if (_status != null) _status.Dispose();
            _deepseek.Dispose();
            _openCodeGo.Dispose();
            _mijia.Dispose();
            _pcMonitor.Dispose();
            _link.Dispose();
        }
    }
}
