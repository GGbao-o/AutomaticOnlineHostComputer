using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceAddresses;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices
{
    /// <summary>
    /// 激光打标机/打号机 设备服务。
    /// <para>
    /// 支持两种通信方式：
    ///   - ModbusTcp：通过 Modbus TCP 读写寄存器。
    ///   - FileHandshake：通过文件共享目录握手（写文件A→等待文件B）。
    /// </para>
    /// </summary>
    public sealed class LaserMarkerService : IDisposable
    {
        public enum MarkerMode { ModbusTcp, FileHandshake }

        private readonly ModbusTcpClient?      _modbus;
        private readonly FileHandshakeClient?  _file;
        private readonly MarkerMode            _mode;
        private bool _disposed;

        /// <summary>Modbus TCP 模式构造。</summary>
        public LaserMarkerService(string ip, int port = 502)
        {
            _modbus = new ModbusTcpClient(ip, port);
            _mode   = MarkerMode.ModbusTcp;
        }

        /// <summary>文件共享模式构造。</summary>
        public LaserMarkerService(string sharedFolderPath)
        {
            _file = new FileHandshakeClient(sharedFolderPath);
            _mode = MarkerMode.FileHandshake;
        }

        public Task ConnectAsync(CancellationToken ct = default) => _mode == MarkerMode.ModbusTcp
            ? _modbus!.ConnectAsync(ct)
            : _file!.ConnectAsync(ct);

        public Task DisconnectAsync() => _mode == MarkerMode.ModbusTcp
            ? _modbus!.DisconnectAsync()
            : _file!.DisconnectAsync();

        // ─── ModbusTcp 模式：读取状态 ────────────────────────────────

        public Task<bool> IsDeviceBusyAsync(CancellationToken ct = default)
            => ReadBoolAsync(LaserMarkerAddress.Reg_DeviceBusy - 1, ct);

        public Task<bool> IsMachineDoneAsync(CancellationToken ct = default)
            => ReadBoolAsync(LaserMarkerAddress.Reg_MachineDone - 1, ct);

        // ─── ModbusTcp 模式：写入控制 ────────────────────────────────

        public Task SetLoadDoneAsync(CancellationToken ct = default)
            => _modbus!.WriteAsync(LaserMarkerAddress.Reg_LoadDone - 1, 1, ct);

        public Task SetUnloadDoneAsync(CancellationToken ct = default)
            => _modbus!.WriteAsync(LaserMarkerAddress.Reg_UnloadDone - 1, 1, ct);

        public Task SetRollerDiametersAsync(int outerDiam, int innerDiam, CancellationToken ct = default)
            => Task.WhenAll(
                _modbus!.WriteAsync(LaserMarkerAddress.Reg_RollerOuterDiam - 1, outerDiam, ct),
                _modbus!.WriteAsync(LaserMarkerAddress.Reg_RollerInnerDiam - 1, innerDiam, ct));

        /// <summary>
        /// 发送打印文字（UTF-8编码写入寄存器 0x000A~0x00C8）。
        /// 格式：版号;序号;版辊直径;周长;版长;内径;外径
        /// </summary>
        public async Task SendPrintTextAsync(string text, CancellationToken ct = default)
        {
            byte[] raw    = Encoding.UTF8.GetBytes(text);
            int    count  = LaserMarkerAddress.Reg_PrintText_Count;
            var    regs   = new int[count];
            for (int i = 0; i < raw.Length && i / 2 < count; i += 2)
            {
                int hi = raw[i];
                int lo = i + 1 < raw.Length ? raw[i + 1] : 0;
                regs[i / 2] = (hi << 8) | lo;
            }
            for (int i = 0; i < count; i++)
                await _modbus!.WriteAsync(LaserMarkerAddress.Reg_PrintText_Start - 1 + i, regs[i], ct);
        }

        // ─── FileHandshake 模式 ──────────────────────────────────────

        /// <summary>
        /// 发送打标指令（写 cmd.txt），并等待设备完成确认（ack.txt 出现）。
        /// workpieceNo 为工件编号，markContent 为刻印文字（格式：版号;序号;直径;周长;版长;内径;外径）。
        /// </summary>
        public Task SendMarkCommandAsync(string workpieceNo, string markContent, CancellationToken ct = default)
            => _file!.SendMarkCommandAsync(workpieceNo, markContent, ct);

        // ─── 私有辅助 ─────────────────────────────────────────────────

        private async Task<bool> ReadBoolAsync(int addr, CancellationToken ct)
            => (await _modbus!.ReadIntAsync(addr, ct)) != 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Task.Run 兜底：避免在 UI 线程同步等异步断开时死锁
            Task.Run(async () =>
            {
                if (_mode == MarkerMode.ModbusTcp)
                    await _modbus!.DisconnectAsync();
                else
                    await _file!.DisconnectAsync();
            }).GetAwaiter().GetResult();
        }
    }
}
