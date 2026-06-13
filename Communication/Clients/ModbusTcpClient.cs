using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Contracts;
using AutomaticOnlineHostComputer.Communication.Models;

namespace AutomaticOnlineHostComputer.Communication.Clients
{
    /// <summary>
    /// Modbus TCP 客户端。
    /// <para>
    /// 适用设备：锦州斜床（功能码 03/06/16）、plc研磨机、天车（汇川PLC Modbus TCP）。
    /// </para>
    /// <para>
    /// 协议要点：
    ///   - 标准 Modbus TCP，端口默认 502。
    ///   - 功能码 0x03 读保持寄存器（Read Holding Registers）。
    ///   - 功能码 0x10 写多个寄存器（Write Multiple Registers）。
    ///   - 每个寄存器 16 bit（2 字节），大端序（Big-Endian）。
    ///   - 浮点数：IEEE 754 单精度，占 2 个连续寄存器（4 字节），高字在前。
    /// </para>
    /// </summary>
    public sealed class ModbusTcpClient : IDeviceClient
    {
        // ── 配置 ────────────────────────────────────────────────────────
        private readonly string _ip;
        private readonly int    _port;
        private readonly byte   _unitId;       // 从站地址（默认 1）
        private readonly int    _timeoutMs;
        private readonly bool   _enableConsoleLog; // 默认false, 避免hex dump刷屏

        // ── 底层 TCP ─────────────────────────────────────────────────
        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private ushort _transactionId;         // Modbus TCP 事务 ID（自增）

        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── 构造 ─────────────────────────────────────────────────────

        /// <param name="ip">设备 IP 地址</param>
        /// <param name="port">端口，默认 502</param>
        /// <param name="unitId">从站 ID，默认 1</param>
        /// <param name="timeoutMs">读写超时（毫秒），默认 3000</param>
        /// <param name="enableConsoleLog">是否打印 hex dump 日志，默认 false</param>
        public ModbusTcpClient(string ip, int port = 502, byte unitId = 1, int timeoutMs = 3000,
                               bool enableConsoleLog = false)
        {
            _ip        = ip;
            _port      = port;
            _unitId    = unitId;
            _timeoutMs = timeoutMs;
            _enableConsoleLog = enableConsoleLog;
            if (_enableConsoleLog)
                Console.WriteLine($"[ModbusTcpClient] 创建客户端 ip={_ip}:{_port}, unitId={_unitId}, timeout={_timeoutMs}ms");
        }

        // ── IDeviceClient ────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsConnected => _tcp?.Connected == true && _stream != null;
        private int OperationTimeoutMs => Math.Max(_timeoutMs, 5000);

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (IsConnected) return;

                // 旧连接可能已经半断开但 TcpClient.Connected 仍为 true/旧值；重连前必须清理。
                CloseSocketNoThrow();
                _tcp = new TcpClient { SendTimeout = _timeoutMs, ReceiveTimeout = _timeoutMs };
                if (_enableConsoleLog) Console.WriteLine($"[ModbusTcpClient] 开始连接 {_ip}:{_port}");

