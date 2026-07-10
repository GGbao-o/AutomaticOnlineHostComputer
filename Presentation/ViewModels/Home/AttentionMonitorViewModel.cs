using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>异常监控页的只读展示状态；不参与引擎、设备或任务控制。</summary>
public sealed class AttentionMonitorViewModel : ObservableObject
{
    private readonly AttentionEventCenter _eventCenter;
    private readonly ObservableCollection<AttentionEvent> _events = new();
    private readonly ICollectionView _filteredEvents;
    private string _selectedKind = "全部";
    private string _selectedScope = "全部";
    private int _safetyAlarmCount;
    private int _warningCount;
    private int _emergencyCount;
    private string _lastEventText = "暂无事件";
    private bool _refreshQueued;

    public AttentionMonitorViewModel(AttentionEventCenter eventCenter)
    {
        _eventCenter = eventCenter;
        _filteredEvents = CollectionViewSource.GetDefaultView(_events);
        _filteredEvents.Filter = MatchesFilter;
        _eventCenter.Changed += QueueRefresh;
        RefreshFromSnapshot();
    }

    public IReadOnlyList<string> KindOptions { get; } = new[]
    {
        "全部", "红色安全异常", "黄色警告", "应急操作"
    };

    public IReadOnlyList<string> ScopeOptions { get; } = new[]
    {
        "全部", "1号线", "2号线", "动平衡", "研磨", "全局"
    };

    public ICollectionView FilteredEvents => _filteredEvents;
    public int SafetyAlarmCount { get => _safetyAlarmCount; private set => SetField(ref _safetyAlarmCount, value); }
    public int WarningCount { get => _warningCount; private set => SetField(ref _warningCount, value); }
    public int EmergencyCount { get => _emergencyCount; private set => SetField(ref _emergencyCount, value); }
    public string LastEventText { get => _lastEventText; private set => SetField(ref _lastEventText, value); }

    public string SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (!SetField(ref _selectedKind, value)) return;
            _filteredEvents.Refresh();
        }
    }

    public string SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (!SetField(ref _selectedScope, value)) return;
            _filteredEvents.Refresh();
        }
    }

    private bool MatchesFilter(object obj)
    {
        if (obj is not AttentionEvent item) return false;

        bool kindMatches = SelectedKind == "全部" || item.KindText == SelectedKind;
        bool scopeMatches = SelectedScope == "全部" || item.Scope == SelectedScope;
        return kindMatches && scopeMatches;
    }

    private void QueueRefresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            RefreshFromSnapshot();
            return;
        }

        lock (_events)
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
        }

        dispatcher.BeginInvoke((Action)(() =>
        {
            try { RefreshFromSnapshot(); }
            finally
            {
                lock (_events) _refreshQueued = false;
            }
        }));
    }

    private void RefreshFromSnapshot()
    {
        var snapshot = _eventCenter.Snapshot();
        _events.Clear();
        foreach (var item in snapshot) _events.Add(item);

        SafetyAlarmCount = snapshot.Count(x => x.Kind == AttentionEventKind.SafetyAlarm);
        WarningCount = snapshot.Count(x => x.Kind == AttentionEventKind.Warning);
        EmergencyCount = snapshot.Count(x => x.Kind == AttentionEventKind.Emergency);
        LastEventText = snapshot.Count == 0
            ? "暂无事件"
            : snapshot[0].OccurredAt.ToString("yyyy-MM-dd HH:mm:ss");
        _filteredEvents.Refresh();
    }
}
