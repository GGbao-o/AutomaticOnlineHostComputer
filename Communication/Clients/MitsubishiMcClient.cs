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
    /// 三菱 PLC MC 协议（3E 帧，二进制模式）客户端。
    /// <para>
    /// 适用设备：
    ///   - 上料架 / 中转架（三菱 FX3G + FX3U-ENET-L）
    ///   - 下料架 / 动平衡料架（三菱 PLC）
    ///   - 货叉（三菱 PLC）
    /// </para>
    /// <para>
    /// 协议要点：
    ///   - 3E 帧格式（二进制），默认端口 3000（可配置）。
    ///   - 命令 0x0401 批量读字（Read Word），子命令 0x0000。
    ///   - 命令 0x1401 批量写字（Write Word），子命令 0x0000。
    ///   - 软元件类型（DeviceType）：D区=0xA8，R区=0xAF，M区=0x90，Y区=0xA2，X区=0x9C 等。
    ///   - 数据为小端序（Little-Endian）。
    /// </para>
    /// </summary>
    public sealed class MitsubishiMcClient : IDeviceClient
    {
        // ── 常用软元件类型编码（MC协议二进制模式）──────────────────────
        public const byte DeviceD = 0xA8; // 数据寄存器 D
        public const byte DeviceR = 0xAF; // 文件寄存器 R
        public const byte DeviceM = 0x90; // 内部继电器 M
        public const byte DeviceY = 0xA2; // 输出继电器 Y
        public const byte DeviceX = 0x9C; // 输入继电器 X

        // ── 配置 ────────────────────────────────────────────────────────
        private readonly string _ip;
        private readonly int    _port;
        private readonly int    _timeoutMs;
        private readonly byte   _deviceType; // 默认软元件类型（D区）

        // ── 底层 TCP ─────────────────────────────────────────────────
        private TcpClient?     _tcp;
        private NetworkStream? _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── 构造 ─────────────────────────────────────────────────────

        /// <param name="ip">PLC IP 地址</param>
        /// <param name="port">端口，默认 3000（FX3U-ENET-L 默认）</param>
        /// <param name="deviceType">默认软元件类型，默认 D 区（0xA8）</param>
        /// <param name="timeoutMs">超时毫秒</param>
        public MitsubishiMcClient(string ip, int port = 3000, byte deviceType = DeviceD, int timeoutMs = 3000)
        {
            _ip         = ip;
            _port       = port;
            _deviceType = deviceType;
            _timeoutMs  = timeoutMs;
        }

        // ── IDeviceClient ────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsConnected => _tcp?.Connected == true;

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (IsConnected) return;
                _tcp = new TcpClient { SendTimeout = _timeoutMs, ReceiveTimeout = _timeoutMs };
                await _tcp.ConnectAsync(_ip, _port, ct);
                _stream = _tcp.GetStream();
            }
            finally { _lock.Release(); }
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync()
        {
            // 必须获取锁：防止正在 ReadWordsAsync/WriteWordsAsync 中读写 _stream 时被 Close/null
            await _lock.WaitAsync();
            try
            {
                _stream?.Close();
                _tcp?.Close();
                _stream = null;
                _tcp    = null;
            }
            finally { _lock.Release(); }
        }

        /// <inheritdoc/>
        public async Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            var words = await ReadWordsAsync(_deviceType, (ushort)address, (ushort)count, ct);
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = (short)words[i]; // 有符号
            return new ReadResult(result);
        }

        /// <inheritdoc/>
        public async Task<double> ReadDoubleAsync(int address, CancellationToken ct = default)
        {
            // 读 2 个连续字，拼成 32 位整数，再转 IEEE 754 单精度浮点
            var words = await ReadWordsAsync(_deviceType, (ushort)address, 2, ct);
            uint lo = words[0];
            uint hi = words[1];
            uint bits = (hi << 16) | lo; // 三菱小端：低字在前
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
            await WriteWordsAsync(_deviceType, (ushort)address, new[] { (ushort)value }, ct);
        }

        /// <inheritdoc/>
        public async Task WriteAsync(int address, double value, CancellationToken ct = default)
        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0);
            ushort lo = (ushort)(bits & 0xFFFF);
            ushort hi = (ushort)(bits >> 16);
            await WriteWordsAsync(_deviceType, (ushort)address, new[] { lo, hi }, ct); // 低字在前
        }

        /// <inheritdoc/>
        public async Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
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

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            // 获取锁后再释放：确保没有其他线程正在读写方法中持有 _lock
            await _lock.WaitAsync();
            _lock.Release();
            _lock.Dispose();
        }

        // ── 额外的软元件类型读写（供业务层指定软元件区）──────────────

        /// <summary>
        /// 以指定软元件类型读取字数据。
        /// 例：ReadWordsAsync(DeviceD, 100, 5) 读 D100~D104。
        /// </summary>
        public async Task<ReadResult> ReadAsync(byte deviceType, int address, int count, CancellationToken ct = default)
        {
            var words = await ReadWordsAsync(deviceType, (ushort)address, (ushort)count, ct);
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = (short)words[i];
            return new ReadResult(result);
        }

        /// <summary>
        /// 以指定软元件类型写入单个字。
        /// </summary>
        public Task WriteAsync(byte deviceType, int address, int value, CancellationToken ct = default)
            => WriteWordsAsync(deviceType, (ushort)address, new[] { (ushort)value }, ct);

        // ── 内部底层方法 ──────────────────────────────────────────────

        /// <summary>
        /// MC 协议 3E 帧：批量读字（命令 0x0401，子命令 0x0000）。
        /// </summary>
        private async Task<ushort[]> ReadWordsAsync(byte deviceType, ushort startAddr, ushort count, CancellationToken ct)
        {
            EnsureConnected();

            // ── 构造请求报文 ──────────────────────────────────────────
            // 副头部(2) + 网络号(1) + PC号(1) + IO编号(2) + 站号(1) + 数据长度(2) + 预约(2) + 命令(2) + 子命令(2) + 起始地址(3) + 软元件类型(1) + 点数(2)
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
            bw.Write((ushort)0x5000);  // 副头部 3E 帧
            bw.Write((byte)0x00);      // 网络号
            bw.Write((byte)0xFF);      // PC号（本站）
            bw.Write((ushort)0x03FF);  // 目标IO编号（主站）
            bw.Write((byte)0x00);      // 目标站号
            // 数据长度 = 从"预约"到末尾的字节数 = 2+2+2+3+1+2 = 12
            bw.Write((ushort)12);
            bw.Write((ushort)0x0000);  // 预约（CPU 监视定时器）
            bw.Write((ushort)0x0401);  // 命令：批量读
            bw.Write((ushort)0x0000);  // 子命令：字单位
            // 起始软元件编号（3字节 LE）
            bw.Write((byte)(startAddr & 0xFF));
            bw.Write((byte)((startAddr >> 8) & 0xFF));
            bw.Write((byte)0x00);
            bw.Write(deviceType);      // 软元件类型
            bw.Write(count);           // 点数

            var request = ms.ToArray();
            await _lock.WaitAsync(ct);
            try
            {
                await _stream!.WriteAsync(request, ct);

                // ── 读响应报文 ───────────────────────────────────────
                var header = new byte[11];
                await ReadExactAsync(_stream, header, 11, ct);
                // header[7..8] = 数据长度，header[9..10] = 完成代码
                ushort dataLength    = (ushort)(header[7] | (header[8] << 8));
                ushort completeCode  = (ushort)(header[9] | (header[10] << 8));
                if (completeCode != 0)
                    throw new IOException($"三菱MC协议读错误，完成代码：0x{completeCode:X4}");

                int wordCount = (dataLength - 2) / 2; // -2 去掉完成代码本身
                var dataBytes = new byte[wordCount * 2];
                await ReadExactAsync(_stream, dataBytes, dataBytes.Length, ct);

                var result = new ushort[wordCount];
                for (int i = 0; i < wordCount; i++)
                    result[i] = (ushort)(dataBytes[i * 2] | (dataBytes[i * 2 + 1] << 8));
                return result;
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// MC 协议 3E 帧：批量写字（命令 0x1401，子命令 0x0000）。
        /// </summary>
        private async Task WriteWordsAsync(byte deviceType, ushort startAddr, ushort[] values, CancellationToken ct)
        {
            EnsureConnected();

            int n = values.Length;
            // 数据长度 = 2(预约)+2(命令)+2(子命令)+3(地址)+1(类型)+2(点数)+n*2(数据) = 12 + n*2
            ushort dataLen = (ushort)(12 + n * 2);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
            bw.Write((ushort)0x5000);
            bw.Write((byte)0x00);
            bw.Write((byte)0xFF);
            bw.Write((ushort)0x03FF);
            bw.Write((byte)0x00);
            bw.Write(dataLen);
            bw.Write((ushort)0x0000);  // 预约
            bw.Write((ushort)0x1401);  // 命令：批量写
            bw.Write((ushort)0x0000);  // 子命令：字单位
            bw.Write((byte)(startAddr & 0xFF));
            bw.Write((byte)((startAddr >> 8) & 0xFF));
            bw.Write((byte)0x00);
            bw.Write(deviceType);
            bw.Write((ushort)n);
            foreach (var v in values)
                bw.Write(v); // LE

            var request = ms.ToArray();
            await _lock.WaitAsync(ct);
            try
            {
                await _stream!.WriteAsync(request, ct);

                // 读响应（11字节）
                var header = new byte[11];
                await ReadExactAsync(_stream, header, 11, ct);
                ushort completeCode = (ushort)(header[9] | (header[10] << 8));
                if (completeCode != 0)
                    throw new IOException($"三菱MC协议写错误，完成代码：0x{completeCode:X4}");
            }
            finally { _lock.Release(); }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("三菱MC未连接，请先调用 ConnectAsync()。");
        }

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
    }
}
