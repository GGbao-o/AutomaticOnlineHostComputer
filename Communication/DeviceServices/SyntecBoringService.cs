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
            double leftPlugThickness,
            double rightPlugThickness,
            double innerConicity,
            double innerDiameter,
            CancellationToken ct = default)
        {
            await _client.WriteAsync(SyntecBoringAddress.MacroRollerLength,     rollerLength,      ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroOuterDiameter,    outerDiameter,     ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroLeftPlugThickness,  leftPlugThickness,  ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroRightPlugThickness, rightPlugThickness, ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroInnerConicity,    innerConicity,     ct);
            await _client.WriteAsync(SyntecBoringAddress.MacroInnerDiameter,    innerDiameter,     ct);
        }

        // ─── 写 R区信号（上位机→机床握手） ───────────────────────────

        public Task SetDataSentDoneAsync(CancellationToken ct = default)
            => _client.WriteRAsync(SyntecBoringAddress.R_DataSentDone, 1, ct);

        public Task SetForkLoadInPlaceAsync(CancellationToken ct = default)
            => _client.WriteRAsync(SyntecBoringAddress.R_ForkLoadInPlace, 1, ct);

        public Task SetForkLoadOutDoneAsync(CancellationToken ct = default)
            => _client.WriteRAsync(SyntecBoringAddress.R_ForkLoadOutDone, 1, ct);

        public Task SetForkUnloadInPlaceAsync(CancellationToken ct = default)
            => _client.WriteRAsync(SyntecBoringAddress.R_ForkUnloadInPlace, 1, ct);

        public Task SetForkUnloadOutDoneAsync(CancellationToken ct = default)
            => _client.WriteRAsync(SyntecBoringAddress.R_ForkUnloadOutDone, 1, ct);

        /// <summary>直接写任意R区寄存器值 (用于清上一步信号)。</summary>
        public Task WriteRAsync(int addr, int value, CancellationToken ct = default)
            => _client.WriteRAsync(addr, value, ct);

        /// <summary>双头镗是否空闲: R6101=1(就绪) 且 R6103/6105/6107/6109 全为0。</summary>
        public async Task<bool> IsIdleAsync(CancellationToken ct = default)
        {
            var s = await ReadAllSignalsAsync(ct);
            return s.r6101 && !s.r6103 && !s.r6105 && !s.r6107 && !s.r6109;
        }

        /// <summary>一次批量读R6101~R6110，返回5个关键读信号。</summary>
        public async Task<(bool r6101, bool r6103, bool r6105, bool r6107, bool r6109)> ReadAllSignalsAsync(CancellationToken ct = default)
        {
            var vals = await _client.ReadRAsync(6101, 10, ct);
            return (vals[0] != 0, vals[2] != 0, vals[4] != 0, vals[6] != 0, vals[8] != 0);
        }

        // ─── 私有辅助 ─────────────────────────────────────────────────

        private async Task<bool> ReadBoolAsync(int address, CancellationToken ct)
        {
            var vals = await _client.ReadRAsync(address, 1, ct);  // R区寄存器用READ_plc_addr
            return vals.Length > 0 && vals[0] != 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Task.Run(async () => await _client.DisconnectAsync()).GetAwaiter().GetResult();
        }
    }
}
