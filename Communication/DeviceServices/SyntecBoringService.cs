using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>
    /// 双头镗（新代 Syntec CNC）设备服务，封装所有读写操作。
    /// <para>宏变量通过 Syntec OpenCNC SDK 读写；R区信号通过 Modbus TCP（端口502）读写。</para>
    /// </summary>
    public sealed class SyntecBoringService : IDisposable
    {
        private readonly SyntecCncClient _client;
        private bool _disposed;

        public SyntecBoringService(string ip, int port = 502)
        {
            _client = new SyntecCncClient(ip, port);
        }

        public Task ConnectAsync(CancellationToken ct = default) => _client.ConnectAsync(ct);
        public Task DisconnectAsync() => _client.DisconnectAsync();
        public bool IsConnected => _client.IsConnected;

        // ─── 读 R区信号（机床→上位机） ────────────────────────────────

        public Task<bool> IsRequestDataAsync(CancellationToken ct = default)
            => ReadBoolAsync(SyntecBoringAddress.R_RequestData, ct);

        public Task<bool> IsRequestLoadAsync(CancellationToken ct = default)
            => ReadBoolAsync(SyntecBoringAddress.R_RequestLoad, ct);

        public Task<bool> IsClampDoneAsync(CancellationToken ct = default)
            => ReadBoolAsync(SyntecBoringAddress.R_ClampDone, ct);

        public Task<bool> IsRequestUnloadAsync(CancellationToken ct = default)
            => ReadBoolAsync(SyntecBoringAddress.R_RequestUnload, ct);

        public Task<bool> IsUnclampDoneAsync(CancellationToken ct = default)
            => ReadBoolAsync(SyntecBoringAddress.R_UnclampDone, ct);

        // ─── 写宏变量（上位机→机床加工参数） ─────────────────────────

        /// <summary>下发双头镗加工参数（宏变量）。</summary>
        public async Task SendMachiningParamsAsync(
            double rollerLength,
            double outerDiameter,
            double plugThickness,
            double innerConicity,
            double innerDiameter,
            CancellationToken ct = default)
        {
            await _client.WriteAsync(SyntecBoringAddress.MacroRollerLength,  rollerLength,  ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroOuterDiameter, outerDiameter, ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroPlugThickness, plugThickness, ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroInnerConicity, innerConicity, ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroInnerDiameter, innerDiameter, ct);
        }

        // ─── 写 R区信号（上位机→机床握手） ───────────────────────────

        public Task SetDataSentDoneAsync(CancellationToken ct = default)
            => _client.WriteAsync(SyntecBoringAddress.R_DataSentDone, 1, ct);

        public Task SetForkLoadInPlaceAsync(CancellationToken ct = default)
            => _client.WriteAsync(SyntecBoringAddress.R_ForkLoadInPlace, 1, ct);

        public Task SetForkLoadOutDoneAsync(CancellationToken ct = default)
            => _client.WriteAsync(SyntecBoringAddress.R_ForkLoadOutDone, 1, ct);

        public Task SetForkUnloadInPlaceAsync(CancellationToken ct = default)
            => _client.WriteAsync(SyntecBoringAddress.R_ForkUnloadInPlace, 1, ct);

        public Task SetForkUnloadOutDoneAsync(CancellationToken ct = default)
            => _client.WriteAsync(SyntecBoringAddress.R_ForkUnloadOutDone, 1, ct);

        // ─── 私有辅助 ─────────────────────────────────────────────────

        private async Task<bool> ReadBoolAsync(int address, CancellationToken ct)
            => (await _client.ReadIntAsync(address, ct)) != 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
        }
    }
}
