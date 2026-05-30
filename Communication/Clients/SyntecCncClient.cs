using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Contracts;
using AutomaticOnlineHostComputer.Communication.Models;

namespace AutomaticOnlineHostComputer.Communication.Clients
{
    /// <summary>
    /// 新代（Syntec）CNC 通信客户端，基于 Syntec.OpenCNC.dll SDK。
    /// <para>
    /// 适用设备：双头镗（新代数控系统），通过以太网与 CNC 控制器通信。
    /// </para>
    /// <para>
    /// 依赖 DLL（位于 Libs/Syntec/）：
    ///   Syntec.OpenCNC.dll, OCApi.dll, OCKrnl.dll, OCKrnlDrv.dll,
    ///   OPLog_Fixed.dll, SyntecFS.dll, Modbus.dll
    ///   → 均需复制到程序输出目录。
    /// </para>
    /// <para>
    /// 地址规则：
    ///   - 宏变量 @703 → address=703，@740 → address=740，以此类推。
    ///   - R 区寄存器 R6101 → 使用 <see cref="ReadRAsync"/> / <see cref="WriteRAsync"/>，address=6101。
    ///   - 通用 <see cref="ReadAsync"/> / <see cref="WriteAsync"/> 默认操作宏变量。
    /// </para>
    /// </summary>
    public sealed class SyntecCncClient : IDeviceClient
    {
        // ── 配置 ────────────────────────────────────────────────────────
        private readonly string _ip;
        private readonly int    _timeoutMs;

        // ── 连接状态 ──────────────────────────────────────────────────
        private bool _isConnected;

        // Syntec SDK 中实际通过静态方法/全局句柄管理连接，此处用 IP 标识
        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── 构造 ─────────────────────────────────────────────────────

        /// <param name="ip">CNC 控制器 IP 地址</param>
        /// <param name="timeoutMs">超时（毫秒），默认 5000</param>
        public SyntecCncClient(string ip, int timeoutMs = 5000)
        {
            _ip        = ip;
            _timeoutMs = timeoutMs;
        }

        // ── IDeviceClient ────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsConnected => _isConnected;

