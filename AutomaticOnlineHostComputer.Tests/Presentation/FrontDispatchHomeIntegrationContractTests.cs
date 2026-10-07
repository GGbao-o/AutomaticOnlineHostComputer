namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class FrontDispatchHomeIntegrationContractTests
{
    [Fact]
    public void Home_uses_coordinator_for_pause_dispatch_commit_and_failure_requeue()
    {
        string source = ReadHomeSource();

        Assert.Contains("private readonly FrontDispatchQueueCoordinator _frontDispatchQueue = new();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly object _frontDispatchLock", source, StringComparison.Ordinal);
        Assert.Contains("row.OnPauseRequested = PauseTaskRow;", source, StringComparison.Ordinal);
        Assert.Contains("_frontDispatchQueue.TryPause", source, StringComparison.Ordinal);
        Assert.Contains("_frontDispatchQueue.TryBeginDispatch", source, StringComparison.Ordinal);
        Assert.Contains("dequeued.Row.MarkDispatching();", source, StringComparison.Ordinal);
        Assert.Contains("_frontDispatchQueue.CompleteDispatch(dequeued)", source, StringComparison.Ordinal);
        Assert.Contains("dequeued.Row.MarkEnteredLine();", source, StringComparison.Ordinal);
        Assert.Contains("_frontDispatchQueue.RequeueHead(dequeued)", source, StringComparison.Ordinal);
        Assert.Contains("dequeued.Row.MarkQueued();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!item.Row.IsRunning)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_applies_versioned_queue_snapshots_to_visible_row_positions()
    {
        string source = ReadHomeSource();

        Assert.Contains("private long _appliedFrontDispatchOrderVersion", source, StringComparison.Ordinal);
        Assert.Contains("ApplyFrontDispatchOrderSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.Version < _appliedFrontDispatchOrderVersion", source, StringComparison.Ordinal);
        Assert.Contains("row.SetQueuePosition", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_view_and_business_document_expose_the_real_fifo_order_contract()
    {
        string view = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Views", "Home", "HomeView.xaml"));
        string document = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Docs", "全线业务逻辑详解.md"));

        int sequenceColumn = view.IndexOf("Binding=\"{Binding Sequence}\"", StringComparison.Ordinal);
        int orderColumn = view.IndexOf("Binding=\"{Binding QueueOrderText}\"", StringComparison.Ordinal);
        Assert.True(sequenceColumn >= 0 && orderColumn > sequenceColumn, "顺序列必须位于序号列之后。");
        Assert.Contains("Header=\"顺序\"", view, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanToggleRun}\"", view, StringComparison.Ordinal);

        Assert.Contains("暂停会把任务从FIFO删除", document, StringComparison.Ordinal);
        Assert.Contains("再次启动会进入当前FIFO队尾", document, StringComparison.Ordinal);
        Assert.Contains("“派发中”", document, StringComparison.Ordinal);
        Assert.Contains("“已入线”", document, StringComparison.Ordinal);
    }

    private static string ReadHomeSource() =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "Presentation",
            "ViewModels",
            "Home",
            "HomeViewModel.cs"));
}
