using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;
using FanucFocas;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>独立线程轮询返回的信号快照。业务层只读，Worker线程写。</summary>
    public sealed class FanucSignalSnapshot
    {
        public bool Connected { get; set; }
        public bool RqData { get; set; }
        public bool RqLoad { get; set; }
        public bool Clamped { get; set; }
        public bool RqUnload { get; set; }
        public bool Opened { get; set; }
        public string? Error { get; set; }
        public DateTime CapturedAt { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// FANUC斜床通信服务。
    ///
    /// FOCAS句柄由本服务的专用Worker线程创建，并且只允许该Worker线程使用。
    /// 业务线程只负责把读写请求放入队列并等待结果，不能直接调用FanucSdk。
    /// 这样既保留原有业务接口和重试规则，又避免同一句柄跨OS线程使用导致EW_HANDLE(-8)。
    /// </summary>
    public sealed class FanucSkewBedService : IDisposable
    {
        private readonly string _ip;
        private readonly ushort _port;
        private readonly int _connectTimeoutSeconds;
        private readonly FanucSdk _sdk = new();

        // 每台设备一条命令队列。所有连接、读、写、断开都在对应Worker中串行执行。
        // 此队列取代了原SemaphoreSlim：信号量只能防并发，不能保证FOCAS句柄始终由同一OS线程调用。
        private readonly BlockingCollection<IWorkerCommand> _commands =
            new(new ConcurrentQueue<IWorkerCommand>());
        private readonly object _workerLifecycleLock = new();

        private bool _disposed;
        private const int FocasOperationTimeoutSeconds = 3;
        private const int WorkerNormalPollMs = 500;
        private const int WorkerBackoffMaxMs = 30000;
        private const int RetryDelayMs = 1000;

        private Thread? _worker;
        private CancellationTokenSource? _workerCts;
        private int _workerThreadId;
        private FanucSignalSnapshot _latestSnapshot = new();
        private readonly object _snapshotLock = new();

        public FanucSignalSnapshot LatestSnapshot { get { lock (_snapshotLock) return _latestSnapshot; } }
        public bool IsWorkerAlive => _worker?.IsAlive == true;

        /// <param name="connectTimeoutSeconds">
        /// FOCAS连接超时，单位为秒。cnc_allclibhndl3原生接口的timeout参数就是秒，禁止再做毫秒换算。
        /// </param>
        public FanucSkewBedService(string ip, int port = 8193, int connectTimeoutSeconds = 3)
        {
            _ip = ip;
            _port = (ushort)port;
            _connectTimeoutSeconds = Math.Max(1, connectTimeoutSeconds);
        }

        // 这里只读取托管状态，不调用FOCAS原生函数；真正的连接和读写仍严格在Worker线程中。
        public bool IsConnected { get { try { return _sdk.IsConnected; } catch { return false; } } }

        /// <summary>
        /// 启动每台设备独立的FOCAS Worker。重复调用保持幂等，不会创建第二条设备线程。
        /// </summary>
        public void StartPollWorker()
        {
            lock (_workerLifecycleLock)
            {
                ThrowIfDisposed();
                if (_worker != null && _worker.IsAlive) return;

                _workerCts?.Dispose();
                _workerCts = new CancellationTokenSource();
                CancellationToken ct = _workerCts.Token;
                _worker = new Thread(() => WorkerLoop(ct))
                {
                    Name = $"FANUC-{_ip}",
                    IsBackground = true
                };
                _worker.Start();
                Console.WriteLine($"[FANUC-SDK] Worker线程已启动 PLC={_ip}:{_port} Name={_worker.Name}");
            }
        }

        /// <summary>
        /// 停止Worker。FOCAS断开也由Worker自己完成，避免Dispose线程跨线程释放句柄。
        /// </summary>
        public void StopWorker()
        {
            Thread? worker;
            CancellationTokenSource? cts;
            lock (_workerLifecycleLock)
            {
                worker = _worker;
                cts = _workerCts;
                try { cts?.Cancel(); } catch { }
            }

            if (worker != null && worker.IsAlive && worker != Thread.CurrentThread)
            {
                // 连接和单次FOCAS操作均配置为3秒，额外留2秒给Worker执行finally中的句柄释放。
                if (!worker.Join((FocasOperationTimeoutSeconds * 1000) + 2000))
                {
                    Console.WriteLine($"[FANUC-SDK] ⚠ Worker线程停止超时5s PLC={_ip}:{_port}; " +
                                      "为保护句柄线程归属，本线程不会强制调用Disconnect");
                    return;
                }
            }

            lock (_workerLifecycleLock)
            {
                if (ReferenceEquals(_worker, worker))
                {
                    _worker = null;
                    _workerThreadId = 0;
                    _workerCts?.Dispose();
                    _workerCts = null;
                }
            }
            SetSnapshotError("Worker已停止");
        }

        /// <summary>
        /// Worker同时承担两项工作：优先处理业务命令，并在空隙中按500ms刷新#1001~#1005快照。
        /// 离线设备的连接阻塞只发生在自己的Worker，不占用UI线程，也不会拖住其他斜床Worker。
        /// </summary>
        private void WorkerLoop(CancellationToken ct)
        {
            _workerThreadId = Environment.CurrentManagedThreadId;
            int backoffMs = 0;
            DateTime nextPollUtc = DateTime.UtcNow;
            Console.WriteLine($"[FANUC-SDK] Worker进入循环 {DiagnosticContext()}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 业务握手优先，避免正等待#1003等信号时被常规快照轮询额外延迟。
                    if (_commands.TryTake(out IWorkerCommand? immediateCommand))
                    {
                        immediateCommand.Execute(this);
                        continue;
                    }

                    DateTime now = DateTime.UtcNow;
                    if (now >= nextPollUtc)
                    {
                        bool success = PollSnapshotOnce(ct);
                        if (success)
                        {
                            backoffMs = 0;
                            nextPollUtc = DateTime.UtcNow.AddMilliseconds(WorkerNormalPollMs);
                        }
                        else
                        {
                            backoffMs = backoffMs == 0
                                ? WorkerNormalPollMs
                                : Math.Min(backoffMs * 2, WorkerBackoffMaxMs);
                            nextPollUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);
                        }
                        continue;
                    }

                    int waitMs = Math.Max(1, (int)Math.Min(
                        WorkerNormalPollMs,
                        (nextPollUtc - now).TotalMilliseconds));
                    if (_commands.TryTake(out IWorkerCommand? command, waitMs, ct))
                        command.Execute(this);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 正常停止路径，不向业务层制造额外异常日志。
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FANUC-SDK] ❌ Worker异常退出 {DiagnosticContext()} Error={ex.Message}");
            }
            finally
            {
                // 句柄在哪个线程创建，就在哪个线程释放；这是修复EW_HANDLE(-8)的关键约束之一。
                try { _sdk.Disconnect(); }
                catch (Exception ex) { Console.WriteLine($"[FANUC-SDK] ⚠ Worker断开失败 {DiagnosticContext()} Error={ex.Message}"); }

                OperationCanceledException stopped = new("FANUC Worker已停止", ct);
                while (_commands.TryTake(out IWorkerCommand? pending)) pending.Fail(stopped);
                SetSnapshotError(ct.IsCancellationRequested ? "Worker已停止" : "Worker异常退出");
                Console.WriteLine($"[FANUC-SDK] Worker已退出 PLC={_ip}:{_port} WorkerTid={_workerThreadId}");
            }
        }

        private bool PollSnapshotOnce(CancellationToken ct)
        {
            try
            {
                EnsureConnectedOnWorker();
                double rqData = _sdk.GetMacro(FanucSkewBedAddress.RequestData);
                ct.ThrowIfCancellationRequested();
                double rqLoad = _sdk.GetMacro(FanucSkewBedAddress.RequestLoad);
                ct.ThrowIfCancellationRequested();
                double clamped = _sdk.GetMacro(FanucSkewBedAddress.TailstockClamped);
                ct.ThrowIfCancellationRequested();
                double rqUnload = _sdk.GetMacro(FanucSkewBedAddress.RequestUnload);
                ct.ThrowIfCancellationRequested();
                double opened = _sdk.GetMacro(FanucSkewBedAddress.TailstockOpened);

                lock (_snapshotLock)
                {
                    _latestSnapshot = new FanucSignalSnapshot
                    {
                        Connected = true,
                        RqData = Math.Abs(rqData) > 0.01,
                        RqLoad = Math.Abs(rqLoad) > 0.01,
                        Clamped = Math.Abs(clamped) > 0.01,
                        RqUnload = Math.Abs(rqUnload) > 0.01,
                        Opened = Math.Abs(opened) > 0.01,
                        CapturedAt = DateTime.UtcNow
                    };
                }
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DisconnectOnWorker("快照读取失败");
                SetSnapshotError(ex.Message);
                Console.WriteLine($"[FANUC-SDK] Worker快照失败 {DiagnosticContext()} Error={ex.Message}; 将按退避周期重连");
                return false;
            }
        }

        private void SetSnapshotError(string error)
        {
            lock (_snapshotLock)
            {
                _latestSnapshot = new FanucSignalSnapshot
                {
                    Connected = false,
                    Error = error,
                    CapturedAt = DateTime.UtcNow
                };
            }
        }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                // SafeCall已保证连接存在；再读#1000用于验证新句柄确实可执行实际FOCAS操作。
                double value = _sdk.GetMacro(FanucSkewBedAddress.MachineReady);
                Console.WriteLine($"[FANUC-SDK] 连接验证 {DiagnosticContext()} #{FanucSkewBedAddress.MachineReady}={value:F1}");
            }, "连接并验证", ct);
        }

        public Task DisconnectAsync()
        {
            return EnqueueRawAsync(() =>
            {
                _sdk.Disconnect();
                Console.WriteLine($"[FANUC-SDK] 主动断开 PLC={_ip}:{_port} WorkerTid={_workerThreadId}");
            });
        }

        /// <summary>仅能由Worker调用；建立会话并设置FOCAS单次操作超时。</summary>
        private void EnsureConnectedOnWorker()
        {
            AssertWorkerThread();
            if (_sdk.IsConnected) return;

            Console.WriteLine($"[FANUC-SDK] 连接 PLC={_ip}:{_port} ConnectTimeout={_connectTimeoutSeconds}s WorkerTid={_workerThreadId}");
            if (!_sdk.Connect(_ip, _port, _connectTimeoutSeconds))
                throw new InvalidOperationException($"FANUC连接失败 PLC={_ip}:{_port} Timeout={_connectTimeoutSeconds}s");

            ApplyOperationTimeoutOnWorker();
            Console.WriteLine($"[FANUC-SDK] ✓ 连接成功 {DiagnosticContext()}");
        }

        private void ApplyOperationTimeoutOnWorker()
        {
            AssertWorkerThread();
            Focas1.cnc_settimeout(_sdk.Handle, FocasOperationTimeoutSeconds);
            Console.WriteLine($"[FANUC-SDK] 会话操作超时={FocasOperationTimeoutSeconds}s {DiagnosticContext()}");
        }

        private void DisconnectOnWorker(string reason)
        {
            AssertWorkerThread();
            if (!_sdk.IsConnected) return;
            string context = DiagnosticContext();
            try
            {
                _sdk.Disconnect();
                Console.WriteLine($"[FANUC-SDK] 断开并准备重建会话 Reason={reason} {context}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FANUC-SDK] ⚠ 断开失败 Reason={reason} {context} Error={ex.Message}");
            }
        }

        // ─── Worker命令与原有三次重连保护 ─────────────────────────

        private Task SafeCallAsync(Action action, string desc, CancellationToken ct = default, int maxAttempts = 3)
            => EnqueueAsync(() => { action(); return true; }, desc, ct, maxAttempts);

        private Task<T> SafeCallAsync<T>(Func<T> func, string desc, CancellationToken ct = default, int maxAttempts = 3)
            => EnqueueAsync(func, desc, ct, maxAttempts);

        private Task<T> EnqueueAsync<T>(Func<T> func, string desc, CancellationToken ct, int maxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            EnsureWorkerStarted();
            WorkerCommand<T> command = new(func, desc, ct, Math.Max(1, maxAttempts), useReconnectProtection: true);
            AddCommand(command);
            return command.Task;
        }

        private Task EnqueueRawAsync(Action action, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureWorkerStarted();
            WorkerCommand<bool> command = new(
                () => { action(); return true; },
                "内部命令",
                ct,
                maxAttempts: 1,
                useReconnectProtection: false);
            AddCommand(command);
            return command.Task;
        }

        private void AddCommand(IWorkerCommand command)
        {
            try { _commands.Add(command); }
            catch (InvalidOperationException)
            {
                command.Fail(new ObjectDisposedException(nameof(FanucSkewBedService)));
            }
        }

        private void EnsureWorkerStarted()
        {
            ThrowIfDisposed();
            if (_worker?.IsAlive == true) return;
            StartPollWorker();
        }

        private T ExecuteWithRetryOnWorker<T>(Func<T> operation, string desc, CancellationToken ct, int maxAttempts)
        {
            AssertWorkerThread();
            Exception? lastError = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    EnsureConnectedOnWorker();
                    return operation();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    bool hadConnectedSession = _sdk.IsConnected;
                    Console.WriteLine($"[FANUC-SDK] {(attempt < maxAttempts ? "⚠" : "❌")} {desc}失败 " +
                                      $"Attempt={attempt}/{maxAttempts} {DiagnosticContext()} Error={ex.Message}");

                    if (attempt >= maxAttempts) break;

                    DisconnectOnWorker($"{desc}第{attempt}次失败");
                    if (hadConnectedSession)
                    {
                        // 已有会话中的读写失败（例如EW_HANDLE）不额外等待，下一次循环立即重建句柄。
                        Console.WriteLine($"[FANUC-SDK] 准备立即重连并重试 {desc} PLC={_ip}:{_port} WorkerTid={_workerThreadId}");
                    }
                    else
                    {
                        // 连句柄都未建立成功时等待1秒，避免未上电设备在三次尝试之间形成紧密连接风暴。
                        Console.WriteLine($"[FANUC-SDK] 连接未建立，{RetryDelayMs}ms后重试 {desc} " +
                                          $"PLC={_ip}:{_port} WorkerTid={_workerThreadId}");
                        if (ct.WaitHandle.WaitOne(RetryDelayMs)) ct.ThrowIfCancellationRequested();
                    }
                }
            }

            // 保留最后一次原始异常类型（例如FocasException），避免改变上层已有catch判断和业务分支。
            ExceptionDispatchInfo.Capture(lastError ?? new InvalidOperationException($"{desc}执行失败")).Throw();
            throw new InvalidOperationException($"{desc}执行失败"); // 编译器可达性占位，运行时不会到达。
        }

        /// <summary>
        /// 同步入口仍保持同步语义，但实际FOCAS写入由Worker完成；调用方只阻塞等待结果。
        /// </summary>
        private void SafeCallSync(Action action, string desc)
        {
            if (Environment.CurrentManagedThreadId == _workerThreadId)
            {
                ExecuteWithRetryOnWorker(() => { action(); return true; }, desc, CancellationToken.None, 3);
                return;
            }
            SafeCallAsync(action, desc).GetAwaiter().GetResult();
        }

        private void AssertWorkerThread()
        {
            if (_workerThreadId == 0 || Environment.CurrentManagedThreadId != _workerThreadId)
            {
                throw new InvalidOperationException(
                    $"禁止跨线程调用FOCAS句柄 PLC={_ip}:{_port}; " +
                    $"CurrentTid={Environment.CurrentManagedThreadId}, WorkerTid={_workerThreadId}");
            }
        }

        private string DiagnosticContext()
        {
            string handle = "none";
            try { if (_sdk.IsConnected) handle = _sdk.Handle.ToString(); } catch { }
            return $"PLC={_ip}:{_port} Handle={handle} WorkerTid={_workerThreadId}";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FanucSkewBedService));
        }

        // ─── 读写宏变量（public业务接口及地址均保持不变） ───────────

        private Task<bool> ReadMacroBoolAsync(int addr, CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                double value = _sdk.GetMacro(addr);
                bool result = Math.Abs(value) > 0.01;
                Console.WriteLine($"[FANUC-SDK] 读宏变量 #{addr}={value:F1} → {result}");
                return result;
            }, $"读 #{addr}", ct);
        }

        private Task WriteMacroBoolAsync(int addr, bool value, CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                _sdk.SetMacro(addr, value ? 1.0 : 0.0);
                Console.WriteLine($"[FANUC-SDK] 写宏变量 #{addr}={(value ? 1 : 0)} OK");
            }, $"写 #{addr}", ct);
        }

        /// <summary>安全写宏变量(public, 供引擎中清信号等同步调用)。</summary>
        public void SafeSetMacro(int addr, double value)
        {
            SafeCallSync(() =>
            {
                _sdk.SetMacro(addr, value);
                Console.WriteLine($"[FANUC-SDK] SafeSetMacro #{addr}={value} OK");
            }, $"SafeSet #{addr}");
        }

        public Task<(bool rqData, bool rqLoad, bool clamped, bool rqUnload, bool opened)> ReadSignalsAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                bool rqData = Math.Abs(_sdk.GetMacro(FanucSkewBedAddress.RequestData)) > 0.01;
                ct.ThrowIfCancellationRequested();
                bool rqLoad = Math.Abs(_sdk.GetMacro(FanucSkewBedAddress.RequestLoad)) > 0.01;
                ct.ThrowIfCancellationRequested();
                bool clamped = Math.Abs(_sdk.GetMacro(FanucSkewBedAddress.TailstockClamped)) > 0.01;
                ct.ThrowIfCancellationRequested();
                bool rqUnload = Math.Abs(_sdk.GetMacro(FanucSkewBedAddress.RequestUnload)) > 0.01;
                ct.ThrowIfCancellationRequested();
                bool opened = Math.Abs(_sdk.GetMacro(FanucSkewBedAddress.TailstockOpened)) > 0.01;
                Console.WriteLine($"[FANUC-SDK] 快照 #{FanucSkewBedAddress.RequestData}={rqData} #{FanucSkewBedAddress.RequestLoad}={rqLoad} #{FanucSkewBedAddress.TailstockClamped}={clamped} #{FanucSkewBedAddress.RequestUnload}={rqUnload} #{FanucSkewBedAddress.TailstockOpened}={opened}");
                return (rqData, rqLoad, clamped, rqUnload, opened);
            }, "读斜床信号快照", ct, maxAttempts: 1);
        }

        public Task<bool> IsMachineReadyAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.MachineReady, ct);
        public Task<bool> IsRequestDataAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestData, ct);
        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestLoad, ct);
        public Task<bool> IsTailstockClampedAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.TailstockClamped, ct);
        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestUnload, ct);
        public Task<bool> IsTailstockOpenedAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.TailstockOpened, ct);

        public async Task SendMachiningParamsAsync(double rollerLength, double rollerDiameter, double borePlugSize, int mode, CancellationToken ct = default)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await SafeCallAsync(() =>
                {
                    Console.WriteLine($"[FANUC-SDK] 写加工参数 attempt={attempt}: L={rollerLength} D={rollerDiameter} bore={borePlugSize} mode={mode}");
                    _sdk.SetMacro(FanucSkewBedAddress.RollerLength, rollerLength);
                    _sdk.SetMacro(FanucSkewBedAddress.RollerDiameter, rollerDiameter);
                    _sdk.SetMacro(FanucSkewBedAddress.BorePlugSize, borePlugSize);
                    _sdk.SetMacro(FanucSkewBedAddress.MachiningMode, mode);
                }, "写加工参数", ct);

                if (await VerifyMachiningParamsAsync(rollerLength, rollerDiameter, borePlugSize, mode, attempt, ct))
                    break;

                if (attempt == maxAttempts)
                    throw new InvalidOperationException($"FANUC加工参数写入校验失败, 已禁止写#{FanucSkewBedAddress.DataSentDone}=1");

                await Task.Delay(100, ct);
            }

            await WriteMacroBoolAsync(FanucSkewBedAddress.DataSentDone, true, ct);
        }

        private Task<bool> VerifyMachiningParamsAsync(double rollerLength, double rollerDiameter, double borePlugSize, int mode, int attempt, CancellationToken ct)
        {
            return SafeCallAsync(() =>
            {
                double length = _sdk.GetMacro(FanucSkewBedAddress.RollerLength);
                double diameter = _sdk.GetMacro(FanucSkewBedAddress.RollerDiameter);
                double bore = _sdk.GetMacro(FanucSkewBedAddress.BorePlugSize);
                double actualMode = _sdk.GetMacro(FanucSkewBedAddress.MachiningMode);

                bool ok = IsClose(length, rollerLength) &&
                          IsClose(diameter, rollerDiameter) &&
                          IsClose(bore, borePlugSize) &&
                          (int)Math.Round(actualMode) == mode;

                Console.WriteLine($"[FANUC-SDK] 加工参数读回 attempt={attempt}: #{FanucSkewBedAddress.RollerLength}={length} #{FanucSkewBedAddress.RollerDiameter}={diameter} #{FanucSkewBedAddress.BorePlugSize}={bore} #{FanucSkewBedAddress.MachiningMode}={actualMode} → {(ok ? "OK" : "NG")}");
                return ok;
            }, "校验加工参数", ct);
        }

        private static bool IsClose(double actual, double expected) => Math.Abs(actual - expected) <= 0.01;

        public Task SetCraneLoadInPlaceAsync(bool value, CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneLoadInPlace, value, ct);
        public Task SetCraneLoadDoneAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneLoadDone, true, ct);
        public Task SetCraneUnloadInPlaceAsync(bool value, CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneUnloadInPlace, value, ct);
        public Task SetCraneUnloadDoneAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneUnloadDone, true, ct);
        public Task StopTailstockAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.TailstockStop, true, ct);

        /// <summary>
        /// 应急清零：清掉上位机下发过的加工参数和握手信号。
        /// 仅用于人工确认现场已处理后的应急恢复，不控制天车/机床动作。
        /// </summary>
        public Task ClearEmergencyRegistersAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                int[] addrs =
                {
                    FanucSkewBedAddress.RollerLength,
                    FanucSkewBedAddress.RollerDiameter,
                    FanucSkewBedAddress.BorePlugSize,
                    FanucSkewBedAddress.MachiningMode,
                    FanucSkewBedAddress.DataSentDone,
                    FanucSkewBedAddress.CraneLoadInPlace,
                    FanucSkewBedAddress.CraneLoadDone,
                    FanucSkewBedAddress.CraneUnloadInPlace,
                    FanucSkewBedAddress.CraneUnloadDone
                };
                foreach (int addr in addrs)
                {
                    ct.ThrowIfCancellationRequested();
                    _sdk.SetMacro(addr, 0.0);
                }
                Console.WriteLine("[FANUC-SDK] 应急清零: #800/#801/#802/#909/#1101~#1105 → 0");
            }, "FANUC应急清零", ct);
        }

        public Task<string> TestPmcAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                try
                {
                    StringBuilder sb = new();
                    sb.Append("宏变量: ");
                    for (int i = 1000; i <= 1005; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { double value = _sdk.GetMacro(i); sb.Append($"#{i}={value:F1} "); }
                        catch (FocasException ex) { sb.Append($"#{i}=E{ex.ErrorCode} "); }
                    }
                    sb.Append($"| 状态: {_sdk.GetStatus()}");
                    return sb.ToString();
                }
                catch (Exception ex) { return $"扫描失败: {ex.Message}"; }
            }, "PMC扫描", ct, maxAttempts: 1);
        }

        public Task<string> ProbeStatusAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                try { return _sdk.GetStatus().RunStatusText; }
                catch { return "disconnected"; }
            }, "读取statinfo", ct, maxAttempts: 1);
        }

        public void Dispose()
        {
            lock (_workerLifecycleLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            StopWorker();
            // 正常情况下StopWorker会在5秒内等到Worker退出，此时可以安全释放队列。
            // 极端情况下若原生DLL调用超过停止等待时间，Worker仍可能在finally中访问队列；
            // 此时宁可让该服务对象随进程回收，也不能提前Dispose队列造成第二个线程异常。
            if (_worker?.IsAlive != true)
            {
                _commands.CompleteAdding();
                _commands.Dispose();
            }
        }

        private interface IWorkerCommand
        {
            void Execute(FanucSkewBedService owner);
            void Fail(Exception exception);
        }

        private sealed class WorkerCommand<T> : IWorkerCommand
        {
            private readonly Func<T> _operation;
            private readonly string _description;
            private readonly CancellationToken _cancellationToken;
            private readonly int _maxAttempts;
            private readonly bool _useReconnectProtection;
            private readonly TaskCompletionSource<T> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public WorkerCommand(
                Func<T> operation,
                string description,
                CancellationToken cancellationToken,
                int maxAttempts,
                bool useReconnectProtection)
            {
                _operation = operation;
                _description = description;
                _cancellationToken = cancellationToken;
                _maxAttempts = maxAttempts;
                _useReconnectProtection = useReconnectProtection;
            }

            public Task<T> Task => _completion.Task;

            public void Execute(FanucSkewBedService owner)
            {
                if (_completion.Task.IsCompleted) return;
                try
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    T result = _useReconnectProtection
                        ? owner.ExecuteWithRetryOnWorker(_operation, _description, _cancellationToken, _maxAttempts)
                        : _operation();
                    _completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    _completion.TrySetCanceled(_cancellationToken);
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                }
            }

            public void Fail(Exception exception)
            {
                if (exception is OperationCanceledException)
                    _completion.TrySetCanceled();
                else
                    _completion.TrySetException(exception);
            }
        }
    }
}
