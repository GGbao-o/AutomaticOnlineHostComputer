namespace AutomaticOnlineHostComputer.Service;

/// <summary>仅把原调度层的释放结果补到弹窗；不操作任何锁或设备。</summary>
internal sealed class FrontCraneAlarmDispatchScope : IDisposable
{
    private static readonly AsyncLocal<FrontCraneAlarmDispatchScope?> Ambient = new();
    private readonly FrontCraneAlarmDispatchScope? _previous;
    private FlowAlarmPresentationContext? _latest;
    private bool _releaseRecorded;
    private int _disposed;

    private FrontCraneAlarmDispatchScope() => _previous = Ambient.Value;

    public static FrontCraneAlarmDispatchScope Begin()
    {
        var scope = new FrontCraneAlarmDispatchScope();
        Ambient.Value = scope;
        return scope;
    }

    internal static bool Track(FlowAlarmPresentationContext context)
    {
        var scope = Ambient.Value;
        if (scope == null) return false;
        scope._latest = context;
        return true;
    }

    public static void RecordReleased()
    {
        try
        {
            var scope = Ambient.Value;
            if (scope == null) return;
            scope._releaseRecorded = true;
            scope._latest?.CompleteFrontDispatch(true);
        }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_releaseRecorded) _latest?.CompleteFrontDispatch(false); }
        catch { }
        finally { Ambient.Value = _previous; }
    }
}
