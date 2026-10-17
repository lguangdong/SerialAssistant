using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using SerialAssistant.Services.Interface;

namespace SerialAssistant.Services.SerialAssistant
{
    public sealed class SerialPortService : ISerialPortService
    {
        private const int ReadTimeoutMs = 200;
        private SerialPort? _port = null;
        private Thread? _readLoopThread = null;
        private readonly object _lock = new object();
        public bool IsOpen => _port?.IsOpen == true;

        public event Action<byte[]>? DataReceived;
        public event Action<string>? ErrorOccurred;

        public void Open(SerialSetting s)
        {
            if (IsOpen) return;
            ReleasePort();
            var port = new SerialPort(s.PortName, s.BaudRate, s.Parity, s.DataBits, s.StopBits)
            {
                Handshake = s.Handshake,
                ReadTimeout = ReadTimeoutMs,
                WriteTimeout = 2000,
                ReadBufferSize = 8192,
            };
            try
            {
                port.Open();
                port.DiscardInBuffer();
            }
            catch (Exception ex)
            {
                try { port.Dispose(); } catch { }
                ErrorOccurred?.Invoke(ex.Message);
                return;
            }
       
            _port = port;
            _readLoopThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "SerialPortService.ReadLoop",
            };
            _readLoopThread.Start(port);
        }

        private void ReadLoop(object? state)
        {
            var port = (SerialPort)state!;
            var buffer = new byte[4096];
            while (true)
            {
                try
                {
                    int n = port.Read(buffer, 0, buffer.Length);
                    if (n > 0)
                    {
                        DataReceived?.Invoke(buffer.AsSpan(0, n).ToArray());
                    }
                }
                catch (TimeoutException) { } //正常超时
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(ex.Message);
                    break;
                }

            }
        }

        public int Send(byte[] data)
        {
            lock (_lock)
            {
                if (!IsOpen) return 0;
                _port!.Write(data, 0, data.Length);
                return data.Length;
            }

        }

        public void Close() => ReleasePort();

        public void Dispose() => ReleasePort();

        private void ReleasePort()
        {
            SerialPort? port;
            Thread? thread;
            lock (_lock)
            {
                port = _port;
                thread = _readLoopThread;
                _port = null;
                _readLoopThread = null;
            }
            if (port == null) return;
            try { if (port.IsOpen) port.Close(); } catch { }

            if (!thread!.Join(ReadTimeoutMs + 100))
                ErrorOccurred?.Invoke("读线程未正常退出");

            try { port.Dispose(); } catch { }
        }
    }
}
