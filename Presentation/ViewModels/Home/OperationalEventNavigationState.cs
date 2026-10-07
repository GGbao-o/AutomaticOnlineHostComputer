using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using AutomaticOnlineHostComputer.Presentation.Services;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// 导航菜单红点状态。只读，不改变 Store 或事件。
/// 进入页面时清零，离开后新增事件重新计数。最多显示 99+。
/// </summary>
public sealed class OperationalEventNavigationState : INotifyPropertyChanged, IDisposable
{
    private readonly Service.OperationalEvents.IOperationalEventStore _store;
    private readonly IUiDispatcher _dispatcher;
    private long _lastViewedTotalReceived;
    private long _lastObservedTotalReceived;
    private bool _isViewing;
    private long _unseenCount;
    private bool _disposed;
    private bool _refreshQueued;

    public OperationalEventNavigationState(
        Service.OperationalEvents.IOperationalEventStore store,
        IUiDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        var diag = _store.GetDiagnosticsSnapshot();
        _lastViewedTotalReceived = diag.TotalReceived;
        _lastObservedTotalReceived = diag.TotalReceived;
        _store.Changed += OnStoreChanged;
    }

    public long UnseenCount => _unseenCount;
    public bool HasUnseenEvents => _unseenCount > 0;

    public string UnseenDisplayText
    {
        get
        {
            if (_unseenCount <= 0) return "";
            return _unseenCount >= 100 ? "99+" : _unseenCount.ToString();
        }
    }

    /// <summary>进入异常监控页面时调用，清零计数。</summary>
    public void EnterViewing()
    {
        _isViewing = true;
        var diag = _store.GetDiagnosticsSnapshot();
        _lastViewedTotalReceived = diag.TotalReceived;
        _lastObservedTotalReceived = diag.TotalReceived;
        UpdateUnseen(0);
    }

    /// <summary>离开异常监控页面时调用。</summary>
    public void LeaveViewing()
    {
        _isViewing = false;
    }

    private void OnStoreChanged()
    {
        var diag = _store.GetDiagnosticsSnapshot();
        long total = diag.TotalReceived;

        // 避免旧快照降低基线
        long local;
        local = Interlocked.Read(ref _lastObservedTotalReceived);
        if (total <= local) return;
        Interlocked.Exchange(ref _lastObservedTotalReceived, total);

        if (_isViewing)
        {
            // 查看期间保持为零，但推进已看基线
            Interlocked.Exchange(ref _lastViewedTotalReceived, total);
            UpdateUnseen(0);
            return;
        }

        long viewed = Interlocked.Read(ref _lastViewedTotalReceived);
        long unseen = total - viewed;
        if (unseen < 0) unseen = 0;
        UpdateUnseen(unseen);
    }

    private void UpdateUnseen(long count)
    {
        Interlocked.Exchange(ref _unseenCount, count);
        QueueUiRefresh();
    }

    private void QueueUiRefresh()
    {
        if (_disposed) return;
        if (_refreshQueued) return;
        _refreshQueued = true;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                _refreshQueued = false;
                if (_disposed) return;
                try
                {
                    OnPropertyChanged(nameof(UnseenCount));
                    OnPropertyChanged(nameof(HasUnseenEvents));
                    OnPropertyChanged(nameof(UnseenDisplayText));
                }
                catch { }
            });
        }
        catch { _refreshQueued = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnStoreChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
