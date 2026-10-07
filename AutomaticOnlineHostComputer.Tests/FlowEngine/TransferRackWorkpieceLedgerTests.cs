using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class TransferRackWorkpieceLedgerTests
{
    [Fact]
    public void Manual_resolution_reservation_cannot_be_taken_by_a_new_action()
    {
        var ledger = new TransferRackWorkpieceLedger();
        ledger.Enqueue("ST105", new WorkpieceCache { PlateNo = "E26072224", Sequence = "8" }, out _);
        Assert.True(ledger.TryReserve("ST105", "action-A", out _, out _));

        Assert.True(ledger.HoldForManualResolution("ST105", "action-A", "XY超时，位置未知"));

        Assert.False(ledger.TryReserve("ST105", "action-B", out _, out _));
        Assert.True(ledger.IsHeldForManualResolution("ST105", out string owner, out string reason));
        Assert.Equal("action-A", owner);
        Assert.Contains("XY超时", reason);
    }

    [Fact]
    public void Safe_pre_pickup_failure_can_release_regular_reservation()
    {
        var ledger = new TransferRackWorkpieceLedger();
        ledger.Enqueue("ST105", new WorkpieceCache { PlateNo = "E26072224", Sequence = "8" }, out _);
        Assert.True(ledger.TryReserve("ST105", "action-A", out _, out _));

        Assert.True(ledger.ReleaseReservation("ST105", "action-A"));

        Assert.True(ledger.TryReserve("ST105", "action-B", out _, out _));
    }

    [Fact]
    public void Manual_resolution_cannot_be_silently_reactivated_by_same_action()
    {
        var ledger = new TransferRackWorkpieceLedger();
        ledger.Enqueue("ST105", new WorkpieceCache { PlateNo = "E26072224", Sequence = "8" }, out _);
        Assert.True(ledger.TryReserve("ST105", "action-A", out _, out _));
        Assert.True(ledger.HoldForManualResolution("ST105", "action-A", "运动超时"));

        Assert.False(ledger.TryReserve("ST105", "action-A", out _, out _));
        Assert.False(ledger.ReleaseReservation("ST105", "action-A"));
    }

    [Fact]
    public void Manual_resolution_still_at_source_releases_only_the_reservation()
    {
        var ledger = new TransferRackWorkpieceLedger();
        ledger.Enqueue("ST105", new WorkpieceCache { PlateNo = "E26072224", Sequence = "8" }, out _);
        Assert.True(ledger.TryReserve("ST105", "action-A", out _, out _));
        Assert.True(ledger.HoldForManualResolution("ST105", "action-A", "运动超时"));

        Assert.True(ledger.TryResolveManualReservation("ST105", "action-A", FlowActionManualResolution.StillAtSource, out WorkpieceCache wp, out _));

        Assert.Equal("E26072224", wp.PlateNo);
        Assert.True(ledger.TryReserve("ST105", "action-B", out _, out _));
    }
}