                // 强制 5 秒连接超时：TcpClient 自身不认 SendTimeout，
                // 底层走 Windows TCP 握手默认 21s，用 CancellationTokenSource 兜底切断
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    await _tcp.ConnectAsync(_ip, _port, linkedCts.Token);
                    _stream = _tcp.GetStream();
                    if (_enableConsoleLog) Console.WriteLine($"[ModbusTcpClient] 连接成功 {_ip}:{_port}");
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    CloseSocketNoThrow();
                    throw new TimeoutException($"Modbus TCP 连接超时（5s）：{_ip}:{_port}");
                }
                catch
                {
                    CloseSocketNoThrow();
                    throw;
                }
            }
            finally { _lock.Release(); }
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync()
        {
            // 必须获取锁：防止正在 SendRequestAsync 中读写 _stream 时被 Close/null
            await _lock.WaitAsync();
            try
            {
                CloseSocketNoThrow();
                if (_enableConsoleLog) Console.WriteLine($"[ModbusTcpClient] 已断开 {_ip}:{_port}");
            }
            finally { _lock.Release(); }
        }

        /// <inheritdoc/>
        public async Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            // 读取 count 个保持寄存器（功能码 0x03）
            var raw = await ReadRegistersAsync((ushort)address, (ushort)count, ct);
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = (short)raw[i]; // 有符号解析（如需无符号改为 (int)raw[i]）
            return new ReadResult(result);
        }

        /// <inheritdoc/>
        public async Task<double> ReadDoubleAsync(int address, CancellationToken ct = default)
        {
            // 读取 2 个连续寄存器，拼合成 IEEE 754 单精度浮点
            var raw = await ReadRegistersAsync((ushort)address, 2, ct);
            uint hi = raw[0];
            uint lo = raw[1];
            uint bits = (hi << 16) | lo;
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        /// <inheritdoc/>
        public async Task<int> ReadIntAsync(int address, CancellationToken ct = default)
        {
            var result = await ReadAsync(address, 1, ct);
            return result.FirstInt;
        }

        /// <inheritdoc/>
        public async Task WriteAsync(int address, int value, CancellationToken ct = default)
        {
            // 写单个寄存器（功能码 0x06）
            await WriteSingleRegisterAsync((ushort)address, (ushort)value, ct);
        }

        /// <inheritdoc/>
        public async Task WriteAsync(int address, double value, CancellationToken ct = default)
        {
            // IEEE 754 单精度浮点拆成 2 个寄存器
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
            ushort hi = (ushort)(bits >> 16);
            ushort lo = (ushort)(bits & 0xFFFF);
            await WriteMultipleRegistersAsync((ushort)address, new[] { hi, lo }, ct);
        }

        /// <inheritdoc/>
        public async Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
            // 逐个写入（如果地址连续可优化为批量写；此处通用写法）
            for (int i = 0; i < addresses.Length; i++)
                await WriteAsync(addresses[i], values[i], ct);
        }

        /// <inheritdoc/>
        public async Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
            for (int i = 0; i < addresses.Length; i++)
                await WriteAsync(addresses[i], values[i], ct);
        }

        /// <summary>
        /// 原子写入一个 Int32 值到两个连续 Modbus 寄存器（FC16 Write Multiple Registers）。
        /// 小端（Little-Endian）：低字在前（startAddr），高字在后（startAddr+1）。
        /// 与汇川 PLC 的 32 位有符号整数小端字节交换一致。
        /// 一次 FC16 请求完成，PLC 不会读到半新半旧的中间态。
        /// </summary>
        /// <param name="startAddr">起始寄存器地址（低字）</param>
        /// <param name="value">32 位有符号整数值</param>
        public async Task WriteInt32Async(int startAddr, int value, CancellationToken ct = default)
        {
            ushort lo = (ushort)(value & 0xFFFF);
            ushort hi = (ushort)((value >> 16) & 0xFFFF);
            await WriteMultipleRegistersAsync((ushort)startAddr, new[] { lo, hi }, ct);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            // 获取锁后再释放：确保没有其他线程正在 SendRequestAsync 中持有 _lock
            await _lock.WaitAsync();
            _lock.Release();
            _lock.Dispose();
        }

        // ── 内部底层方法 ──────────────────────────────────────────────

        /// <summary>
        /// 发送 Modbus TCP 请求并读取响应。
        /// </summary>
        private async Task<byte[]> SendRequestAsync(byte[] pdu, int responseDataLen, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            int opTimeoutMs = OperationTimeoutMs;
            using var opTimeoutCts = new CancellationTokenSource(opTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, opTimeoutCts.Token);
            var ioCt = linkedCts.Token;
            try
            {
                // 连接状态必须在锁内重新确认。
                // 可能在等待锁期间, 前一个请求超时并 CloseSocketNoThrow(), 导致 _stream 被置空。
                EnsureConnected();
                var stream = _stream ?? throw new InvalidOperationException("Modbus TCP 连接流为空，请重新连接。");

                ushort tid = ++_transactionId;

                // MBAP Header (7 bytes) + PDU
                var request = new byte[7 + pdu.Length];
                request[0] = (byte)(tid >> 8);
                request[1] = (byte)(tid & 0xFF);
                request[2] = 0; request[3] = 0;          // Protocol ID = 0
                ushort len = (ushort)(1 + pdu.Length);
                request[4] = (byte)(len >> 8);
                request[5] = (byte)(len & 0xFF);
                request[6] = _unitId;
                Array.Copy(pdu, 0, request, 7, pdu.Length);

                if (_enableConsoleLog)
                    Console.WriteLine($"[ModbusTcpClient] TX tid={tid} unit={_unitId} pdu={BitConverter.ToString(pdu)} frame={BitConverter.ToString(request)}");

                await stream.WriteAsync(request, ioCt);

                // 接收响应：MBAP(7) + PDU
                var header = new byte[7];
                await ReadExactAsync(stream, header, 7, ioCt);
                int dataLen = ((header[4] << 8) | header[5]) - 1; // -1 去掉 UnitId
                var body = new byte[dataLen];
                await ReadExactAsync(stream, body, dataLen, ioCt);

                if (_enableConsoleLog)
                    Console.WriteLine($"[ModbusTcpClient] RX tid={((header[0] << 8) | header[1])} header={BitConverter.ToString(header)} body={BitConverter.ToString(body)}");

                // body[0] = 功能码，body[1] = 字节数，后面是数据
                if ((body[0] & 0x80) != 0)
                    throw new IOException($"Modbus 异常响应：错误码 0x{body[1]:X2}");

                return body;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && opTimeoutCts.IsCancellationRequested)
            {
                // 单次Modbus请求必须有硬超时；设备半开不回包时, 不能让主循环或动作任务一直挂住。
                CloseSocketNoThrow();
                throw new TimeoutException($"Modbus TCP请求超时({opTimeoutMs}ms)：{_ip}:{_port}");
            }
            catch
            {
                // 任何报文读写异常后都把连接标记为断开，防止后续继续复用坏 socket。
                CloseSocketNoThrow();
                throw;
            }
            finally { _lock.Release(); }
        }

        /// <summary>读取 count 个保持寄存器（FC=03），返回寄存器值数组（ushort）。</summary>
        private async Task<ushort[]> ReadRegistersAsync(ushort startAddr, ushort count, CancellationToken ct)
        {
            var pdu = new byte[]
            {
                0x03,
                (byte)(startAddr >> 8), (byte)(startAddr & 0xFF),
                (byte)(count     >> 8), (byte)(count     & 0xFF)
            };
            var resp = await SendRequestAsync(pdu, 2 + count * 2, ct);
            // resp[0]=FC, resp[1]=字节数, resp[2..]=数据
            var result = new ushort[count];
            for (int i = 0; i < count; i++)
                result[i] = (ushort)((resp[2 + i * 2] << 8) | resp[3 + i * 2]);
            return result;
        }

        /// <summary>读线圈（FC=01）。X区二进制信号位，每个地址对应一个X点。</summary>
        public async Task<bool> ReadCoilAsync(int address, CancellationToken ct = default)
        {
            var pdu = new byte[]
            {
                0x01,  // FC01 Read Coils
                (byte)(address >> 8), (byte)(address & 0xFF),
                0x00, 0x01  // count=1
            };
            var resp = await SendRequestAsync(pdu, 2, ct);
            // resp[0]=FC, resp[1]=字节数(1), resp[2]=数据字节
            return (resp[2] & 0x01) != 0;
        }

        /// <summary>读输入寄存器（FC=04）。D63488等只读区必须用此方法。</summary>
        public async Task<int[]> ReadInputRegistersAsync(int startAddr, int count, CancellationToken ct = default)
        {
            var raw = await ReadInputRegistersInternalAsync((ushort)startAddr, (ushort)count, ct);
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = (short)raw[i];
            return result;
        }

        private async Task<ushort[]> ReadInputRegistersInternalAsync(ushort startAddr, ushort count, CancellationToken ct)
        {
            var pdu = new byte[]
            {
                0x04,  // FC04 Read Input Registers（只读寄存器）
                (byte)(startAddr >> 8), (byte)(startAddr & 0xFF),
                (byte)(count     >> 8), (byte)(count     & 0xFF)
            };
            var resp = await SendRequestAsync(pdu, 2 + count * 2, ct);
            var result = new ushort[count];
            for (int i = 0; i < count; i++)
                result[i] = (ushort)((resp[2 + i * 2] << 8) | resp[3 + i * 2]);
            return result;
        }

        /// <summary>写单个寄存器（FC=06）。</summary>
        private async Task WriteSingleRegisterAsync(ushort addr, ushort value, CancellationToken ct)
        {
            var pdu = new byte[]
            {
                0x06,
                (byte)(addr  >> 8), (byte)(addr  & 0xFF),
                (byte)(value >> 8), (byte)(value & 0xFF)
            };
            await SendRequestAsync(pdu, 4, ct);
        }

        /// <summary>写多个连续寄存器（FC=16）。</summary>
        private async Task WriteMultipleRegistersAsync(ushort startAddr, ushort[] values, CancellationToken ct)
        {
            int n = values.Length;
            var pdu = new byte[6 + n * 2];
            pdu[0] = 0x10;
            pdu[1] = (byte)(startAddr >> 8); pdu[2] = (byte)(startAddr & 0xFF);
            pdu[3] = (byte)(n >> 8);         pdu[4] = (byte)(n & 0xFF);
            pdu[5] = (byte)(n * 2);
            for (int i = 0; i < n; i++)
            {
                pdu[6 + i * 2]     = (byte)(values[i] >> 8);
                pdu[7 + i * 2]     = (byte)(values[i] & 0xFF);
            }
            await SendRequestAsync(pdu, 4, ct);
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Modbus TCP 未连接，请先调用 ConnectAsync()。");
        }

        /// <summary>从 stream 精确读取 count 字节。</summary>
        private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int received = 0;
            while (received < count)
            {
                int n = await stream.ReadAsync(buffer, received, count - received, ct);
                if (n == 0) throw new IOException("Modbus TCP 连接已断开（远端关闭）。");
                received += n;
            }
        }

        private void CloseSocketNoThrow()
        {
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }
    }
}
