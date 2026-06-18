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
            => ReadBoolAsync(ModbusSkewBedAddress.MachineReady, ct);

        /// <summary>操作模式：手动=0，人工自动=1，全自动=2</summary>
        public Task<int> GetOperationModeAsync(CancellationToken ct = default)
            => _client.ReadIntAsync(ModbusSkewBedAddress.OperationMode, ct);

        public Task<bool> IsRequestDataAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.RequestData, ct);

        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.RequestLoad, ct);

        public Task<bool> IsTailstockClampedAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.TailstockClamped, ct);

        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.RequestUnload, ct);

        public Task<bool> IsTailstockOpenedAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.TailstockOpened, ct);

        public Task<bool> IsDoorOpenAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.DoorOpen, ct);

        public Task<bool> IsTool1LifeEndAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.Tool1LifeEnd, ct);

        public Task<bool> IsTool2LifeEndAsync(CancellationToken ct = default)
            => ReadBoolAsync(ModbusSkewBedAddress.Tool2LifeEnd, ct);

        // ─── 读取 FLOAT 参数（2寄存器 IEEE754） ──────────────────────

        /// <summary>读取版长（mm）</summary>
        public Task<double> GetRollerLengthAsync(CancellationToken ct = default)
            => ReadFloatAsync(ModbusSkewBedAddress.RollerLength, ct);

        /// <summary>读取堵孔尺寸（mm）</summary>
        public Task<double> GetBorePlugSizeAsync(CancellationToken ct = default)
            => ReadFloatAsync(ModbusSkewBedAddress.BorePlugSize, ct);

        /// <summary>读取成活直径（mm）</summary>
        public Task<double> GetRollerDiameterAsync(CancellationToken ct = default)
            => ReadFloatAsync(ModbusSkewBedAddress.RollerDiameter, ct);

        /// <summary>读取加工时间（分钟）</summary>
        public Task<double> GetMachiningTimeAsync(CancellationToken ct = default)
            => ReadFloatAsync(ModbusSkewBedAddress.MachiningTime, ct);

        /// <summary>一次读5个关键信号: 请求数据/请求上料/夹紧/请求下料/张开。</summary>
        public async Task<(bool rqData, bool rqLoad, bool clamped, bool rqUnload, bool opened)> ReadSignalsAsync(CancellationToken ct = default)
        {
            const int start = ModbusSkewBedAddress.RequestData;        // 10352
            const int end = ModbusSkewBedAddress.TailstockOpened;      // 10358
            var r = await _client.ReadAsync(start, end - start + 1, ct);

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
        }

        // ─── 写入加工参数 ────────────────────────────────────────────

        /// <summary>下发加工参数（版长、堵孔、成活直径 float；模式 int）。</summary>
        public async Task SendMachiningParamsAsync(
            double rollerLength, double borePlugSize, double rollerDiameter,
            int mode, CancellationToken ct = default)
        {
            await _client.WriteAsync(ModbusSkewBedAddress.RollerLength, rollerLength, ct);      // 10300~10301 FLOAT
            await _client.WriteAsync(ModbusSkewBedAddress.BorePlugSize, borePlugSize, ct);       // 10302~10303 FLOAT
            await _client.WriteAsync(ModbusSkewBedAddress.RollerDiameter, rollerDiameter, ct);   // 10304~10305 FLOAT
            await _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, mode, ct);              // 10370 INT
        }

        // ─── 写入控制命令（INT16） ────────────────────────────────────

        /// <summary>
        /// 下发加工模式（粗车1，精车2，研磨3，粗精磨4，精磨5，粗精研磨6）
        /// </summary>
        public Task SetMachiningModeAsync(int mode, CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, mode, ct);

        /// <summary>远程尾座顶紧（写1）</summary>
        public Task ClampTailstockAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.TailstockClampCmd, 1, ct);

        /// <summary>远程启动机床</summary>
        public Task RemoteStartAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.RemoteStart, 1, ct);

        /// <summary>远程尾座张开（写1）</summary>
        public Task OpenTailstockAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.TailstockOpenCmd, 1, ct);

        /// <summary>天车异常时停止尾座（写1）</summary>
        public Task StopTailstockAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.TailstockStopCmd, 1, ct);

        // ─── 级联清零（写0清除上一步信号, 与FANUC级联规则对应） ─────

        /// <summary>清10370=0（加工模式复位, 写10371前必须先清）</summary>
        public Task ClearMachiningModeAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.MachiningMode, 0, ct);

        /// <summary>清10371=0（尾座顶紧复位, 写10372前必须先清）</summary>
        public Task ClearTailstockClampAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.TailstockClampCmd, 0, ct);

        /// <summary>清10372=0（远程启动复位, 下料写10373前必须先清）</summary>
        public Task ClearRemoteStartAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.RemoteStart, 0, ct);

        /// <summary>清10373=0（尾座张开复位, 写10374前必须先清）</summary>
        public Task ClearTailstockOpenAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.TailstockOpenCmd, 0, ct);
        
        /// <summary>
        /// 写10374=0 
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task ClearTailstockStopAsync(CancellationToken ct = default)
            => _client.WriteAsync(ModbusSkewBedAddress.TailstockStopCmd, 0, ct);

        // ─── 私有辅助 ─────────────────────────────────────────────────

        private async Task<bool> ReadBoolAsync(int address, CancellationToken ct)
            => (await _client.ReadIntAsync(address, ct)) != 0;

        private async Task<double> ReadFloatAsync(int startAddr, CancellationToken ct)
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