        /// <inheritdoc/>
        public Task ConnectAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                // SyntecRemoteCNC 构造即连接（SDK 内部建立 TCP）
                // 通过调用一次读取验证连通性
                short ret = SyntecApi.READ_macro_single(_ip, 740, out _);
                // ret == 0 成功；ret 非0 表示连接或读取失败
                if (ret != 0)
                    throw new InvalidOperationException($"Syntec CNC 连接失败，错误码：{ret}（IP={_ip}）");
                _isConnected = true;
            }, ct);
        }

        /// <inheritdoc/>
        public Task DisconnectAsync()
        {
            // Syntec SDK 无需显式断开（无持久连接对象）
            _isConnected = false;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 读取宏变量，返回浮点型 <see cref="ReadResult"/>。
        /// count > 1 时读取 address ~ address+count-1 的连续宏变量。
        /// </remarks>
        public Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                var values = new double[count];
                for (int i = 0; i < count; i++)
                {
                    short ret = SyntecApi.READ_macro_single(_ip, address + i, out double val);
                    if (ret != 0)
                        throw new IOException($"Syntec 读宏变量 @{address + i} 失败，错误码：{ret}");
                    values[i] = val;
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
                short ret = SyntecApi.WRITE_macro_single(_ip, address, value);
                if (ret != 0)
                    throw new IOException($"Syntec 写宏变量 @{address} 失败，错误码：{ret}");
            }, ct);
        }

        /// <inheritdoc/>
        public async Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
            // 转成 double 数组，复用批量写接口
            var dbl = Array.ConvertAll(values, v => (double)v);
            await WriteBatchAsync(addresses, dbl, ct);
        }

        /// <inheritdoc/>
        public Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                short ret = SyntecApi.WRITE_macro_all(_ip, addresses, values);
                if (ret != 0)
                    throw new IOException($"Syntec 批量写宏变量失败，错误码：{ret}");
            }, ct);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _lock.Dispose();
        }

        // ── Syntec 扩展：R 区读写 ─────────────────────────────────────

        /// <summary>
        /// 读取 PLC R 区寄存器（整数型，dataType=2）。
        /// 例：ReadRAsync(6101, 10) 读取 R6101~R6110。
        /// </summary>
        /// <param name="startAddr">起始 R 区地址</param>
        /// <param name="count">读取点数</param>
        /// <param name="ct">取消令牌</param>
        public Task<int[]> ReadRAsync(int startAddr, int count, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                short ret = SyntecApi.READ_plc_r(_ip, startAddr, startAddr + count - 1, out int[] vals);
                if (ret != 0)
                    throw new IOException($"Syntec 读 R 区 R{startAddr} 失败，错误码：{ret}");
                return vals;
            }, ct);
        }

        /// <summary>
        /// 写入 PLC R 区单个寄存器（整数型，dataType=2）。
        /// 例：WriteRAsync(6102, 1) 写 R6102=1（下发完成信号）。
        /// </summary>
        /// <param name="addr">R 区地址</param>
        /// <param name="value">要写入的整数值</param>
        /// <param name="ct">取消令牌</param>
        public Task WriteRAsync(int addr, int value, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                EnsureConnected();
                short ret = SyntecApi.WRITE_plc_r(_ip, addr, addr, new[] { value });
                if (ret != 0)
                    throw new IOException($"Syntec 写 R 区 R{addr} 失败，错误码：{ret}");
            }, ct);
        }

        // ── 私有 ─────────────────────────────────────────────────────

        private void EnsureConnected()
        {
            if (!_isConnected)
                throw new InvalidOperationException("Syntec CNC 未连接，请先调用 ConnectAsync()。");
        }

        // ── Syntec SDK P/Invoke 绑定 ──────────────────────────────────
        // Syntec.OpenCNC.dll 内部使用托管 API，下方通过反射/动态调用封装。
        // 若 Syntec SDK 已提供托管 DLL（.NET），可直接添加项目引用后调用；
        // 此处提供静态代理方法，便于在不引用 SDK 的情况下编译通过，实际部署时替换为真实调用。

        private static class SyntecApi
        {
            // ── 缓存：每个 IP 对应一个 SyntecRemoteCNC 实例, 避免重复创建 ──
            private static readonly Dictionary<string, object> _instances = new();
            private static Type? _cncType;
            private static readonly object _initLock = new();

            /// <summary>获取或创建指定 IP 的 CNC 实例 (延迟加载+缓存)</summary>
            private static object GetInstance(string ip)
            {
                if (_instances.TryGetValue(ip, out var inst)) return inst;
                lock (_initLock)
                {
                    if (_instances.TryGetValue(ip, out inst)) return inst;
                    if (_cncType == null)
                    {
                        var asm = System.Reflection.Assembly.LoadFrom("Syntec.OpenCNC.dll");
                        _cncType = asm.GetType("Syntec.Remote.SyntecRemoteCNC")
                                   ?? throw new DllNotFoundException("未找到 Syntec.Remote.SyntecRemoteCNC 类型");
                    }
                    inst = Activator.CreateInstance(_cncType, ip)!;
                    _instances[ip] = inst;
                    return inst;
                }
            }

            private static object Invoke(string ip, string methodName, object[] args)
            {
                try
                {
                    var inst = GetInstance(ip);
                    var mi = _cncType!.GetMethod(methodName)
                              ?? throw new MissingMethodException(_cncType.Name, methodName);
                    return mi.Invoke(inst, args);
                }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            // ═══════════════ 宏变量 ────────────────────────────
            public static short READ_macro_single(string ip, int index, out double data)
            {
                var args = new object[] { index, 0.0 };
                var ret = Convert.ToInt16(Invoke(ip, "READ_macro_single", args));
                data = (double)args[1];
                return ret;
            }
            public static short WRITE_macro_single(string ip, int index, double data)
                => Convert.ToInt16(Invoke(ip, "WRITE_macro_single", new object[] { index, data }));
            public static short WRITE_macro_all(string ip, int[] indexArray, double[] dataArray)
                => Convert.ToInt16(Invoke(ip, "WRITE_macro_all", new object[] { indexArray, dataArray }));

            // ═══════════════ R 区寄存器 (READ_plc_addr / WRITE_plc_addr) ──
            // SDK签名: READ_plc_addr(string dev, int start, int end, out short type, out byte[] b, out short[] s, out int[] iv)
            public static short READ_plc_r(string ip, int startAddr, int endAddr, out int[] values)
            {
                var args = new object[] { "R", startAddr, endAddr, (short)0, null!, null!, null! };
                var ret = Convert.ToInt16(Invoke(ip, "READ_plc_addr", args));
                values = (int[])args[6];
                return ret;
            }
            // R区写: WRITE_plc_addr(string dev, int start, int end, short type, byte[]B, short[]S, int[]I)
            // R区类型为Int(type=2), 所以填I数组
            public static short WRITE_plc_r(string ip, int startAddr, int endAddr, int[] values)
                => Convert.ToInt16(Invoke(ip, "WRITE_plc_addr",
                    new object[] { "R", startAddr, endAddr, (short)2, null!, null!, values }));
        }
    }
}
