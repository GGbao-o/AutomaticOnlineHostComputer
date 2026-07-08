using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 1/2号线前后天车相邻碰撞区互斥锁。
/// <para>ZoneMT: 打号机 ↔ 中转架，相邻会碰撞；ZoneTS: 中转架 ↔ 斜床1，相邻会碰撞。</para>
/// <para>中转架位于两段碰撞区中间，进入中转架必须同时持有 ZoneMT + ZoneTS。</para>
/// <para>现场确认策略: 中转架取/放完成且Z轴回0位或配置安全高度后，即可按流程释放对应Zone锁。</para>
/// </summary>
public class SafetyFlags : IDisposable
{
    /// <summary>ZoneMT：打号机 ↔ 中转架相邻碰撞区。</summary>
    public readonly SemaphoreSlim MarkerTransferCollisionLock = new(1, 1);

    /// <summary>ZoneTS：中转架 ↔ 斜床1(ST108/ST606)相邻碰撞区。</summary>
    public readonly SemaphoreSlim TransferSkew1CollisionLock = new(1, 1);

    public void Dispose()
    {
        MarkerTransferCollisionLock.Dispose();
        TransferSkew1CollisionLock.Dispose();
    }
}
