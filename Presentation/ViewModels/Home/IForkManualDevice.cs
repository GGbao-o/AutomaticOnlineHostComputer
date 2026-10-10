using System;
using System.Threading;
using System.Threading.Tasks;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>手动货叉所需的设备操作；不包含生产任务或业务状态。</summary>
public interface IForkManualDevice : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task<ForkStatus> ReadAllStatusAsync(CancellationToken ct);
    Task GoStandbyAsync(CancellationToken ct);
    Task GoFeedToBoringAsync(CancellationToken ct);
    Task GoPickFromBoringAsync(CancellationToken ct);
    Task GoStandbyToPos3Async(CancellationToken ct);
}

internal sealed class ForkManualDevice(string name, string ip, int port) : IForkManualDevice
{
    private readonly ForkService _service = new(name, ip, port);
    public bool IsConnected => _service.IsConnected;
    public Task ConnectAsync(CancellationToken ct) => _service.ConnectAsync(ct);
    public Task<ForkStatus> ReadAllStatusAsync(CancellationToken ct) => _service.ReadAllStatusAsync(ct);
    public Task GoStandbyAsync(CancellationToken ct) => _service.GoStandbyAsync(ct);
    public Task GoFeedToBoringAsync(CancellationToken ct) => _service.GoFeedToBoringAsync(ct);
    public Task GoPickFromBoringAsync(CancellationToken ct) => _service.GoPickFromBoringAsync(ct);
    public Task GoStandbyToPos3Async(CancellationToken ct) => _service.GoStandbyToPos3Async(ct);
    public void Dispose() => _service.Dispose();
}
