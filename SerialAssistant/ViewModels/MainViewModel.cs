using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialAssistant.Services.Interface;
using SerialAssistant.Services.SerialAssistant;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Design;
using System.Data;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SerialAssistant.ViewModels
{

    /// <summary>串口连接状态</summary>
    public enum SerialConnectionState
    {
        /// <summary>未连接</summary>
        Disconnected,
        /// <summary>已连接</summary>
        Connected,
        /// <summary>连接已丢失（设备被拔出 / 读取异常）</summary>
        Lost,
    }

    /// <summary>串口参数预设</summary>
    public sealed class SerialPreset
    {
        public SerialPreset(string name, int baudRate, int dataBits, Parity parity, StopBits stopBits)
        {
            Name = name;
            BaudRate = baudRate;
            DataBits = dataBits;
            Parity = parity;
            StopBits = stopBits;
        }

        public string Name { get; }
        public int BaudRate { get; }
        public int DataBits { get; }
        public Parity Parity { get; }
        public StopBits StopBits { get; }
    }
    public partial class MainViewModel: ObservableObject
    {
        // ==================== 常量 ====================
        private const int UiRefreshIntervalMs = 50;       // UI 批量刷新间隔（20fps）
        private const int MaxBytesPerTick = 64 * 1024;    // 单次消费上限（背压保护）
        private const int MaxDisplayChars = 256 * 1024;   // 显示区字符上限（防内存无限增长）
        private const int MinSendIntervalMs = 10;         // 定时发送最小间隔

        // ==================== 依赖 ====================
        private readonly ISerialPortService _serial = new SerialPortService();
        private readonly LogWriter _logger = new();
        private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

        // ==================== 内部状态 ====================
        private readonly ConcurrentQueue<byte[]> _rxQueue = new();
        private readonly StringBuilder _display = new();
        private readonly DispatcherTimer _uiTimer;
        private bool _opening;
        private bool _suppressLogCallback;

        private CancellationTokenSource? _timeCts;
        private Task? _timeLoop;

        public MainViewModel()
        {
            _serial.DataReceived += OnDataReceived;
            _serial.ErrorOccurred += OnSerialError;
     
            LoadPresets();
            LoadDefaultQuickCommands();
            RefreshPorts();

            _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(UiRefreshIntervalMs),
            };
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();
        }

        // ==================== 选项集合 ====================

        public ObservableCollection<SerialPortInfo> Ports { get; } = new();

        public ObservableCollection<string> BaudRates { get; } = new()
        {
            "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200", "230400",
        };

        public ObservableCollection<int> DataBitsOptions { get; } = new() { 5, 6, 7, 8 };

        public ObservableCollection<string> ParityOptions { get; } = new()
        {
            "None", "Odd", "Even", "Mark", "Space",
        };

        public ObservableCollection<string> StopBitsOptions { get; } = new()
        {
            "1", "1.5", "2",
        };

        public ObservableCollection<string> HandshakeOptions { get; } = new()
        {
            "None", "XOnXOff", "RequestToSend", "RequestToSendXOnXOff",
        };

        public ObservableCollection<SerialPreset> Presets { get; } = new();

        public ObservableCollection<string> QuickCommands { get; } = new();

        // ==================== 端口参数 ====================

        [ObservableProperty]
        private SerialPortInfo? _selectedPort;

        [ObservableProperty]
        private string _selectedBaudRate = "115200";

        [ObservableProperty]
        private int _selectedDataBits = 8;

        [ObservableProperty]
        private string _selectedParity = "None";

        [ObservableProperty]
        private string _selectedStopBits = "1";

        [ObservableProperty]
        private string _selectedHandshake = "None";

        [ObservableProperty]
        private SerialPreset? _selectedPreset;

        // ==================== 连接状态 ====================

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConnected))]
        [NotifyPropertyChangedFor(nameof(CanEditSettings))]
        [NotifyPropertyChangedFor(nameof(OpenButtonText))]
        [NotifyPropertyChangedFor(nameof(ConnectionText))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private SerialConnectionState _connectionState = SerialConnectionState.Disconnected;

        [ObservableProperty]
        private string _statusMessage = "就绪";

        public bool IsConnected => ConnectionState == SerialConnectionState.Connected;

        /// <summary>串口打开后参数不可修改，所以这里反向控制界面的可用性</summary>
        public bool CanEditSettings => ConnectionState != SerialConnectionState.Connected;

        public string OpenButtonText => IsConnected ? "关闭串口" : "打开串口";

        public string ConnectionText => ConnectionState switch
        {
            SerialConnectionState.Connected => "已连接",
            SerialConnectionState.Lost => "设备已断开",
            _ => "未连接",
        };

        // ==================== 接收显示 ====================

        [ObservableProperty]
        private string _displayText = string.Empty;

        [ObservableProperty]
        private bool _isDisplayHex;

        [ObservableProperty]
        private bool _showTimestamp = true;

        [ObservableProperty]
        private bool _autoScroll = true;

        // ==================== 发送 ====================

        [ObservableProperty]
        private string _sendText = string.Empty;

        [ObservableProperty]
        private bool _isSendHex;

        [ObservableProperty]
        private bool _isTimedSend;

        [ObservableProperty]
        private int _intervalMs = 1000;

        // ==================== 统计 ====================

        [ObservableProperty]
        private long _rxCount;

        [ObservableProperty]
        private long _txCount;

        // ==================== 日志 ====================

        [ObservableProperty]
        private bool _isLogging;

        [ObservableProperty]
        private string _logFilePath = string.Empty;

        // ====================================================================
        //  命令
        // ====================================================================

        [RelayCommand]
        private void RefreshPorts()
        {
            string? keep = SelectedPort?.PortName;
            var latest = SerialPortUtil.GetPorts();

            Ports.Clear();
            foreach (var p in latest)
                Ports.Add(p);

            // 刷新后尽量还原之前的选择，避免下拉框被清空
            SelectedPort = FindPort(keep) ?? (Ports.Count > 0 ? Ports[0] : null);

            if (Ports.Count == 0)
                StatusMessage = "未检测到任何串口";
        }

        private SerialPortInfo? FindPort(string? portName)
        {
            if (string.IsNullOrEmpty(portName)) return null;
            foreach (var p in Ports)
            {
                if (string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        [RelayCommand]
        private void ToggleOpen()
        {
            if (IsConnected) ClosePort();
            else OpenPort();
        }

        [RelayCommand(CanExecute = nameof(CanSend))]
        private Task Send() => SendCore(SendText, IsSendHex);

        private bool CanSend() => IsConnected;

        [RelayCommand]
        private void ClearSend() => SendText = string.Empty;

        [RelayCommand]
        private void ClearDisplay()
        {
            _display.Clear();
            DisplayText = string.Empty;
            RxCount = 0;
        }

        [RelayCommand]
        private async Task SendQuickCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            await SendCore(command, IsSendHex);
        }

        /// <summary>把当前发送框内容存为快捷指令</summary>
        [RelayCommand]
        private void SaveQuickCommand()
        {
            string text = SendText.Trim();
            if (text.Length == 0)
            {
                StatusMessage = "发送框为空，无法存为快捷指令";
                return;
            }

            foreach (var item in QuickCommands)
            {
                if (item == text)
                {
                    StatusMessage = "该快捷指令已存在";
                    return;
                }
            }

            QuickCommands.Add(text);
            StatusMessage = "已添加快捷指令";
        }

        [RelayCommand]
        private void RemoveQuickCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                StatusMessage = "请先在列表中选择要删除的快捷指令";
                return;
            }

            QuickCommands.Remove(command);
            StatusMessage = "已删除快捷指令";
        }

        // ====================================================================
        //  打开 / 关闭
        // ====================================================================

        private void OpenPort()
        {
            if (SelectedPort is null)
            {
                StatusMessage = "请先选择串口";
                return;
            }

            var setting = new SerialSetting
            {
                PortName = SelectedPort.PortName,
                BaudRate = ParseBaudRate(),
                DataBits = SelectedDataBits,
                Parity = ParseParity(),
                StopBits = ParseStopBits(),
                Handshake = ParseHandshake(),
            };

            StatusMessage = "正在打开串口...";

            // SerialPortService.Open 内部会吞掉异常并通过 ErrorOccurred 上报，
            // 这里用 _opening 标志区分"打开失败的报错"和"通信中断的报错"。
            _opening = true;
            try
            {
                _serial.Open(setting);
            }
            finally
            {
                _opening = false;
            }

            if (_serial.IsOpen)
            {
                SetConnectionState(SerialConnectionState.Connected);
                StatusMessage = "就绪";
            }
            else
            {
                SetConnectionState(SerialConnectionState.Disconnected);
                if (StatusMessage == "正在打开串口...")
                    StatusMessage = "打开串口失败";
            }
        }

        private void ClosePort()
        {
            StopTimedSend();
            IsTimedSend = false;

            _serial.Close();

            SetConnectionState(SerialConnectionState.Disconnected);
            StatusMessage = "已断开";
        }

        /// <summary>非正常断开（设备失联）</summary>
        private void ForceDisconnect(string message)
        {
            StopTimedSend();
            IsTimedSend = false;
            _serial.Close();

            SetConnectionState(SerialConnectionState.Lost);
            StatusMessage = message;
        }

        private void SetConnectionState(SerialConnectionState state)
        {
            ConnectionState = state;

        }

        // ====================================================================
        //  发送
        // ====================================================================

        private async Task SendCore(string text, bool isHex)
        {
            if (!_serial.IsOpen) return;

            byte[] payload;
            string error;

            if (isHex)
            {
                if (!HexUtil.TryFromHex(text, out payload, out error))
                {
                    PostToUi(() =>
                    {
                        StatusMessage = $"HEX 格式错误：{error}";
                        IsTimedSend = false;
                    });
                    return;
                }
            }
            else
            {
                payload = HexUtil.FromAscii(text);
            }

            if (payload.Length == 0)
            {
                PostToUi(() =>
                {
                    StatusMessage = "没有内容可发送";
                    IsTimedSend = false;
                });
                return;
            }
            StatusMessage = $"正在发送中。。。{text}";
            int length = payload.Length;
            string logBody = isHex ? HexUtil.ToHex(payload) : HexUtil.ToAscii(payload);

            try
            {
                await Task.Run(() =>  _serial.Send(payload));
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                PostToUi(() =>
                {
                    StatusMessage = "发送失败：" + message;
                    IsTimedSend = false;
                });
                return;
            }

            PostToUi(() =>
            {
                TxCount += length;
                if (IsLogging) _logger.Write($"-> {logBody}");
                StatusMessage = "发送" + logBody;
            });
        }

        /// <summary>把操作切回 UI 线程执行</summary>
        private void PostToUi(Action action)
        {
            if (_ui.CheckAccess()) action();
            else _ui.InvokeAsync(action);
        }

        // ====================================================================
        //  定时发送
        // ====================================================================

        partial void OnIsTimedSendChanged(bool value)
        {
            if (value) StartTimedSend();
            else StopTimedSend();
        }

        partial void OnIntervalMsChanged(int value)
        {
            // 间隔变化时重启定时器，让新间隔立即生效
            if (IsTimedSend)
            {
                StopTimedSend();
                StartTimedSend();
            }
        }

        private void StartTimedSend()
        {
            string text = SendText;
            bool isHex = IsSendHex;
            int interval = Math.Max(MinSendIntervalMs, IntervalMs);

            // 校验一次内容，避免定时器空转
            if (IsSendHex && !HexUtil.TryFromHex(SendText, out _, out _))
            {
                StatusMessage = "HEX 格式错误，定时发送未启动";
                IsTimedSend = false;
                return;
            }
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            Interlocked.Exchange(ref _timeCts, cts)?.Cancel();
          
            _timeLoop = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                    while(await timer.WaitForNextTickAsync(token))
                    {
                        await SendCore(text, isHex);
                    }
                }
                catch (OperationCanceledException)
                {

                   
                }
                finally
                {
                    cts.Dispose();
                }
            });

            StatusMessage = $"定时发送中（{IntervalMs} ms）";
        }

        private void StopTimedSend()
        {
            Interlocked.Exchange(ref _timeCts, null)?.Cancel();
   
        }

        // ====================================================================
        //  接收
        // ====================================================================

        /// <summary>读线程回调（后台线程）—— 只入队，不做任何加工</summary>
        private void OnDataReceived(byte[] data) => _rxQueue.Enqueue(data);

        /// <summary>UI 线程定时批量消费队列</summary>
        private void OnUiTick(object? sender, EventArgs e)
        {
            if (_rxQueue.IsEmpty) return;

            var bytes = new ListBuilder();
            int consumed = 0;

            while (consumed < MaxBytesPerTick && _rxQueue.TryDequeue(out var chunk))
            {
                bytes.Append(chunk);
                consumed += chunk.Length;
            }

            byte[] payload = bytes.ToArray();
            if (payload.Length == 0) return;

            RxCount += payload.Length;

            string body = IsDisplayHex ? HexUtil.ToHex(payload) : HexUtil.ToAscii(payload);
            string line = ShowTimestamp
                ? $"[{DateTime.Now:HH:mm:ss.fff}] {body}"
                : body;

            AppendToDisplay(line);

            if (IsLogging)
                _logger.Write($"<- {body}");
        }

        private void AppendToDisplay(string text)
        {
            _display.Append(text);
            _display.Append(Environment.NewLine);   // 每批数据一行

            if (_display.Length > MaxDisplayChars)
            {
                // 超出上限 → 只保留尾部，整体替换一次
                _display.Remove(0, _display.Length - MaxDisplayChars);
                DisplayText = _display.ToString();
            }
            else
            {
                DisplayText = _display.ToString();
            }
        }

        // ====================================================================
        //  串口错误
        // ====================================================================

        private void OnSerialError(string message)
        {
            PostToUi(() =>
            {
                // ① 打开过程中的错误 → 直接显示原因
                if (_opening)
                {
                    StatusMessage = message;
                    return;
                }

                // ② 已连接状态下的读取异常 → 判定设备失联
                if (ConnectionState == SerialConnectionState.Connected)
                {
                    ForceDisconnect($"读取异常：{message}（设备可能已断开）");
                    return;
                }

                // ③ 其余情况（关闭端口产生的余波）→ 静默丢弃
            });
        }

        // ====================================================================
        //  日志
        // ====================================================================

        partial void OnIsLoggingChanged(bool value)
        {
            if (_suppressLogCallback) return;

            _suppressLogCallback = true;
            try
            {
                if (value) StartLogging();
                else StopLogging();
            }
            finally
            {
                _suppressLogCallback = false;
            }
        }

        private void StartLogging()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "SerialAssistantLogs");
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, $"SerialLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                _logger.Start(path);

                LogFilePath = path;
                StatusMessage = "日志已开始记录";
            }
            catch (Exception ex)
            {
                _logger.Stop();
                LogFilePath = string.Empty;
                IsLogging = false;
                StatusMessage = "日志开启失败：" + ex.Message;
            }
        }

        private void StopLogging()
        {
            _logger.Stop();
            StatusMessage = "日志已停止";
        }

        // ====================================================================
        //  初始化
        // ====================================================================

        private void LoadPresets()
        {
            Presets.Add(new SerialPreset("115200 8N1", 115200, 8, Parity.None, StopBits.One));
            Presets.Add(new SerialPreset("9600 8N1", 9600, 8, Parity.None, StopBits.One));
            Presets.Add(new SerialPreset("19200 8N1", 19200, 8, Parity.None, StopBits.One));
            Presets.Add(new SerialPreset("9600 7E1 (PLC)", 9600, 7, Parity.Even, StopBits.One));
            Presets.Add(new SerialPreset("2400 8E1 (电表)", 2400, 8, Parity.Even, StopBits.One));
            Presets.Add(new SerialPreset("9600 8N2 (Modbus)", 9600, 8, Parity.None, StopBits.Two));
        }

        private void LoadDefaultQuickCommands()
        {
            QuickCommands.Add("AT");
            QuickCommands.Add("01 03 00 00 00 0A");
            QuickCommands.Add("01 04 00 00 00 02");
            QuickCommands.Add("AA 55");
        }

        partial void OnSelectedPresetChanged(SerialPreset? value)
        {
            if (value is null) return;

            SelectedBaudRate = value.BaudRate.ToString();
            SelectedDataBits = value.DataBits;
            SelectedParity = value.Parity.ToString();
            SelectedStopBits = value.StopBits switch
            {
                StopBits.Two => "2",
                StopBits.OnePointFive => "1.5",
                _ => "1",
            };

            StatusMessage = $"已应用预设：{value.Name}";
        }

        // ==================== 解析辅助 ====================

        private int ParseBaudRate()
            => int.TryParse(SelectedBaudRate, out int v) && v > 0 ? v : 9600;

        private Parity ParseParity()
            => Enum.TryParse(SelectedParity, out Parity v) ? v : Parity.None;

        private StopBits ParseStopBits() => SelectedStopBits switch
        {
            "2" => StopBits.Two,
            "1.5" => StopBits.OnePointFive,
            _ => StopBits.One,
        };

        private Handshake ParseHandshake()
            => Enum.TryParse(SelectedHandshake, out Handshake v) ? v : Handshake.None;

        // ==================== 清理 ====================

        public void Dispose()
        {
            _uiTimer.Stop();
            _uiTimer.Tick -= OnUiTick;

            StopTimedSend();
            try { _timeLoop?.Wait(500); } catch { }
            _serial.DataReceived -= OnDataReceived;
            _serial.ErrorOccurred -= OnSerialError;
            _serial.Dispose();

            _logger.Dispose();
        }

        /// <summary>把多个 byte[] 拼成一个，避免多次分配 List 扩容</summary>
        private sealed class ListBuilder
        {
            private byte[] _buffer = Array.Empty<byte>();
            private int _length;

            public void Append(byte[] data)
            {
                int required = _length + data.Length;
                if (required > _buffer.Length)
                {
                    int newSize = Math.Max(required, Math.Max(1024, _buffer.Length * 2));
                    Array.Resize(ref _buffer, newSize);
                }

                Buffer.BlockCopy(data, 0, _buffer, _length, data.Length);
                _length = required;
            }

            public byte[] ToArray()
            {
                if (_length == 0) return Array.Empty<byte>();
                var result = new byte[_length];
                Buffer.BlockCopy(_buffer, 0, result, 0, _length);
                return result;
            }
        }
    }
}

