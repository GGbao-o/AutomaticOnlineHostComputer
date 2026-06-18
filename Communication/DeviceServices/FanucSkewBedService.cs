using System;
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

    public sealed class FanucSkewBedService : IDisposable
    {
        private readonly string _ip;
        private readonly ushort _port;
        private readonly int _timeoutMs;
        private readonly SemaphoreSlim _sem = new(1, 1); // 防止Worker读取和握手写入同时抢SDK
        private readonly FanucSdk _sdk = new(); // 每台FANUC独立实例, 不再共享全局static锁
        private bool _disposed;
        private const int FocasOperationTimeoutSeconds = 3;
        private const int WorkerNormalPollMs = 500;
        private const int WorkerBackoffMaxMs = 30000;

        // ── 独立轮询线程(不占.NET线程池, FANUC DLL阻塞不拖慢UI) ──
        private Thread? _worker;
        private CancellationTokenSource? _workerCts;
        private FanucSignalSnapshot _latestSnapshot = new();
        private readonly object _snapshotLock = new();

        public FanucSignalSnapshot LatestSnapshot { get { lock (_snapshotLock) return _latestSnapshot; } }
        public bool IsWorkerAlive => _worker?.IsAlive == true;

        /// <param name=\"timeoutMs\">连接超时(ms), 默认300→3s(FANUC SDK内部/10后×100ms)</param>
        public FanucSkewBedService(string ip, int port = 8193, int timeoutMs = 300)
        {
            _ip = ip; _port = (ushort)port; _timeoutMs = timeoutMs;
        }

        public bool IsConnected { get { try { return _sdk.IsConnected; } catch { return false; } } }

        /// <summary>启动独立轮询线程。</summary>
        public void StartPollWorker()
        {
            if (_worker != null && _worker.IsAlive) return;
            _workerCts?.Cancel(); _workerCts?.Dispose();
            _workerCts = new CancellationTokenSource();
            var ct = _workerCts.Token;
            _worker = new Thread(() => WorkerLoop(ct)) { Name = $"FANUC-{_ip}", IsBackground = true };
            _worker.Start();
            Console.WriteLine($"[FANUC-SDK] Worker线程已启动 {_ip}:{_port} Name={_worker.Name}");
        }

        /// <summary>停止轮询线程。</summary>
        public void StopWorker()
        {
            try { _workerCts?.Cancel(); } catch { }
            if (_worker != null && _worker.IsAlive)
            {
                if (!_worker.Join(3000))
                    Console.WriteLine($"[FANUC-SDK] ⚠ Worker线程停止超时3s {_ip}:{_port}");
            }
            _workerCts?.Dispose();
            _workerCts = null;
            _worker = null;
            SetSnapshotError("Worker已停止");
        }

        private void WorkerLoop(CancellationToken ct)
        {
            int backoffMs = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _sem.Wait(ct);
                    try
                    {
                        if (!_sdk.IsConnected)
                        {
                            Console.WriteLine($"[FANUC-SDK] Worker连接 {_ip}:{_port}...");
                            if (!_sdk.Connect(_ip, _port, _timeoutMs))
                                throw new InvalidOperationException("连接失败");
                            Console.WriteLine($"[FANUC-SDK] Worker连接成功 {_ip}:{_port}");
                            ApplyOperationTimeout();
                        }

                        double rqData = _sdk.GetMacro(FanucSkewBedAddress.RequestData);
                        ct.ThrowIfCancellationRequested();
                        double rqLoad = _sdk.GetMacro(FanucSkewBedAddress.RequestLoad);
                        ct.ThrowIfCancellationRequested();
                        double clamped = _sdk.GetMacro(FanucSkewBedAddress.TailstockClamped);
                        ct.ThrowIfCancellationRequested();
                        double rqUnload = _sdk.GetMacro(FanucSkewBedAddress.RequestUnload);
                        ct.ThrowIfCancellationRequested();
                        double opened = _sdk.GetMacro(FanucSkewBedAddress.TailstockOpened);

                        var snap = new FanucSignalSnapshot
                        {
                            Connected = true,
                            RqData = Math.Abs(rqData) > 0.01,
                            RqLoad = Math.Abs(rqLoad) > 0.01,
                            Clamped = Math.Abs(clamped) > 0.01,
                            RqUnload = Math.Abs(rqUnload) > 0.01,
                            Opened = Math.Abs(opened) > 0.01,
                            CapturedAt = DateTime.UtcNow
                        };
                        lock (_snapshotLock) _latestSnapshot = snap;
                        backoffMs = 0;
                    }
                    finally { _sem.Release(); }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    try { _sdk.Disconnect(); } catch { }
                    SetSnapshotError(ex.Message);
                    if (backoffMs == 0) backoffMs = WorkerNormalPollMs;
                    else backoffMs = Math.Min(backoffMs * 2, WorkerBackoffMaxMs);
                    Console.WriteLine($"[FANUC-SDK] Worker {_ip}:{_port} 读失败: {ex.Message}, 退避{backoffMs}ms后重连");
                }

                try { Task.Delay(backoffMs == 0 ? WorkerNormalPollMs : backoffMs, ct).Wait(ct); }
                catch (OperationCanceledException) { break; }
            }
            SetSnapshotError(ct.IsCancellationRequested ? "Worker已停止" : "Worker异常退出");
        }

        private void SetSnapshotError(string error)
        {
            lock (_snapshotLock) _latestSnapshot = new FanucSignalSnapshot { Connected = false, Error = error, CapturedAt = DateTime.UtcNow };
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                Console.WriteLine($"[FANUC-SDK] 连接 {_ip}:{_port} timeout={_timeoutMs}ms");
                bool ok = _sdk.Connect(_ip, _port, _timeoutMs);
                if (!ok) throw new InvalidOperationException($"FANUC连接失败 {_ip}:{_port}");
                Console.WriteLine($"[FANUC-SDK] ✓ 连接成功 hndl={_sdk.Handle}");
                ApplyOperationTimeout();
                try { double v = _sdk.GetMacro(1000); Console.WriteLine($"[FANUC-SDK] 连接验证 #1000={v:F1}"); }
                catch (FocasException ex) { Console.WriteLine($"[FANUC-SDK] ⚠ 连接验证失败 err={ex.ErrorCode}, 重连..."); _sdk.Disconnect(); ok = _sdk.Connect(_ip, _port, _timeoutMs); if (!ok) throw new InvalidOperationException("重连失败"); ApplyOperationTimeout(); }
            }
            finally { _sem.Release(); }
        }

        public async Task DisconnectAsync() { await _sem.WaitAsync(); try { _sdk.Disconnect(); } finally { _sem.Release(); } }

        // ─── 异步重连保护 (轮询/握手路径, 不阻塞线程池) ──────────

        private void ApplyOperationTimeout()
        {
            Focas1.cnc_settimeout(_sdk.Handle, FocasOperationTimeoutSeconds);
            Console.WriteLine($"[FANUC-SDK] 会话超时={FocasOperationTimeoutSeconds}秒");
        }

        private async Task SafeCallAsync(Func<Task> f, string desc, CancellationToken ct = default, int maxAttempts = 3)
        {
            await _sem.WaitAsync(ct); // 异步等待, 不阻塞线程
            try
            {
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await f(); return; }
                    catch (Exception ex) when (attempt < maxAttempts - 1)
                    {
                        Console.WriteLine($"[FANUC-SDK] {desc} 异常 attempt={attempt}: {ex.Message}, 重连...");
                        try { _sdk.Disconnect(); } catch { }
                        if (!_sdk.Connect(_ip, _port, _timeoutMs))
                        { Console.WriteLine($"[FANUC-SDK] 重连失败, 等1s重试..."); await Task.Delay(1000, ct); continue; }
                        ApplyOperationTimeout();
                        Console.WriteLine($"[FANUC-SDK] 重连成功, 重试操作");
                    }
                }
                throw new InvalidOperationException($"{desc} {maxAttempts}次重试均失败");
            }
            finally { _sem.Release(); }
        }

        private async Task<T> SafeCallAsync<T>(Func<Task<T>> f, string desc, CancellationToken ct = default, int maxAttempts = 3)
        {
            await _sem.WaitAsync(ct);
            try
            {
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try { return await f(); }
                    catch (Exception ex) when (attempt < maxAttempts - 1)
                    {
                        Console.WriteLine($"[FANUC-SDK] {desc} 异常 attempt={attempt}: {ex.Message}, 重连...");
                        try { _sdk.Disconnect(); } catch { }
                        if (!_sdk.Connect(_ip, _port, _timeoutMs))
                        { Console.WriteLine($"[FANUC-SDK] 重连失败, 等1s重试..."); await Task.Delay(1000, ct); continue; }
                        ApplyOperationTimeout();
                        Console.WriteLine($"[FANUC-SDK] 重连成功, 重试操作");
                    }
                }
                throw new InvalidOperationException($"{desc} {maxAttempts}次重试均失败");
            }
            finally { _sem.Release(); }
        }

        // ─── 同步重连保护 (SafeSetMacro 由引擎同步调用) ──────────

        private void SafeCallSync(Action f, string desc)
        {
            _sem.Wait(); // 同步调用者无法await, 保留阻塞Wait
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { f(); return; }
                    catch (Exception ex) when (attempt < 2)
                    {
                        Console.WriteLine($"[FANUC-SDK] {desc} 异常 attempt={attempt}: {ex.Message}, 重连...");
                        try { _sdk.Disconnect(); } catch { }
                        if (!_sdk.Connect(_ip, _port, _timeoutMs))
                        { Console.WriteLine($"[FANUC-SDK] 重连失败, 等1s重试..."); Thread.Sleep(1000); continue; }
                        ApplyOperationTimeout();
                        Console.WriteLine($"[FANUC-SDK] 重连成功, 重试操作");
                    }
                }
                throw new InvalidOperationException($"{desc} 3次重试均失败");
            }
            finally { _sem.Release(); }
        }

        // ─── 读写宏变量 ───────────────────────────────────────────

        private Task<bool> ReadMacroBoolAsync(int addr, CancellationToken ct = default)
        {
            return SafeCallAsync(async () => {
                double v = _sdk.GetMacro(addr);
                bool r = Math.Abs(v) > 0.01;
                Console.WriteLine($"[FANUC-SDK] 读宏变量 #{addr}={v:F1} → {r}");
                return r;
            }, $"读 #{addr}", ct);
        }

        private Task WriteMacroBoolAsync(int addr, bool val, CancellationToken ct = default)
        {
            return SafeCallAsync(async () => {
                _sdk.SetMacro(addr, val ? 1.0 : 0.0);
                Console.WriteLine($"[FANUC-SDK] 写宏变量 #{addr}={ (val ? 1 : 0) } OK");
            }, $"写 #{addr}", ct);
        }

        /// <summary>安全写宏变量(public, 供引擎中清信号等同步调用)</summary>
        public void SafeSetMacro(int addr, double val)
        {
            SafeCallSync(() => { _sdk.SetMacro(addr, val); Console.WriteLine($"[FANUC-SDK] SafeSetMacro #{addr}={val} OK"); }, $"SafeSet #{addr}");
        }

        // ─── 信号读取 ─────────────────────────────────────────────

        public async Task<(bool rqData, bool rqLoad, bool clamped, bool rqUnload, bool opened)> ReadSignalsAsync(CancellationToken ct = default)
        {
            return await SafeCallAsync(() =>
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
                return Task.FromResult((rqData, rqLoad, clamped, rqUnload, opened));
            }, "读斜床信号快照", ct, maxAttempts: 1);
        }
        /// <summary>
        /// 设置1000 准备就绪
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task<bool> IsMachineReadyAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.MachineReady, ct);
        public Task<bool> IsRequestDataAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestData, ct);
        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestLoad, ct);
        public Task<bool> IsTailstockClampedAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.TailstockClamped, ct);
        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestUnload, ct);
        public Task<bool> IsTailstockOpenedAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.TailstockOpened, ct);

        // ─── 写入 ─────────────────────────────────────────────────

        public async Task SendMachiningParamsAsync(double rollerLength, double rollerDiameter, double borePlugSize, int mode, CancellationToken ct = default)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await SafeCallAsync(() => {
                    Console.WriteLine($"[FANUC-SDK] 写加工参数 attempt={attempt}: L={rollerLength} D={rollerDiameter} bore={borePlugSize} mode={mode}");
                    _sdk.SetMacro(FanucSkewBedAddress.RollerLength, rollerLength);
                    _sdk.SetMacro(FanucSkewBedAddress.RollerDiameter, rollerDiameter);
                    _sdk.SetMacro(FanucSkewBedAddress.BorePlugSize, borePlugSize);
                    _sdk.SetMacro(FanucSkewBedAddress.MachiningMode, mode);
                    return Task.CompletedTask;
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
            return SafeCallAsync(() => {
                double l = _sdk.GetMacro(FanucSkewBedAddress.RollerLength);
                double d = _sdk.GetMacro(FanucSkewBedAddress.RollerDiameter);
                double b = _sdk.GetMacro(FanucSkewBedAddress.BorePlugSize);
                double m = _sdk.GetMacro(FanucSkewBedAddress.MachiningMode);

                bool ok = IsClose(l, rollerLength) &&
                          IsClose(d, rollerDiameter) &&
                          IsClose(b, borePlugSize) &&
                          (int)Math.Round(m) == mode;

                Console.WriteLine($"[FANUC-SDK] 加工参数读回 attempt={attempt}: #{FanucSkewBedAddress.RollerLength}={l} #{FanucSkewBedAddress.RollerDiameter}={d} #{FanucSkewBedAddress.BorePlugSize}={b} #{FanucSkewBedAddress.MachiningMode}={m} → {(ok ? "OK" : "NG")}");
                return Task.FromResult(ok);
            }, "校验加工参数", ct);
        }

        private static bool IsClose(double actual, double expected) => Math.Abs(actual - expected) <= 0.01;

        public Task SetCraneLoadInPlaceAsync(bool v, CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneLoadInPlace, v, ct);
        public Task SetCraneLoadDoneAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneLoadDone, true, ct);
        public Task SetCraneUnloadInPlaceAsync(bool v, CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneUnloadInPlace, v, ct);
        public Task SetCraneUnloadDoneAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneUnloadDone, true, ct);
        public Task StopTailstockAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.TailstockStop, true, ct);

        // ─── 调试 ─────────────────────────────────────────────────

        public Task<string> TestPmcAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("宏变量: ");
                    for (int i = 1000; i <= 1005; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { double v = _sdk.GetMacro(i); sb.Append($"#{i}={v:F1} "); }
                        catch (FocasException ex) { sb.Append($"#{i}=E{ex.ErrorCode} "); }
                    }
                    sb.Append($"| 状态: {_sdk.GetStatus()}");
                    return Task.FromResult(sb.ToString());
                }
                catch (Exception ex) { return Task.FromResult($"扫描失败: {ex.Message}"); }
            }, "PMC扫描", ct, maxAttempts: 1);
        }

        public Task<string> ProbeStatusAsync(CancellationToken ct = default)
        {
            return SafeCallAsync(() =>
            {
                try { return Task.FromResult(_sdk.GetStatus().RunStatusText); }
                catch { return Task.FromResult("disconnected"); }
            }, "读取statinfo", ct, maxAttempts: 1);
        }

        public void Dispose() { if (!_disposed) { _disposed = true; StopWorker(); try { _sdk.Disconnect(); } catch { } _sem.Dispose(); } }
    }
}
