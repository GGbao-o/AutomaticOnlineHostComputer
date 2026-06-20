using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>
    /// 锦州斜床（迅捷PLC，Modbus TCP）设备服务，封装所有读写操作。
    /// <para>共4台，每台独立实例。通信端口 502，单元ID=1。</para>
    /// <para>
    /// FLOAT 类型（版长/堵孔/成活直径/加工时间）使用 2 个连续保持寄存器，
    /// 高字在前（Big-Endian），须调用 <see cref="ModbusTcpClient"/> 的 ReadFloat 扩展。
    /// </para>
    /// </summary>
    public sealed class ModbusSkewBedService : IDisposable
    {
        private const int MaxIoAttempts = 3;
        private readonly ModbusTcpClient _client;
        private bool _disposed;

        /// <param name="ip">PLC IP 地址</param>
        /// <param name="port">Modbus TCP 端口，默认 502</param>
        public ModbusSkewBedService(string ip, int port = 502)
        {
            _client = new ModbusTcpClient(ip, port);
        }

        public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);
        public Task DisconnectAsync() => _client.DisconnectAsync();
        public bool IsConnected => _client.IsConnected;

        // ─── 读取机床状态（INT16） ─────────────────────────────────────

        public Task<bool> IsMachineReadyAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.MachineReady, token), "读10350机床准备", ct);

        /// <summary>操作模式：手动=0，人工自动=1，全自动=2</summary>
        public Task<int> GetOperationModeAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.ReadIntAsync(ModbusSkewBedAddress.OperationMode, token), "读10351操作模式", ct);

        public Task<bool> IsRequestDataAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.RequestData, token), "读10352请求数据", ct);

        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.RequestLoad, token), "读10353请求上料", ct);

        public Task<bool> IsTailstockClampedAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.TailstockClamped, token), "读10354尾座顶紧", ct);

        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.RequestUnload, token), "读10357请求下料", ct);

        public Task<bool> IsTailstockOpenedAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.TailstockOpened, token), "读10358尾座张开", ct);

        public Task<bool> IsDoorOpenAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.DoorOpen, token), "读10359门开", ct);

        public Task<bool> IsTool1LifeEndAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.Tool1LifeEnd, token), "读10360刀具1寿命", ct);

        public Task<bool> IsTool2LifeEndAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadBoolRawAsync(ModbusSkewBedAddress.Tool2LifeEnd, token), "读10361刀具2寿命", ct);

        // ─── 读取 FLOAT 参数（2寄存器 IEEE754） ──────────────────────

        /// <summary>读取版长（mm）</summary>
        public Task<double> GetRollerLengthAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadFloatRawAsync(ModbusSkewBedAddress.RollerLength, token), "读10300版长", ct);

        /// <summary>读取堵孔尺寸（mm）</summary>
        public Task<double> GetBorePlugSizeAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadFloatRawAsync(ModbusSkewBedAddress.BorePlugSize, token), "读10302堵孔", ct);

        /// <summary>读取成活直径（mm）</summary>
        public Task<double> GetRollerDiameterAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadFloatRawAsync(ModbusSkewBedAddress.RollerDiameter, token), "读10304成活直径", ct);

        /// <summary>读取加工时间（分钟）</summary>
        public Task<double> GetMachiningTimeAsync(CancellationToken ct = default)
            => SafeCallAsync(token => ReadFloatRawAsync(ModbusSkewBedAddress.MachiningTime, token), "读10362加工时间", ct);

        /// <summary>一次读5个关键信号: 请求数据/请求上料/夹紧/请求下料/张开。</summary>
        public async Task<(bool rqData, bool rqLoad, bool clamped, bool rqUnload, bool opened)> ReadSignalsAsync(CancellationToken ct = default)
        {
            return await SafeCallAsync(async token =>
            {
                const int start = ModbusSkewBedAddress.RequestData;        // 10352
                const int end = ModbusSkewBedAddress.TailstockOpened;      // 10358
                var r = await _client.ReadAsync(start, end - start + 1, token);

                bool At(int address)
                {
                    int index = address - start;
                    if ((uint)index >= (uint)r.IntValues.Length)
                        throw new InvalidOperationException($"斜床信号快照缺少地址{address}。");
                    return r.IntValues[index] != 0;
                }

                return (
                    At(ModbusSkewBedAddress.RequestData),
                    At(ModbusSkewBedAddress.RequestLoad),
                    At(ModbusSkewBedAddress.TailstockClamped),
                    At(ModbusSkewBedAddress.RequestUnload),
                    At(ModbusSkewBedAddress.TailstockOpened));
            }, "读10352~10358斜床信号快照", ct);
        }

        // ─── 写入加工参数 ────────────────────────────────────────────

        /// <summary>下发加工参数（版长、堵孔、成活直径 float；模式 int）。</summary>
        public async Task SendMachiningParamsAsync(
            double rollerLength, double borePlugSize, double rollerDiameter,
            int mode, CancellationToken ct = default)
        {
            await SafeCallAsync(async token =>
            {
                await _client.WriteAsync(ModbusSkewBedAddress.RollerLength, rollerLength, token);      // 10300~10301 FLOAT
                await _client.WriteAsync(ModbusSkewBedAddress.BorePlugSize, borePlugSize, token);       // 10302~10303 FLOAT
                await _client.WriteAsync(ModbusSkewBedAddress.RollerDiameter, rollerDiameter, token);   // 10304~10305 FLOAT
                await _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, mode, token);              // 10370 INT
            }, "写10300~10305/10370加工参数", ct);
        }

        // ─── 写入控制命令（INT16） ────────────────────────────────────

        /// <summary>
        /// 下发加工模式（粗车1，精车2，研磨3，粗精磨4，精磨5，粗精研磨6）
        /// </summary>
        public Task SetMachiningModeAsync(int mode, CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, mode, token), "写10370加工模式", ct);

        /// <summary>远程尾座顶紧（写1）</summary>
        public Task ClampTailstockAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.TailstockClampCmd, 1, token), "写10371尾座顶紧", ct);

        /// <summary>远程启动机床</summary>
        public Task RemoteStartAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.RemoteStart, 1, token), "写10372远程启动", ct);

        /// <summary>远程尾座张开（写1）</summary>
        public Task OpenTailstockAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.TailstockOpenCmd, 1, token), "写10373尾座张开", ct);

        /// <summary>天车异常时停止尾座（写1）</summary>
        public Task StopTailstockAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.TailstockStopCmd, 1, token), "写10374尾座停止", ct);

        // ─── 级联清零（写0清除上一步信号, 与FANUC级联规则对应） ─────

        /// <summary>清10370=0（加工模式复位, 写10371前必须先清）</summary>
        public Task ClearMachiningModeAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, 0, token), "清10370加工模式", ct);

        /// <summary>清10371=0（尾座顶紧复位, 写10372前必须先清）</summary>
        public Task ClearTailstockClampAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.TailstockClampCmd, 0, token), "清10371尾座顶紧", ct);

        /// <summary>清10372=0（远程启动复位, 下料写10373前必须先清）</summary>
        public Task ClearRemoteStartAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.RemoteStart, 0, token), "清10372远程启动", ct);

        /// <summary>清10373=0（尾座张开复位, 写10374前必须先清）</summary>
        public Task ClearTailstockOpenAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.TailstockOpenCmd, 0, token), "清10373尾座张开", ct);
        
        /// <summary>
        /// 写10374=0 
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task ClearTailstockStopAsync(CancellationToken ct = default)
            => SafeCallAsync(token => _client.WriteAsync(ModbusSkewBedAddress.TailstockStopCmd, 0, token), "清10374尾座停止", ct);

        /// <summary>
        /// 应急清零：清掉上位机写入的加工参数和远程动作命令。
        /// 仅用于人工确认现场已处理后的应急恢复，不控制天车/机床动作。
        /// </summary>
        public async Task ClearEmergencyRegistersAsync(CancellationToken ct = default)
        {
            await SafeCallAsync(async token =>
            {
                await _client.WriteAsync(ModbusSkewBedAddress.RollerLength, 0.0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.BorePlugSize, 0.0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.RollerDiameter, 0.0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, 0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.TailstockClampCmd, 0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.RemoteStart, 0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.TailstockOpenCmd, 0, token);
                await _client.WriteAsync(ModbusSkewBedAddress.TailstockStopCmd, 0, token);
            }, "应急清零10300~10305/10370~10374", ct);
            Console.WriteLine("[ModbusSkewBed] 应急清零: 10300~10305/10370~10374 → 0");
        }

        // ─── 私有辅助 ─────────────────────────────────────────────────

        private async Task<T> SafeCallAsync<T>(Func<CancellationToken, Task<T>> action, string desc, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= MaxIoAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!_client.IsConnected)
                        await _client.ConnectAsync(ct);

                    return await action(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxIoAttempts)
                {
                    Console.WriteLine($"[ModbusSkewBed] {desc} 异常 attempt={attempt}/{MaxIoAttempts}: {ex.Message} → 重连后重试");
                    try { await _client.DisconnectAsync(); } catch { }
                    await Task.Delay(200, ct);
                }
            }

            throw new InvalidOperationException($"{desc} {MaxIoAttempts}次重连重试仍失败");
        }

        private Task SafeCallAsync(Func<CancellationToken, Task> action, string desc, CancellationToken ct)
            => SafeCallAsync(async token =>
            {
                await action(token);
                return true;
            }, desc, ct);

        private async Task<bool> ReadBoolRawAsync(int address, CancellationToken ct)
            => (await _client.ReadIntAsync(address, ct)) != 0;

        private async Task<double> ReadFloatRawAsync(int startAddr, CancellationToken ct)
        {
            var result = await _client.ReadAsync(startAddr, 2, ct);
            // 高字先（IntValues[0]），低字后（IntValues[1]），组装 IEEE 754 Big-Endian float
            ushort hi = (ushort)result.IntValues[0];
            ushort lo = (ushort)result.IntValues[1];
            byte[] bytes = {
                (byte)(lo & 0xFF), (byte)(lo >> 8),
                (byte)(hi & 0xFF), (byte)(hi >> 8)
            };
            return BitConverter.ToSingle(bytes, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
        }
    }
}
