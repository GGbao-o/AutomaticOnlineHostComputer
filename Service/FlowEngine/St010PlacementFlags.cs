using System.Threading;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// ST010最终放板流程的只读提示标志。
/// 仅供研磨天车决定本趟是否跳过M731，不参与锁、PLC通信、坐标或动作控制。
/// </summary>
public sealed class St010PlacementFlags
{
    private int _m3St010Placing;
    private int _crane2St010Placing;

    public bool BeginM3Placement() => Interlocked.Exchange(ref _m3St010Placing, 1) == 0;
    public bool EndM3Placement() => Interlocked.Exchange(ref _m3St010Placing, 0) == 1;
    public bool BeginCrane2Placement() => Interlocked.Exchange(ref _crane2St010Placing, 1) == 0;
    public bool EndCrane2Placement() => Interlocked.Exchange(ref _crane2St010Placing, 0) == 1;

    public St010PlacementSnapshot Snapshot() => new(
        Volatile.Read(ref _m3St010Placing) == 1,
        Volatile.Read(ref _crane2St010Placing) == 1);
}

public readonly record struct St010PlacementSnapshot(bool M3St010Placing, bool Crane2St010Placing)
{
    public bool AnyPlacing => M3St010Placing || Crane2St010Placing;
}
