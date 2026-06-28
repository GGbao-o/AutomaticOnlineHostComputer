using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutomaticOnlineHostComputer.Service;

/// <summary>
/// 1/2号线前后天车共享区域互斥锁（打号机+中转架区域, 同一时间只允许一台天车进入）
/// <para>替代了旧的两个 volatile bool + 手工 while 轮询的 check-then-set 方案,
/// SemaphoreSlim(1,1) 由操作系统保证原子分配, 无竞态窗口。</para>
/// <para>用法: await _lock.WaitAsync(TimeSpan.FromSeconds(300), ct) → try { ... } finally { _lock.Release(); }</para>
/// </summary>
public class SafetyFlags : IDisposable
{
    public readonly SemaphoreSlim SharedAreaLock = new(1, 1);
    public void Dispose() => SharedAreaLock.Dispose();
}
