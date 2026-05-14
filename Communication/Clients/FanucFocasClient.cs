using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Contracts;
using AutomaticOnlineHostComputer.Communication.Models;

namespace AutomaticOnlineHostComputer.Communication.Clients
{
    /// <summary>
    /// FANUC FOCAS 2 通信客户端。
    /// <para>
    /// 适用设备：沈阳斜床（FANUC 系统），通过以太网 TCP 端口 8193 通信。
    /// </para>
    /// <para>
    /// 依赖 DLL：<c>Libs/FANUC/Fwlib32.dll</c>（32 位）或 <c>fwlib30i.dll</c>（64 位）。
    /// 项目需将 DLL 复制到输出目录。
    /// </para>
    /// <para>
    /// 地址规则：
    ///   - 宏变量 #800 → address=800，#1000 → address=1000，以此类推。
    ///   - 本实现默认操作宏变量（cnc_rdmacro / cnc_wrmacro）。
    ///   - PMC 信号读写通过 <see cref="ReadPmcAsync"/> / <see cref="WritePmcAsync"/> 扩展方法访问。
    /// </para>
    /// </summary>
    public sealed class FanucFocasClient : IDeviceClient
    {
        // ── 配置 ────────────────────────────────────────────────────────
        private readonly string _ip;
        private readonly ushort _port;
        private readonly short  _timeoutSec;

        // ── 连接句柄 ─────────────────────────────────────────────────
        private uint _handle;
        private bool _isConnected;

        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── 构造 ─────────────────────────────────────────────────────

        /// <param name="ip">CNC IP 地址</param>
        /// <param name="port">端口，默认 8193</param>
        /// <param name="timeoutSec">连接超时秒数，默认 10</param>
        public FanucFocasClient(string ip, ushort port = 8193, short timeoutSec = 10)
        {
            _ip         = ip;
            _port       = port;
            _timeoutSec = timeoutSec;
        }

        // ── IDeviceClient ────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsConnected => _isConnected;

