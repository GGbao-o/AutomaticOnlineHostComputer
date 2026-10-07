using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.Contracts;

namespace AutomaticOnlineHostComputer.Communication.DeviceServices;

/// <summary>研磨取料工件参数显示屏单次写入结果。</summary>
public sealed record GrindingWorkpieceDisplayWriteResult(bool Success, string Stage, Exception? Error)
{
    public static GrindingWorkpieceDisplayWriteResult Completed() => new(true, "完成", null);
    public static GrindingWorkpieceDisplayWriteResult Failed(string stage, Exception error) => new(false, stage, error);
}

/// <summary>
/// 研磨取料工件参数显示屏通信。该设备只用于展示，不参与研磨业务控制。
/// </summary>
public sealed class GrindingWorkpieceDisplayService : IAsyncDisposable
{
    public const int Port = 502;
    public const byte UnitId = 1;
    public const int TimeoutMs = 3000;
    public const int DiameterAddress = 100;
    public const int LengthAddress = 101;

    private readonly Func<string, int, byte, int, IDeviceClient> _clientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDeviceClient? _client;
    private string? _clientIp;
    private int _disposeStarted;
    private bool _disposed;

    public GrindingWorkpieceDisplayService(
        Func<string, int, byte, int, IDeviceClient>? clientFactory = null)
    {
        _clientFactory = clientFactory ??
            ((ip, port, unitId, timeoutMs) => new ModbusTcpClient(ip, port, unitId, timeoutMs));
    }

    /// <summary>
    /// 写直径到100、长度到101。设备或配置异常转换为失败结果；调用方取消仍向外传播。
    /// </summary>
    public async Task<GrindingWorkpieceDisplayWriteResult> TryWriteAsync(
        string ip, double diameter, double length, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string normalizedIp = (ip ?? string.Empty).Trim();
            if (normalizedIp.Length == 0)
                return GrindingWorkpieceDisplayWriteResult.Failed(
                    "配置IP", new ArgumentException("研磨工件显示屏IP不能为空", nameof(ip)));

            int displayDiameter = (int)Math.Round(diameter);
            int displayLength = (int)Math.Round(length);
            string stage = "连接";

            using var timeoutCts = new CancellationTokenSource(TimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            CancellationToken displayCt = linkedCts.Token;

            try
            {
                if (_client != null && !string.Equals(_clientIp, normalizedIp, StringComparison.OrdinalIgnoreCase))
                    await ResetClientNoThrowAsync().ConfigureAwait(false);

                _client ??= _clientFactory(normalizedIp, Port, UnitId, TimeoutMs);
                _clientIp = normalizedIp;

                if (!_client.IsConnected)
                    await _client.ConnectAsync(displayCt).ConfigureAwait(false);

                stage = "直径地址100";
                await _client.WriteAsync(DiameterAddress, displayDiameter, displayCt).ConfigureAwait(false);

                stage = "长度地址101";
                await _client.WriteAsync(LengthAddress, displayLength, displayCt).ConfigureAwait(false);

                return GrindingWorkpieceDisplayWriteResult.Completed();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
            {
                await ResetClientNoThrowAsync().ConfigureAwait(false);
                return GrindingWorkpieceDisplayWriteResult.Failed(
                    stage, new TimeoutException($"研磨工件显示屏{stage}超时({TimeoutMs}ms)", ex));
            }
            catch (Exception ex)
            {
                await ResetClientNoThrowAsync().ConfigureAwait(false);
                return GrindingWorkpieceDisplayWriteResult.Failed(stage, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetClientNoThrowAsync()
    {
        IDeviceClient? oldClient = _client;
        _client = null;
        _clientIp = null;
        if (oldClient == null) return;

        try
        {
            await oldClient.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 显示屏是旁路设备，释放旧连接失败不能影响研磨业务或下一次重连。
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await ResetClientNoThrowAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
