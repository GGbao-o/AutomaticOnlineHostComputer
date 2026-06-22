using System;
using System.Collections.Concurrent;
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
        private readonly string _ip;
        private readonly int _timeoutMs;
        private volatile bool _isConnected;
        private volatile bool _workerUnavailable;
        private int _workerExecuting;
        private int _disposed;

        // Syntec OpenCNC调用是同步SDK。每台CNC固定使用一个后台线程串行调用，
        // 不能再把阻塞调用扔进.NET线程池，否则半开连接会拖慢UI和其它引擎。
        private readonly BlockingCollection<IWorkItem> _workQueue = new();
        private readonly Thread _workerThread;

        private interface IWorkItem
        {
            void Execute();
            void Abandon(CancellationToken cancellationToken);
        }

        private sealed class WorkItem<T> : IWorkItem
        {
            private readonly Func<T> _action;
            private readonly TaskCompletionSource<T> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _state; // 0=排队, 1=执行中, 2=结束, 3=排队时已取消

            public WorkItem(Func<T> action) => _action = action;
            public Task<T> Completion => _completion.Task;

            public void Execute()
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
                try
                {
                    _completion.TrySetResult(_action());
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _state, 2);
                }
            }

            public void Abandon(CancellationToken cancellationToken)
            {
                // 只阻止尚未开始的旧命令。同步SDK一旦进入就无法安全强停；
                // 调用超时后客户端会进入隔离状态，禁止后续命令与旧调用并发。
                if (Interlocked.CompareExchange(ref _state, 3, 0) == 0)
                    _completion.TrySetCanceled(cancellationToken);
            }
        }

        public SyntecCncClient(string ip, int timeoutMs = 5000)
        {
            _ip = ip;
            _timeoutMs = Math.Max(timeoutMs, 1000);
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"SyntecWorker-{ip}"
            };
            _workerThread.Start();
        }

        public bool IsConnected => _isConnected && !_workerUnavailable;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                await InvokeWorkerAsync(() =>
                {
                    short ret = SyntecApi.READ_macro_single(_ip, 740, out _);
                    if (ret != 0)
                        throw new InvalidOperationException($"Syntec CNC 连接失败，错误码：{ret}（IP={_ip}）");
                    return true;
                }, "连接验证", ct).ConfigureAwait(false);
                _isConnected = true;
            }
            catch
            {
                _isConnected = false;
                throw;
            }
        }

        public Task DisconnectAsync()
        {
            // SDK按IP维护内部实例，没有公开的可中断断开接口。这里只撤销业务可用状态。
            _isConnected = false;
            return Task.CompletedTask;
        }

        public async Task<ReadResult> ReadAsync(int address, int count = 1, CancellationToken ct = default)
        {
            return await ExecuteConnectedAsync(() =>
            {
                var values = new double[count];
                for (int i = 0; i < count; i++)
                {
                    short ret = SyntecApi.READ_macro_single(_ip, address + i, out double val);
                    if (ret != 0)
                        throw new IOException($"Syntec 读宏变量 @{address + i} 失败，错误码：{ret}");
                    values[i] = val;
                }
                return new ReadResult(values);
            }, $"读取宏变量@{address}", ct).ConfigureAwait(false);
        }

        public async Task<double> ReadDoubleAsync(int address, CancellationToken ct = default)
            => (await ReadAsync(address, 1, ct).ConfigureAwait(false)).FirstDouble;

        public async Task<int> ReadIntAsync(int address, CancellationToken ct = default)
            => (await ReadAsync(address, 1, ct).ConfigureAwait(false)).FirstInt;

        public Task WriteAsync(int address, int value, CancellationToken ct = default)
            => WriteAsync(address, (double)value, ct);

        public async Task WriteAsync(int address, double value, CancellationToken ct = default)
        {
            await ExecuteConnectedAsync(() =>
            {
                short ret = SyntecApi.WRITE_macro_single(_ip, address, value);
                if (ret != 0)
                    throw new IOException($"Syntec 写宏变量 @{address} 失败，错误码：{ret}");
                return true;
            }, $"写宏变量@{address}", ct).ConfigureAwait(false);
        }

        public async Task WriteBatchAsync(int[] addresses, int[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");
            await WriteBatchAsync(addresses, Array.ConvertAll(values, v => (double)v), ct).ConfigureAwait(false);
        }

        public async Task WriteBatchAsync(int[] addresses, double[] values, CancellationToken ct = default)
        {
            if (addresses.Length != values.Length)
                throw new ArgumentException("addresses 与 values 数组长度不一致");

            await ExecuteConnectedAsync(() =>
            {
                short ret = SyntecApi.WRITE_macro_all(_ip, addresses, values);
                if (ret != 0)
                    throw new IOException($"Syntec 批量写宏变量失败，错误码：{ret}");
                return true;
            }, "批量写宏变量", ct).ConfigureAwait(false);
        }

        public async Task<int[]> ReadRAsync(int startAddr, int count, CancellationToken ct = default)
        {
            return await ExecuteConnectedAsync(() =>
            {
                short ret = SyntecApi.READ_plc_r(_ip, startAddr, startAddr + count - 1, out int[] vals);
                if (ret != 0)
                    throw new IOException($"Syntec 读 R 区 R{startAddr} 失败，错误码：{ret}");
                return vals;
            }, $"读取R{startAddr}~R{startAddr + count - 1}", ct).ConfigureAwait(false);
        }

        public async Task WriteRAsync(int addr, int value, CancellationToken ct = default)
        {
            await ExecuteConnectedAsync(() =>
            {
                short ret = SyntecApi.WRITE_plc_r(_ip, addr, addr, new[] { value });
                if (ret != 0)
                    throw new IOException($"Syntec 写 R 区 R{addr} 失败，错误码：{ret}");
                return true;
            }, $"写R{addr}", ct).ConfigureAwait(false);
        }

        private async Task<T> ExecuteConnectedAsync<T>(
            Func<T> action, string operation, CancellationToken ct)
        {
            EnsureConnected();
            try
            {
                return await InvokeWorkerAsync(action, operation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _isConnected = false;
                throw;
            }
        }

        private async Task<T> InvokeWorkerAsync<T>(
            Func<T> action, string operation, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_workerUnavailable)
                throw new IOException($"Syntec {_ip} 通信Worker已隔离，拒绝{operation}；请重启上位机恢复该CNC通信");

            var item = new WorkItem<T>(action);
            try
            {
                _workQueue.Add(item, ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new ObjectDisposedException(nameof(SyntecCncClient), ex.Message);
            }

            try
            {
                return await item.Completion
                    .WaitAsync(TimeSpan.FromMilliseconds(_timeoutMs), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                item.Abandon(ct);
                throw;
            }
            catch (TimeoutException ex)
            {
                item.Abandon(CancellationToken.None);
                _isConnected = false;
                _workerUnavailable = true;
                // 若任务在真正开始前就已超时，Worker没有被同步SDK占住，可立即允许下一轮重连。
                if (Volatile.Read(ref _workerExecuting) == 0) _workerUnavailable = false;
                Console.WriteLine($"[SyntecWorker] {_ip} {operation} 超时({_timeoutMs}ms)，已隔离该Worker，防止阻塞扩散");
                throw new TimeoutException(
                    $"Syntec {_ip} {operation} 超时({_timeoutMs}ms)，通信Worker已隔离；本线路停止使用该CNC，另一线路不受影响", ex);
            }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var item in _workQueue.GetConsumingEnumerable())
                {
                    Interlocked.Exchange(ref _workerExecuting, 1);
                    try
                    {
                        item.Execute();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _workerExecuting, 0);
                        if (_workerUnavailable)
                        {
                            // 超时只能隔离正在执行的同步调用。若SDK后来返回，允许现有后台重连重新验证；
                            // _isConnected仍为false，业务不会直接沿用这次迟到结果。
                            _workerUnavailable = false;
                            Console.WriteLine($"[SyntecWorker] {_ip} 阻塞调用已返回，解除隔离并等待重新连接验证");
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // 应用退出期间正常收尾。
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Syntec CNC 未连接，请先调用 ConnectAsync()。");
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            _isConnected = false;
            _workQueue.CompleteAdding();
            if (_workerThread.Join(200))
                _workQueue.Dispose();
            return ValueTask.CompletedTask;
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
                // 两台双头镗的专用Worker可能同时首次连接；普通Dictionary的读写必须全部受同一把锁保护。
                lock (_initLock)
                {
                    if (_instances.TryGetValue(ip, out var inst)) return inst;
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