        /// <inheritdoc/>
        public Task ConnectAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                // cnc_allclibhndl3：建立 FOCAS 连接，返回句柄
                short ret = Focas.cnc_allclibhndl3(_ip, _port, _timeoutSec, out _handle);
                if (ret != 0)
                    throw new InvalidOperationException($"FANUC FOCAS 连接失败，错误码：{ret}（IP={_ip}:{_port}）");
                _isConnected = true;
            }, ct);
        }

        /// <inheritdoc/>
        public Task DisconnectAsync()
        {
            if (_isConnected)
            {
                Focas.cnc_freelibhndl(_handle);
                _isConnected = false;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 默认读取宏变量，返回浮点型 <see cref="ReadResult"/>。
        /// count > 1 时逐个读取连续宏变量号。
        /// </remarks>
        public Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                var values = new double[count];
                for (int i = 0; i < count; i++)
                {
                    short ret = Focas.cnc_rdmacro(_handle, (short)(address + i), 10, out var macro);
                    if (ret != 0)
                        throw new IOException($"FANUC 读宏变量 #{address + i} 失败，错误码：{ret}");
                    // FOCAS 宏变量值 = mcr_val / 10^dec_val
                    values[i] = macro.mcr_val / Math.Pow(10, macro.dec_val);
                }
                return new ReadResult(values);
            }, ct);
        }

        /// <inheritdoc/>
        public async Task<double> ReadDoubleAsync(int address, CancellationToken ct = default)
        {
            var result = await ReadAsync(address, 1, ct);
            return result.FirstDouble;
        }

        /// <inheritdoc/>
        public async Task<int> ReadIntAsync(int address, CancellationToken ct = default)
        {
            var result = await ReadAsync(address, 1, ct);
            return result.FirstInt;
        }

        /// <inheritdoc/>
        public Task WriteAsync(int address, int value, CancellationToken ct = default)
            => WriteAsync(address, (double)value, ct);

        /// <inheritdoc/>
        public Task WriteAsync(int address, double value, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                // FOCAS 写宏变量：value * 10^dec_val → mcr_val；dec_val=3 表示保留3位小数
                const short decVal = 3;
                var macro = new Focas.ODBM
                {
                    mcr_val = (short)Math.Round(value * Math.Pow(10, decVal)),
                    dec_val = decVal
                };
                short ret = Focas.cnc_wrmacro(_handle, (short)address, 10, ref macro);
                if (ret != 0)
                    throw new IOException($"FANUC 写宏变量 #{address} 失败，错误码：{ret}");
            }, ct);
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
            _lock.Dispose();
        }

        // ── FOCAS 扩展：PMC 信号读写 ──────────────────────────────────

        /// <summary>
        /// 读取 PMC 信号（cnc_rdpmcrng）。
        /// </summary>
        /// <param name="type">PMC 地址类型（0=G，1=F，2=Y，3=X，4=A，5=R，6=T，7=C，8=D，9=K，10=L…）</param>
        /// <param name="start">起始地址</param>
        /// <param name="end">结束地址</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>字节数组</returns>
        public Task<byte[]> ReadPmcAsync(short type, ushort start, ushort end, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                int count = end - start + 1;
                var buf = new Focas.IODBPMC0 { datano_s = start, datano_e = end, type = type };
                short ret = Focas.cnc_rdpmcrng(_handle, type, 0, start, end, (ushort)(8 + count), ref buf);
                if (ret != 0)
                    throw new IOException($"FANUC PMC读失败，错误码：{ret}");
                var result = new byte[count];
                Array.Copy(buf.cdata, result, count);
                return result;
            }, ct);
        }

        /// <summary>
        /// 写入 PMC 信号（cnc_wrpmcrng）。
        /// </summary>
        public Task WritePmcAsync(short type, ushort start, byte[] data, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                ushort end = (ushort)(start + data.Length - 1);
                var buf = new Focas.IODBPMC0 { datano_s = start, datano_e = end, type = type };
                Array.Copy(data, buf.cdata, data.Length);
                short ret = Focas.cnc_wrpmcrng(_handle, (ushort)(8 + data.Length), ref buf);
                if (ret != 0)
                    throw new IOException($"FANUC PMC写失败，错误码：{ret}");
            }, ct);
        }

        // ── 私有 ─────────────────────────────────────────────────────

        private void EnsureConnected()
        {
            if (!_isConnected)
                throw new InvalidOperationException("FANUC FOCAS 未连接，请先调用 ConnectAsync()。");
        }

        // ── P/Invoke 绑定（FANUC Fwlib32.dll / fwlib30i.dll）──────────

        private static class Focas
        {
            // 注意：实际项目应根据 32/64 位选择对应 DLL；此处以 Fwlib32.dll 为例。
            private const string DllName = "Fwlib32";

            /// <summary>宏变量读取结构体。</summary>
            [StructLayout(LayoutKind.Sequential)]
            public struct ODBM
            {
                public short mcr_val; // 宏变量整数值（原始）
                public short dec_val; // 小数位数
            }

            /// <summary>PMC 字节读写缓冲区（最大 64 字节）。</summary>
            [StructLayout(LayoutKind.Sequential)]
            public struct IODBPMC0
            {
                public short  type;
                public ushort datano_s;
                public ushort datano_e;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
                public byte[] cdata;
                public IODBPMC0() { cdata = new byte[64]; }
            }

            [DllImport(DllName)] public static extern short cnc_allclibhndl3(
                [MarshalAs(UnmanagedType.LPStr)] string ipaddr, ushort port, short timeout, out uint hndl);

            [DllImport(DllName)] public static extern short cnc_freelibhndl(uint hndl);

            [DllImport(DllName)] public static extern short cnc_rdmacro(
                uint hndl, short index, short length, out ODBM macro);

            [DllImport(DllName)] public static extern short cnc_wrmacro(
                uint hndl, short index, short length, ref ODBM macro);

            [DllImport(DllName)] public static extern short cnc_rdpmcrng(
                uint hndl, short type, short kind, ushort db_no, ushort de_no, ushort length, ref IODBPMC0 buf);

            [DllImport(DllName)] public static extern short cnc_wrpmcrng(
                uint hndl, ushort length, ref IODBPMC0 buf);
        }
    }
}
