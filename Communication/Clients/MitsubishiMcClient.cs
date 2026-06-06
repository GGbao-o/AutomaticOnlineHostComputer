using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Contracts;
using AutomaticOnlineHostComputer.Communication.Models;

namespace AutomaticOnlineHostComputer.Communication.Clients
{
    /// <summary>
    /// 三菱 PLC MC 协议（1E 帧 + 3E 帧，二进制模式）客户端。
    /// <para>1E帧适用 FX3G+FX3U-ENET-ADP, 3E帧适用 Q系列。</para>
    /// <para>M寄存器位读/写使用 nibble 编码（每4bit=1个M位, 高nibble=偶地址, 低nibble=奇地址）。</para>
    /// </summary>
    public sealed class MitsubishiMcClient : IDeviceClient
    {
        public const byte DeviceD = 0xA8;
        public const byte DeviceR = 0xAF;
        public const byte DeviceM = 0x90;
        public const byte DeviceY = 0xA2;
        public const byte DeviceX = 0x9C;

        public enum McFrameType { A1E, E3 }

        private static byte SoftElementAscii(byte dev) => dev switch
        { DeviceM => (byte)'M', DeviceD => (byte)'D', DeviceR => (byte)'R', DeviceX => (byte)'X', DeviceY => (byte)'Y', _ => dev };

        private readonly string _ip;
        private readonly int    _port;
        private readonly int    _timeoutMs;
        private readonly byte   _deviceType;
        private readonly McFrameType _frameType;
        private readonly bool   _enableConsoleLog; // 默认false, 避免hex dump刷屏

        /// <summary>M区使用位命令(0x00/0x02)+nibble编码。默认false(字读写)。货叉PLC需设true。</summary>
        public bool UseBitReadForM { get; set; }

        private TcpClient?     _tcp;
        private NetworkStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public MitsubishiMcClient(string ip, int port = 3000, byte deviceType = DeviceD, int timeoutMs = 3000, McFrameType frameType = McFrameType.E3, bool enableConsoleLog = false)
        { _ip = ip; _port = port; _deviceType = deviceType; _timeoutMs = timeoutMs; _frameType = frameType; _enableConsoleLog = enableConsoleLog; }

