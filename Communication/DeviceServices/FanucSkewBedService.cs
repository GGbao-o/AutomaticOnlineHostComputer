using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;
using FanucFocas;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    public sealed class FanucSkewBedService : IDisposable
    {
        private readonly string _ip;
        private readonly ushort _port;
        private readonly int _timeoutMs;
        private readonly SemaphoreSlim _sem = new(1, 1); // 防止轮询和握手同时抢本实例
        private readonly FanucSdk _sdk = new(); // 每台FANUC独立实例, 不再共享全局static锁
        private bool _disposed;

        /// <param name=\"timeoutMs\">连接超时(ms), 默认300→3s(FANUC SDK内部/10后×100ms)</param>
        public FanucSkewBedService(string ip, int port = 8193, int timeoutMs = 300)
        {
            _ip = ip; _port = (ushort)port; _timeoutMs = timeoutMs;
        }

        public bool IsConnected { get { try { return _sdk.IsConnected; } catch { return false; } } }

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
                Focas1.cnc_settimeout(_sdk.Handle, 300);
                Console.WriteLine("[FANUC-SDK] 会话超时=300秒");
                try { double v = _sdk.GetMacro(1000); Console.WriteLine($"[FANUC-SDK] 连接验证 #1000={v:F1}"); }
                catch (FocasException ex) { Console.WriteLine($"[FANUC-SDK] ⚠ 连接验证失败 err={ex.ErrorCode}, 重连..."); _sdk.Disconnect(); ok = _sdk.Connect(_ip, _port, _timeoutMs); if (!ok) throw new InvalidOperationException("重连失败"); Focas1.cnc_settimeout(_sdk.Handle, 300); }
            }
            finally { _sem.Release(); }
        }

        public async Task DisconnectAsync() { await _sem.WaitAsync(); try { _sdk.Disconnect(); } finally { _sem.Release(); } }

        // ─── 异步重连保护 (轮询/握手路径, 不阻塞线程池) ──────────

        private async Task SafeCallAsync(Func<Task> f, string desc)
        {
            await _sem.WaitAsync(); // 异步等待, 不阻塞线程
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { await f(); return; }
                    catch (Exception ex) when (attempt < 2)
                    {
                        Console.WriteLine($"[FANUC-SDK] {desc} 异常 attempt={attempt}: {ex.Message}, 重连...");
                        try { _sdk.Disconnect(); } catch { }
                        if (!_sdk.Connect(_ip, _port, _timeoutMs))
                        { Console.WriteLine($"[FANUC-SDK] 重连失败, 等1s重试..."); await Task.Delay(1000); continue; }
                        Focas1.cnc_settimeout(_sdk.Handle, 300);
                        Console.WriteLine($"[FANUC-SDK] 重连成功, 重试操作");
                    }
                }
                throw new InvalidOperationException($"{desc} 3次重试均失败");
            }
            finally { _sem.Release(); }
        }

        private async Task<T> SafeCallAsync<T>(Func<Task<T>> f, string desc)
        {
            await _sem.WaitAsync();
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { return await f(); }
                    catch (Exception ex) when (attempt < 2)
                    {
                        Console.WriteLine($"[FANUC-SDK] {desc} 异常 attempt={attempt}: {ex.Message}, 重连...");
                        try { _sdk.Disconnect(); } catch { }
                        if (!_sdk.Connect(_ip, _port, _timeoutMs))
                        { Console.WriteLine($"[FANUC-SDK] 重连失败, 等1s重试..."); await Task.Delay(1000); continue; }
                        Focas1.cnc_settimeout(_sdk.Handle, 300);
                        Console.WriteLine($"[FANUC-SDK] 重连成功, 重试操作");
                    }
                }
                throw new InvalidOperationException($"{desc} 3次重试均失败");
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
                        Focas1.cnc_settimeout(_sdk.Handle, 300);
                        Console.WriteLine($"[FANUC-SDK] 重连成功, 重试操作");
                    }
                }
                throw new InvalidOperationException($"{desc} 3次重试均失败");
            }
            finally { _sem.Release(); }
        }

        // ─── 读写宏变量 ───────────────────────────────────────────

        private Task<bool> ReadMacroBoolAsync(int addr)
        {
            return SafeCallAsync(async () => {
                double v = _sdk.GetMacro(addr);
                bool r = Math.Abs(v) > 0.01;
                Console.WriteLine($"[FANUC-SDK] 读宏变量 #{addr}={v:F1} → {r}");
                return r;
            }, $"读 #{addr}");
        }

        private Task WriteMacroBoolAsync(int addr, bool val)
        {
            return SafeCallAsync(async () => {
                _sdk.SetMacro(addr, val ? 1.0 : 0.0);
                Console.WriteLine($"[FANUC-SDK] 写宏变量 #{addr}={ (val ? 1 : 0) } OK");
            }, $"写 #{addr}");
        }

        /// <summary>安全写宏变量(public, 供引擎中清信号等同步调用)</summary>
        public void SafeSetMacro(int addr, double val)
        {
            SafeCallSync(() => { _sdk.SetMacro(addr, val); Console.WriteLine($"[FANUC-SDK] SafeSetMacro #{addr}={val} OK"); }, $"SafeSet #{addr}");
        }

        // ─── 信号读取 ─────────────────────────────────────────────

        public async Task<(bool rqData, bool rqLoad, bool clamped, bool rqUnload, bool opened)> ReadSignalsAsync(CancellationToken ct = default)
        {
            var t = Task.WhenAll(
                ReadMacroBoolAsync(FanucSkewBedAddress.RequestData),
                ReadMacroBoolAsync(FanucSkewBedAddress.RequestLoad),
                ReadMacroBoolAsync(FanucSkewBedAddress.TailstockClamped),
                ReadMacroBoolAsync(FanucSkewBedAddress.RequestUnload),
                ReadMacroBoolAsync(FanucSkewBedAddress.TailstockOpened));
            await t;
            return (t.Result[0], t.Result[1], t.Result[2], t.Result[3], t.Result[4]);
        }
        /// <summary>
        /// 设置1000 准备就绪
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task<bool> IsMachineReadyAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.MachineReady);
        public Task<bool> IsRequestDataAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestData);
        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestLoad);
        public Task<bool> IsTailstockClampedAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.TailstockClamped);
        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.RequestUnload);
        public Task<bool> IsTailstockOpenedAsync(CancellationToken ct = default) => ReadMacroBoolAsync(FanucSkewBedAddress.TailstockOpened);

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
                }, "写加工参数");

                if (await VerifyMachiningParamsAsync(rollerLength, rollerDiameter, borePlugSize, mode, attempt))
                    break;

                if (attempt == maxAttempts)
                    throw new InvalidOperationException($"FANUC加工参数写入校验失败, 已禁止写#{FanucSkewBedAddress.DataSentDone}=1");

                await Task.Delay(100, ct);
            }

            await WriteMacroBoolAsync(FanucSkewBedAddress.DataSentDone, true);
        }

        private Task<bool> VerifyMachiningParamsAsync(double rollerLength, double rollerDiameter, double borePlugSize, int mode, int attempt)
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
            }, "校验加工参数");
        }

        private static bool IsClose(double actual, double expected) => Math.Abs(actual - expected) <= 0.01;

        public Task SetCraneLoadInPlaceAsync(bool v, CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneLoadInPlace, v);
        public Task SetCraneLoadDoneAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneLoadDone, true);
        public Task SetCraneUnloadInPlaceAsync(bool v, CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneUnloadInPlace, v);
        public Task SetCraneUnloadDoneAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.CraneUnloadDone, true);
        public Task StopTailstockAsync(CancellationToken ct = default) => WriteMacroBoolAsync(FanucSkewBedAddress.TailstockStop, true);

        // ─── 调试 ─────────────────────────────────────────────────

        public Task<string> TestPmcAsync(CancellationToken ct = default)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("宏变量: ");
                for (int i = 1000; i <= 1005; i++)
                {
                    try { double v = _sdk.GetMacro(i); sb.Append($"#{i}={v:F1} "); }
                    catch (FocasException ex) { sb.Append($"#{i}=E{ex.ErrorCode} "); }
                }
                sb.Append($"| 状态: {_sdk.GetStatus()}");
                return Task.FromResult(sb.ToString());
            }
            catch (Exception ex) { return Task.FromResult($"扫描失败: {ex.Message}"); }
        }

        public Task<string> ProbeStatusAsync(CancellationToken ct = default)
        {
            try { return Task.FromResult(_sdk.GetStatus().RunStatusText); }
            catch { return Task.FromResult("disconnected"); }
        }

        public void Dispose() { if (!_disposed) { _disposed = true; try { _sdk.Disconnect(); } catch { } } }
    }
}
