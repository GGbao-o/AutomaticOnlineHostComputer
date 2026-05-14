using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>
    /// 沈阳斜床（FANUC 系统）设备服务，封装所有与斜床交互的读写操作。
    /// <para>共6台，每台独立实例，通过 FOCAS2 TCP 通信。</para>
    /// </summary>
    public sealed class FanucSkewBedService : IDisposable
    {
        private readonly FanucFocasClient _client;
        private bool _disposed;

        /// <param name="ip">斜床控制器 IP</param>
        /// <param name="port">FOCAS2 端口，默认 8193</param>
        public FanucSkewBedService(string ip, int port = 8193)
        {
            _client = new FanucFocasClient(ip, (ushort)port);
        }

        public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);
        public Task DisconnectAsync() => _client.DisconnectAsync();
        public bool IsConnected => _client.IsConnected;

        // ─── 读取机床状态 ─────────────────────────────────────────────

        public Task<bool> IsMachineReadyAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.MachineReady, ct);

        public Task<int> GetOperationModeAsync(CancellationToken ct = default)
            => _client.ReadIntAsync(FanucSkewBedAddress.OperationMode, ct);

        public Task<bool> IsRequestDataAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.RequestData, ct);

        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.RequestLoad, ct);

        public Task<bool> IsTailstockClampedAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.TailstockClamped, ct);

        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.RequestUnload, ct);

        public Task<bool> IsTailstockOpenedAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.TailstockOpened, ct);

        public Task<bool> IsAllAxesSafeAsync(CancellationToken ct = default)
            => Task.Run(async () =>
            {
                bool x = await ReadBoolAsync(FanucSkewBedAddress.XAxisSafePos, ct);
                bool y = await ReadBoolAsync(FanucSkewBedAddress.YAxisSafePos, ct);
                bool z = await ReadBoolAsync(FanucSkewBedAddress.ZAxisSafePos, ct);
                bool t = await ReadBoolAsync(FanucSkewBedAddress.TurretSafePos, ct);
                return x && y && z && t;
            }, ct);

        public Task<bool> IsTool1LifeEndAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.Tool1LifeEnd, ct);

        public Task<bool> IsTool2LifeEndAsync(CancellationToken ct = default)
            => ReadBoolAsync(FanucSkewBedAddress.Tool2LifeEnd, ct);

        // ─── 写入加工参数 ─────────────────────────────────────────────

        /// <summary>下发加工参数（版长、成活直径、堵孔、加工模式）到斜床。</summary>
        public async Task SendMachiningParamsAsync(
            double rollerLength,
            double rollerDiameter,
            double borePlugSize,
            int    machiningMode,
            CancellationToken ct = default)
        {
            await _client.WriteAsync(FanucSkewBedAddress.RollerLength,    rollerLength,    ct);
            await _client.WriteAsync(FanucSkewBedAddress.RollerDiameter,  rollerDiameter,  ct);
            await _client.WriteAsync(FanucSkewBedAddress.BorePlugSize,    borePlugSize,    ct);
            await _client.WriteAsync(FanucSkewBedAddress.MachiningMode,   machiningMode,   ct);
            // 通知机床数据下发完成
            await _client.WriteAsync(FanucSkewBedAddress.DataSentDone, 1, ct);
        }

        /// <summary>通知机床天车上料到位。</summary>
        public Task SetCraneLoadInPlaceAsync(bool inPlace, CancellationToken ct = default)
            => _client.WriteAsync(FanucSkewBedAddress.CraneLoadInPlace, inPlace ? 1 : 0, ct);

        /// <summary>通知机床天车下料到位。</summary>
        public Task SetCraneUnloadInPlaceAsync(bool inPlace, CancellationToken ct = default)
            => _client.WriteAsync(FanucSkewBedAddress.CraneUnloadInPlace, inPlace ? 1 : 0, ct);

        /// <summary>通知机床天车下料完成。</summary>
        public Task SetCraneUnloadDoneAsync(CancellationToken ct = default)
            => _client.WriteAsync(FanucSkewBedAddress.CraneUnloadDone, 1, ct);

        /// <summary>天车异常时停止尾座（写 #651=1）。</summary>
        public Task StopTailstockAsync(CancellationToken ct = default)
            => _client.WriteAsync(FanucSkewBedAddress.TailstockStop, 1, ct);

        // ─── 私有辅助 ─────────────────────────────────────────────────

        private async Task<bool> ReadBoolAsync(int address, CancellationToken ct)
        {
            int val = await _client.ReadIntAsync(address, ct);
            return val != 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
        }
    }
}