        public bool IsConnected => _tcp?.Connected == true;
        private int OperationTimeoutMs => Math.Max(_timeoutMs, 5000);

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsConnected) return;
                // 旧 TcpClient 可能处于半断线状态但 Connected 已失真；重连前先清掉旧句柄。
                CloseSocketNoThrow();
                _tcp = new TcpClient { SendTimeout = _timeoutMs, ReceiveTimeout = _timeoutMs, NoDelay = true };
                try
                {
                    await _tcp.ConnectAsync(_ip, _port, ct).ConfigureAwait(false);
                    _stream = _tcp.GetStream();
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch
                {
                    CloseSocketNoThrow();
                    throw;
                }
            }
            finally { _lock.Release(); }
        }

        public async Task DisconnectAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try { CloseSocketNoThrow(); }
            finally { _lock.Release(); }
        }

        public async Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            var words = await ReadWordsAsync(_deviceType, (ushort)address, (ushort)count, ct);
            var result = new int[count];
            for (int i = 0; i < count; i++) result[i] = (short)words[i];
            return new ReadResult(result);
        }

        public async Task<double> ReadDoubleAsync(int address, CancellationToken ct = default)
        {
            var words = await ReadWordsAsync(_deviceType, (ushort)address, 2, ct);
            uint bits = (uint)(words[1] << 16) | words[0];
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        public async Task<int> ReadIntAsync(int address, CancellationToken ct = default)
        { var r = await ReadAsync(address, 1, ct); return r.FirstInt; }

        public async Task WriteAsync(int address, int value, CancellationToken ct = default)
        { await WriteWordsAsync(_deviceType, (ushort)address, new[] { (ushort)value }, ct); }

        public async Task WriteAsync(int address, double value, CancellationToken ct = default)
        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
            await WriteWordsAsync(_deviceType, (ushort)address, new[] { (ushort)(bits & 0xFFFF), (ushort)(bits >> 16) }, ct);
        }

        public async Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default)
        { for (int i = 0; i < addresses.Length; i++) await WriteAsync(addresses[i], values[i], ct); }

        public async Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default)
        { for (int i = 0; i < addresses.Length; i++) await WriteAsync(addresses[i], values[i], ct); }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            await _lock.WaitAsync(); _lock.Release(); _lock.Dispose();
        }

        public async Task<ReadResult> ReadAsync(byte deviceType, int address, int count, CancellationToken ct = default)
        {
            var words = await ReadWordsAsync(deviceType, (ushort)address, (ushort)count, ct);
            var result = new int[count];
            for (int i = 0; i < count; i++) result[i] = (short)words[i];
            return new ReadResult(result);
        }

        public Task WriteAsync(byte deviceType, int address, int value, CancellationToken ct = default)
            => WriteWordsAsync(deviceType, (ushort)address, new[] { (ushort)value }, ct);

        // ═══════════════════════════════════════════════════════════════
        //  底层: 批量读
        // ═══════════════════════════════════════════════════════════════
        private async Task<ushort[]> ReadWordsAsync(byte deviceType, ushort startAddr, ushort count, CancellationToken ct)
        {
            EnsureConnected();
            bool useBit = UseBitReadForM && (deviceType == DeviceM || deviceType == DeviceX || deviceType == DeviceY);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, true);

            if (_frameType == McFrameType.A1E)
            {
                // 1E帧: [cmd 1B][PC=0xFF 1B][timer 2B LE][addr 4B LE][空格 1B][设备 ASCII 1B][点数 2B LE]
                byte cmd = useBit ? (byte)0x00 : (byte)0x01;  // 位读0x00 / 字读0x01
                ushort points = useBit ? (ushort)(count * 16) : count;  // 位读→nibble数, 字读→字数
                bw.Write(cmd);
                bw.Write((byte)0xFF);
                bw.Write((ushort)0x000A);
                bw.Write((uint)startAddr);
                bw.Write((byte)0x20);
                bw.Write(SoftElementAscii(deviceType));
                bw.Write(points);
            }
            else
            {
                bw.Write((byte)0x50); bw.Write((byte)0x00);
                bw.Write((byte)0x00); bw.Write((byte)0xFF);
                bw.Write((ushort)0x03FF); bw.Write((byte)0x00);
                bw.Write((ushort)12); bw.Write((ushort)0x0000);
                bw.Write((ushort)0x0401); bw.Write((ushort)0x0000);
                bw.Write((byte)(startAddr & 0xFF)); bw.Write((byte)((startAddr >> 8) & 0xFF));
                bw.Write((byte)0x00); bw.Write(deviceType); bw.Write(count);
            }

            var request = ms.ToArray();
            await _lock.WaitAsync(ct);
            int opTimeoutMs = OperationTimeoutMs;
            using var opTimeoutCts = new CancellationTokenSource(opTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, opTimeoutCts.Token);
            var ioCt = linkedCts.Token;
            try
            {
                if (_enableConsoleLog)
                    Console.WriteLine($"[MC] TX {_frameType} dev=0x{deviceType:X2} addr={startAddr} cnt={count} useBit={useBit} → {BitConverter.ToString(request)}");
                await _stream!.WriteAsync(request, ioCt);

                ushort[] result;
                if (_frameType == McFrameType.A1E)
                {
                    // ── 响应头：兼容两种格式 ──
                    // 格式A(位操作): [Sub=cmd|0x80 1B][EndCode 1B]
                    // 格式B(字操作): [EndCode 2B LE]
                    var hdr = new byte[2];
                    await ReadExactAsync(_stream, hdr, 2, ioCt);
                    byte endCode;
                    if (hdr[0] is 0x80 or 0x81 or 0x82 or 0x83)  // 格式A
                    { endCode = hdr[1]; if (_enableConsoleLog) Console.WriteLine($"[MC] RX A1E fmt=A sub=0x{hdr[0]:X2} endCode=0x{endCode:X2}"); }
                    else  // 格式B: [EndCode 2B LE]
                    { endCode = hdr[0]; if (_enableConsoleLog) Console.WriteLine($"[MC] RX A1E fmt=B endCode=0x{hdr[0]:X2}{hdr[1]:X2}"); }

                    if (endCode != 0)
                        throw new IOException($"A-1E读错误: endCode=0x{endCode:X2}");

                    if (useBit)
                    {
                        // ── nibble 编码解析 ──
                        // 每字节2个nibble: 高4bit=偶地址, 低4bit=奇地址
                        // nibble=0x1→ON, nibble=0x0→OFF
                        int nibbleCount = count * 16;
                        int byteCount = (nibbleCount + 1) / 2;  // 16 nibble → 8 字节
                        var nibbleBytes = new byte[byteCount];
                        await ReadExactAsync(_stream, nibbleBytes, byteCount, ioCt);
                        if (_enableConsoleLog) Console.WriteLine($"[MC] RX A1E nibbles[{nibbleCount}]={BitConverter.ToString(nibbleBytes)}");

                        result = new ushort[count];
                        for (int w = 0; w < count; w++)
                        {
                            ushort val = 0;
                            for (int b = 0; b < 16; b++)
                            {
                                int nibbleIdx = w * 16 + b;
                                int byteIdx = nibbleIdx / 2;
                                bool isHigh = (nibbleIdx % 2 == 0);  // 偶=高nibble
                                int nibble = isHigh ? (nibbleBytes[byteIdx] >> 4) : (nibbleBytes[byteIdx] & 0x0F);
                                if (nibble != 0) val |= (ushort)(1 << b);
                            }
                            result[w] = val;
                        }
                    }
                    else
                    {
                        // ── 字数据 ──
                        var dataBytes = new byte[count * 2];
                        await ReadExactAsync(_stream, dataBytes, dataBytes.Length, ioCt);
                        if (_enableConsoleLog) Console.WriteLine($"[MC] RX A1E data[{count}]={BitConverter.ToString(dataBytes)}");
                        result = new ushort[count];
                        for (int i = 0; i < count; i++)
                            result[i] = (ushort)(dataBytes[i * 2] | (dataBytes[i * 2 + 1] << 8));
                    }
                }
                else
                {
                    var header = new byte[11];
                    await ReadExactAsync(_stream, header, 11, ioCt);
                    ushort dataLength = (ushort)(header[7] | (header[8] << 8));
                    ushort completeCode = (ushort)(header[9] | (header[10] << 8));
                    if (completeCode != 0) throw new IOException($"三菱MC读错误 完成代码:0x{completeCode:X4}");
                    int wordCount = (dataLength - 2) / 2;
                    var dataBytes = new byte[wordCount * 2];
                    await ReadExactAsync(_stream, dataBytes, dataBytes.Length, ioCt);
                    result = new ushort[wordCount];
                    for (int i = 0; i < wordCount; i++)
                        result[i] = (ushort)(dataBytes[i * 2] | (dataBytes[i * 2 + 1] << 8));
                }
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && opTimeoutCts.IsCancellationRequested)
            {
                // 单次MC报文读写必须有硬超时；否则PLC半开不回包会卡住主循环/动作任务并导致锁无法释放。
                CloseSocketNoThrow();
                throw new TimeoutException($"三菱MC读超时({opTimeoutMs}ms): {_ip}:{_port} dev=0x{deviceType:X2} addr={startAddr} cnt={count}");
            }
            catch
            {
                // 读写异常后不能继续相信 TcpClient.Connected；立即置断开，下一轮才会真正重连。
                CloseSocketNoThrow();
                throw;
            }
            finally { _lock.Release(); }
        }

        // ═══════════════════════════════════════════════════════════════
        //  底层: 批量写
        // ═══════════════════════════════════════════════════════════════
        private async Task WriteWordsAsync(byte deviceType, ushort startAddr, ushort[] values, CancellationToken ct)
        {
            EnsureConnected();
            int n = values.Length;
            bool useBit = UseBitReadForM && (deviceType == DeviceM || deviceType == DeviceX || deviceType == DeviceY);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, true);

            if (_frameType == McFrameType.A1E)
            {
                byte cmd = useBit ? (byte)0x02 : (byte)0x03;  // 位写0x02 / 字写0x03
                ushort points = useBit ? (ushort)(n * 16) : (ushort)n;
                bw.Write(cmd); bw.Write((byte)0xFF); bw.Write((ushort)0x000A);
                bw.Write((uint)startAddr); bw.Write((byte)0x20);
                bw.Write(SoftElementAscii(deviceType)); bw.Write(points);

                if (useBit)
                {
                    // ── nibble 编码写入 ──
                    // 每个 ushort → 8 字节 nibble 数据
                    foreach (var v in values)
                    {
                        var nibbleData = new byte[8];  // 16 nibble = 8 字节
                        for (int b = 0; b < 16; b++)
                        {
                            bool on = (v & (1 << b)) != 0;
                            int byteIdx = b / 2;
                            if (b % 2 == 0)
                                nibbleData[byteIdx] |= (byte)(on ? 0x10 : 0x00);  // 偶→高nibble
                            else
                                nibbleData[byteIdx] |= (byte)(on ? 0x01 : 0x00);  // 奇→低nibble
                        }
                        bw.Write(nibbleData);
                    }
                }
                else
                {
                    foreach (var v in values) bw.Write(v);  // 字数据 2B LE
                }
            }
            else
            {
                bw.Write((byte)0x50); bw.Write((byte)0x00);
                bw.Write((byte)0x00); bw.Write((byte)0xFF);
                bw.Write((ushort)0x03FF); bw.Write((byte)0x00);
                ushort dataLen = (ushort)(12 + n * 2); bw.Write(dataLen);
                bw.Write((ushort)0x0000); bw.Write((ushort)0x1401); bw.Write((ushort)0x0000);
                bw.Write((byte)(startAddr & 0xFF)); bw.Write((byte)((startAddr >> 8) & 0xFF));
                bw.Write((byte)0x00); bw.Write(deviceType); bw.Write((ushort)n);
                foreach (var v in values) bw.Write(v);
            }

            var request = ms.ToArray();
            await _lock.WaitAsync(ct);
            int opTimeoutMs = OperationTimeoutMs;
            using var opTimeoutCts = new CancellationTokenSource(opTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, opTimeoutCts.Token);
            var ioCt = linkedCts.Token;
            try
            {
                if (_enableConsoleLog) Console.WriteLine($"[MC] TX WRITE {_frameType} dev=0x{deviceType:X2} addr={startAddr} cnt={n} useBit={useBit} → {BitConverter.ToString(request)}");
                await _stream!.WriteAsync(request, ioCt);

                if (_frameType == McFrameType.A1E)
                {
                    var ack = new byte[2];
                    await ReadExactAsync(_stream, ack, 2, ioCt);
                    byte endCode;
                    if (ack[0] is 0x80 or 0x81 or 0x82 or 0x83)  // 格式A
                    { endCode = ack[1]; if (_enableConsoleLog) Console.WriteLine($"[MC] RX WRITE A1E fmt=A sub=0x{ack[0]:X2} endCode=0x{endCode:X2}"); }
                    else  // 格式B
                    { endCode = ack[0]; if (_enableConsoleLog) Console.WriteLine($"[MC] RX WRITE A1E fmt=B endCode=0x{ack[0]:X2}{ack[1]:X2}"); }

                    if (endCode != 0)
                        throw new IOException($"A-1E写错误: endCode=0x{endCode:X2}");
                }
                else
                {
                    var header = new byte[11];
                    await ReadExactAsync(_stream, header, 11, ioCt);
                    ushort completeCode = (ushort)(header[9] | (header[10] << 8));
                    if (completeCode != 0)
                        throw new IOException($"三菱MC写错误 完成代码:0x{completeCode:X4}");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && opTimeoutCts.IsCancellationRequested)
            {
                // 写入也必须限时；如果等待ACK卡死, 上层无法进入catch/finally释放安全锁。
                CloseSocketNoThrow();
                throw new TimeoutException($"三菱MC写超时({opTimeoutMs}ms): {_ip}:{_port} dev=0x{deviceType:X2} addr={startAddr} cnt={n}");
            }
            catch
            {
                // 写入失败后关闭当前 socket，避免缓存层继续复用半断开的 MC 连接。
                CloseSocketNoThrow();
                throw;
            }
            finally { _lock.Release(); }
        }

        private void EnsureConnected()
        { if (!IsConnected) throw new InvalidOperationException("三菱MC未连接，请先调用 ConnectAsync()。"); }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int received = 0;
            while (received < count)
            {
                int n = await stream.ReadAsync(buffer, received, count - received, ct);
                if (n == 0) throw new IOException("三菱MC连接已断开（远端关闭）。");
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
