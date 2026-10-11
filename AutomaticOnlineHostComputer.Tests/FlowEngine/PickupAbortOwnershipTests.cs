using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

/// <summary>
/// 验证异常删除/清零的归属边界：真实中转架预约、动作版本、来源到达代次及队列顺序。
/// 包含同身份新到达、提前放锁后新来源、匿名M700以及成功通知不回滚的情况。
/// 使用未初始化引擎注入必要内存字段，只验证清理方法，不执行生产运动或PLC握手。
/// </summary>
public sealed class PickupAbortOwnershipTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Balancing_output_cleanup_requires_unknown_response_and_current_ownership(bool unknown, bool owned, bool expected)
    {
        bool called = false;
        var method = typeof(BalancingFlowEngine).GetMethod("ClearFailedBalancingOutputAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Func<CancellationToken, Task> clear = token => { called = true; Assert.True(token.CanBeCanceled); return Task.CompletedTask; };
        var task = (Task<string>)method.Invoke(null, new object[] { "M824", unknown, (Func<bool>)(() => owned), clear })!;
        await task;
        Assert.Equal(expected, called);
    }

    [Fact]
    public async Task Balancing_cleanup_awaits_actual_write_completion_and_records_failure()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var method = typeof(BalancingFlowEngine).GetMethod("ClearFailedBalancingOutputAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task<string>)method.Invoke(null, new object[] { "M824", true, (Func<bool>)(() => true), (Func<CancellationToken, Task>)(_ => completion.Task) })!;
        Assert.False(task.IsCompleted);
        completion.SetException(new IOException("输出响应丢失"));
        Assert.Contains("清理失败/未知：输出响应丢失", await task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rack_abort_removes_only_own_active_or_frozen_reservation(bool frozen)
    {
        var ledger = new TransferRackWorkpieceLedger();
        var a = Board("A", "1"); var b = Board("A", "2");
        ledger.Enqueue("ST105", a, out var arrival);
        ledger.Enqueue("ST101", b, out _);
        Assert.True(ledger.TryReserve("ST105", "own", out _, out _));
        if (frozen) ledger.HoldForManualResolution("ST105", "own", "失败");
        Assert.True(ledger.DiscardReservedWorkpiece("ST105", "own", arrival, a));
        Assert.False(ledger.IsReserved("ST105", out _));
        Assert.False(ledger.TryGet("ST105", out _));
        Assert.True(ledger.TryGet("ST101", out var kept));
        Assert.Equal("2", kept.Sequence);
        Assert.False(ledger.DiscardReservedWorkpiece("ST105", "own", arrival, a));
    }

    [Fact]
    public void Rack_abort_preserves_new_arrival_even_with_same_identity_and_reused_operation_name()
    {
        var ledger = new TransferRackWorkpieceLedger(); var a = Board("A", "1");
        ledger.Enqueue("ST105", a, out var oldArrival);
        ledger.TryReserve("ST105", "same", out _, out _);
        ledger.Enqueue("ST105", a, out var nextArrival);
        ledger.TryReserve("ST105", "same", out _, out _);
        Assert.NotEqual(oldArrival, nextArrival);
        Assert.False(ledger.DiscardReservedWorkpiece("ST105", "same", oldArrival, a));
        Assert.True(ledger.IsReserved("ST105", out _));
        Assert.True(ledger.TryGet("ST105", out _));
    }

    [Fact]
    public void Rack_abort_requires_matching_owner_and_full_board_identity()
    {
        var ledger = new TransferRackWorkpieceLedger(); var a = Board("A", "1");
        ledger.Enqueue("ST105", a, out var arrival); ledger.TryReserve("ST105", "own", out _, out _);
        Assert.False(ledger.DiscardReservedWorkpiece("ST105", "other", arrival, a));
        Assert.False(ledger.DiscardReservedWorkpiece("ST105", "own", arrival, Board("A", "2")));
        Assert.False(ledger.DiscardReservedWorkpiece("ST105", "own", arrival, default));
        Assert.True(ledger.TryGet("ST105", out _));
    }

    [Theory]
    [InlineData(typeof(Line1FrontFlowEngine))]
    [InlineData(typeof(Line2FrontFlowEngine))]
    public void Front_crane_abort_keeps_other_queued_boards_in_order(Type type)
    {
        object engine = RuntimeHelpers.GetUninitializedObject(type);
        var queue = new ConcurrentQueue<WorkpieceCache>(new[] { Board("A", "1"), Board("A", "2"), Board("B", "1"), Board("A", "1") });
        Set(engine, "_frontCraneTaskLock", new object()); Set(engine, "_craneQueue", queue);
        Assert.Equal(2, Call<int>(engine, "DiscardFrontCraneQueueResidues", Board("A", "1")));
        Assert.Equal(new[] { "A/2", "B/1" }, queue.Select(wp => wp.PlateNo + "/" + wp.Sequence));
    }

    [Fact]
    public void Balancing_abort_checks_action_and_arrival_and_preserves_same_identity_new_board()
    {
        var engine = NewBalance(); var a = Board("A", "1");
        engine.SetBalancingWp("M821", a);
        long oldArrival = Arrivals(engine)["M821"];
        engine.SetBalancingWp("M821", a);
        Assert.False(Call<bool>(engine, "DiscardBalancingSource", false, 7, "M821", oldArrival, (WorkpieceCache?)a));
        Assert.Equal(1, engine.CachedCount);
        Assert.False(Call<bool>(engine, "DiscardBalancingSource", false, 6, "M821", Arrivals(engine)["M821"], (WorkpieceCache?)a));
        Assert.True(Call<bool>(engine, "DiscardBalancingSource", false, 7, "M821", Arrivals(engine)["M821"], (WorkpieceCache?)a));
        Assert.Equal(0, engine.CachedCount);
    }

    [Fact]
    public void Balancing_anonymous_M700_abort_never_matches_other_source_cache()
    {
        var engine = NewBalance(); engine.SetBalancingWp("M821", Board("A", "1"));
        Assert.False(Call<bool>(engine, "DiscardBalancingSource", false, 7, "M700", 0L, (WorkpieceCache?)new WorkpieceCache { Diameter = 160 }));
        Assert.Equal(1, engine.CachedCount);
    }

    [Fact]
    public void Balancing_source_transfer_then_early_release_cannot_delete_new_arrival()
    {
        var engine = NewBalance(); var a = Board("A", "1");
        engine.SetBalancingWp("M821", a); long arrival = Arrivals(engine)["M821"];
        Call<object?>(engine, "TransferBalancingCacheToInFlight", false, 7, "M821", "ST010/M720", a, arrival);
        engine.SetBalancingWp("M821", Board("A", "2"));
        Assert.False(Call<bool>(engine, "DiscardBalancingSource", false, 7, "M821", arrival, (WorkpieceCache?)a));
        Assert.Equal(1, engine.CachedCount);
    }

    private static BalancingFlowEngine NewBalance()
    {
        var e = (BalancingFlowEngine)RuntimeHelpers.GetUninitializedObject(typeof(BalancingFlowEngine));
        Set(e, "_emergencyLock", new object()); Set(e, "_balWpsLock", new object());
        Set(e, "_balWps", new Dictionary<string, WorkpieceCache>()); Set(e, "_balWpArrivals", new Dictionary<string, long>());
        Set(e, "_m2ActionVersion", 7); Set(e, "_m3ActionVersion", 7); return e;
    }
    private static Dictionary<string, long> Arrivals(object e) => (Dictionary<string, long>)e.GetType().GetField("_balWpArrivals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(e)!;
    private static WorkpieceCache Board(string plate, string seq) => new() { PlateNo = plate, Sequence = seq, Length = 3000, Diameter = 160 };
    private static void Set(object e, string field, object value) => e.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e, value);
    private static T Call<T>(object e, string method, params object?[] args) => (T)e.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, args)!;
}
