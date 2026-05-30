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
        private readonly int _timeoutCs; // FOCAS超时单位=1/100秒, 500=5秒

        // ── 连接句柄 ─────────────────────────────────────────────────
        private ushort _handle;
        private bool _isConnected;

        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── 构造 ─────────────────────────────────────────────────────

        /// <param name="ip">CNC IP 地址</param>
        /// <param name="port">端口，默认 8193</param>
        /// <param name="timeoutCs">连接超时(1/100秒)，默认500=5秒</param>
        public FanucFocasClient(string ip, ushort port = 8193, int timeoutCs = 500)
        {
            _ip         = ip;
            _port       = port;
            _timeoutCs = timeoutCs;
        }

        // ── IDeviceClient ────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsConnected => _isConnected;

        /// <inheritdoc/>
        public Task ConnectAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                _handle = 0;
                short ret = Focas.cnc_allclibhndl3(_ip, _port, _timeoutCs, out _handle);
                Console.WriteLine($"[FocasClient] connect {_ip}:{_port} timeoutCs={_timeoutCs} ret={ret} hndl={_handle}");
                if (ret != 0)
                    throw new InvalidOperationException($"FANUC FOCAS 连接失败，错误码：{ret}（IP={_ip}:{_port}）");
                _isConnected = true;
                // 设置会话超时300秒, 防止crane移动期间FOCAS断连
                Focas.cnc_settimeout(_handle, 300);
                Console.WriteLine($"[FocasClient] settimeout=300s");
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
                    short varNo = (short)(address + i);
                    var macro = new Focas.ODBM();
                    short ret = Focas.cnc_rdmacro(_handle, varNo, 10, macro);
                    if (ret != 0)
                        throw new IOException($"FANUC 读宏变量 #{address + i} 失败，错误码：{ret}");
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
                const short decVal = 3;
                int mcr = (int)Math.Round(value * Math.Pow(10, decVal));
                short ret = Focas.cnc_wrmacro(_handle, (short)address, 10, mcr, decVal);
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

        /// <summary>读取 PMC 信号（cnc_rdpmcrng），-8 自动重连重试</summary>
        public async Task<byte[]> ReadPmcAsync(short adrType, ushort start, ushort end, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    EnsureConnected();
                    int count = end - start + 1;
                    var buf = new Focas.IODBPMC0();
                    short ret = Focas.cnc_rdpmcrng(_handle, adrType, 0, (ushort)start, (ushort)end, (ushort)(8 + count), buf);
                    if (ret == 0)
                    {
                        var result = new byte[count];
                        Array.Copy(buf.cdata, result, count);
                        return result;
                    }
                    if (ret == -8 && attempt == 0) // 句柄失效→重连重试一次
                    {
                        Console.WriteLine($"[FocasClient] PMC -8, reconnecting...");
                        try { Focas.cnc_freelibhndl(_handle); } catch { }
                        _isConnected = false; _handle = 0;
                        short cr = Focas.cnc_allclibhndl3(_ip, _port, _timeoutCs, out _handle);
                        _isConnected = (cr == 0);
                        Console.WriteLine($"[FocasClient] reconnect ret={cr} hndl={_handle}");
                        if (!_isConnected) throw new IOException($"FANUC 重连失败，错误码：{cr}");
                        continue;
                    }
                    throw new IOException($"FANUC PMC读失败，错误码：{ret}");
                }
                throw new IOException("FANUC PMC读重试失败");
            }
            finally { _lock.Release(); }
        }

        /// <summary>写入 PMC 信号，-8 自动重连重试</summary>
        public async Task WritePmcAsync(short adrType, ushort start, byte[] data, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    EnsureConnected();
                    int end = start + data.Length - 1;
                    var buf = new Focas.IODBPMC0();
                    buf.type_a = adrType; buf.type_d = 0; buf.datano_s = (short)start; buf.datano_e = (short)end;
                    Array.Copy(data, buf.cdata, data.Length);
                    short ret = Focas.cnc_wrpmcrng(_handle, (ushort)(8 + data.Length), buf);
                    if (ret == 0) return;
                    if (ret == -8 && attempt == 0)
                    {
                        Console.WriteLine($"[FocasClient] PMC write -8, reconnecting...");
                        try { Focas.cnc_freelibhndl(_handle); } catch { }
                        _isConnected = false; _handle = 0;
                        short cr = Focas.cnc_allclibhndl3(_ip, _port, _timeoutCs, out _handle);
                        _isConnected = (cr == 0);
                        Console.WriteLine($"[FocasClient] reconnect ret={cr} hndl={_handle}");
                        if (!_isConnected) throw new IOException($"FANUC 重连失败，错误码：{cr}");
                        continue;
                    }
                    throw new IOException($"FANUC PMC写失败，错误码：{ret}");
                }
            }
            finally { _lock.Release(); }
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
            private const string DllName = "Fwlib32";

            /// <summary>宏变量结构体: datano(2)+dummy(2)+mcr_val(4)+dec_val(2)=10字节</summary>
            [StructLayout(LayoutKind.Sequential)]
            public class ODBM
            {
                public short datano;     // 变量编号
                public short dummy;      // 保留
                public int   mcr_val;    // 宏变量值(原始整数)
                public short dec_val;    // 小数位数
            }

            /// <summary>PMC 字节读写缓冲区（IODBPMC0, SizeConst=5, Explicit布局）。</summary>
            [StructLayout(LayoutKind.Explicit)]
            public class IODBPMC0
            {
                [FieldOffset(0)]  public short   type_a;
                [FieldOffset(2)]  public short   type_d;
                [FieldOffset(4)]  public short   datano_s;
                [FieldOffset(6)]  public short   datano_e;
                [FieldOffset(8)]  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
                public byte[]    cdata;
                public IODBPMC0() { cdata = new byte[5]; }
            }

            [DllImport(DllName, EntryPoint = "cnc_allclibhndl3")] public static extern short cnc_allclibhndl3(
                [MarshalAs(UnmanagedType.LPStr)] string ipaddr, ushort port, int timeout, out ushort hndl);

            [DllImport(DllName)] public static extern short cnc_freelibhndl(ushort hndl);

            [DllImport(DllName)] public static extern short cnc_settimeout(ushort hndl, int timeout); // timeout单位=秒

            [DllImport(DllName)] public static extern short cnc_rdmacro(
                ushort hndl, short number, short length, [Out, MarshalAs(UnmanagedType.LPStruct)] ODBM macro);

            [DllImport(DllName)] public static extern short cnc_wrmacro(
                ushort hndl, short number, short length, int mcr_val, short dec_val);

            [DllImport(DllName, EntryPoint = "pmc_rdpmcrng")] public static extern short cnc_rdpmcrng(
                ushort hndl, short type_a, short type_d, ushort datano_s, ushort datano_e, ushort length, [Out, MarshalAs(UnmanagedType.LPStruct)] IODBPMC0 buf);

            [DllImport(DllName, EntryPoint = "pmc_wrpmcrng")] public static extern short cnc_wrpmcrng(
                ushort hndl, ushort length, [In, MarshalAs(UnmanagedType.LPStruct)] IODBPMC0 buf);

            /// <summary>CNC状态结构体（FANUC官方9字段, class+LPStruct, 必须全否则栈溢出）</summary>
            [StructLayout(LayoutKind.Sequential)]
            public class ODBST
            {
                public short dummy;
                public short tmmode;
                public short aut;       // 选择的自动模式
                public short run;       // 0=停止,1=等待,2=运行中,3=MDI,4=急停
                public short motion;    // 轴移动/暂停状态
                public short mstb;      // M/S/T/B状态
                public short emergency; // 急停状态(1=急停)
                public short alarm;     // 报警状态(1=有报警)
                public short edit;      // 编辑状态(1=编辑)
            }

            [DllImport(DllName)] public static extern short cnc_statinfo(
                ushort hndl, [Out, MarshalAs(UnmanagedType.LPStruct)] ODBST stat); // 注意: 无out关键字, 调用方传实例
        }

        /// <summary>读CNC状态, -8自动重连</summary>
        public async Task<(bool ok, string status)> QuickStatAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_isConnected) await ConnectAsync(ct);
                var stat = new Focas.ODBST();
                short ret = Focas.cnc_statinfo(_handle, stat);
                if (ret == -8) // 重连重试
                {
                    Console.WriteLine("[FocasClient] statinfo -8, reconnecting...");
                    try { Focas.cnc_freelibhndl(_handle); } catch { }
                    _isConnected = false; _handle = 0;
                    short cr = Focas.cnc_allclibhndl3(_ip, _port, _timeoutCs, out _handle);
                    _isConnected = (cr == 0);
                    Console.WriteLine($"[FocasClient] reconnect ret={cr} hndl={_handle}");
                    if (!_isConnected) return (false, $"reconnect err:{cr}");
                    stat = new Focas.ODBST();
                    ret = Focas.cnc_statinfo(_handle, stat);
                }
                if (ret != 0) return (false, $"statinfo err:{ret}");
                string runText = stat.run switch { 0 => "停止", 1 => "等待", 2 => "运行中", 3 => "MDI", 4 => "急停", _ => $"未知({stat.run})" };
                return (true, stat.alarm != 0 ? $"报警 run={runText}" : runText);
            }
            catch { return (false, "err"); }
        }

        /// <summary>FANUC 快速探测: 读 CNC 状态(连→读→断, 供StationCard一次性探测用)</summary>
        public async Task<(bool ok, string status)> ProbeAsync(CancellationToken ct = default)
        {
            try
            {
                await ConnectAsync(ct);
                var stat = new Focas.ODBST();
                short ret = Focas.cnc_statinfo(_handle, stat);
                if (ret != 0)
                    return (false, $"statinfo错误码:{ret}");
                string runText = stat.run switch { 0 => "停止", 1 => "等待", 2 => "运行中", 3 => "MDI", 4 => "急停", _ => $"未知({stat.run})" };
                string status = stat.alarm != 0 ? $"报警 run={runText}" : runText;
                return (true, status);
            }
            catch { return (false, "连接失败"); }
            finally { try { await DisconnectAsync(); } catch { } }
        }
    }
}
