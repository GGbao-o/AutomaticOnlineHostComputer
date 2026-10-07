using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using AutomaticOnlineHostComputer.Presentation.ViewModels;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// “在制工件”页面的纯展示 ViewModel。
/// 只消费 HomeViewModel 生成的不可变内存快照，不持有设备服务，不执行派发、清理或恢复。
/// </summary>
public sealed class InProcessWorkpieceOverviewViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private string _selectedScope = "全部流程";
    private string _selectedStatus = "全部状态";
    private string _searchText = string.Empty;
    private int _knownIdentityCount;
    private int _globalQueueCount;
    private int _frontInTransitCount;
    private int _transferRackCount;
    private int _processDeviceCount;
    private int _mismatchCount;
    private int _multipleRecordCount;
    private string _lastRefreshText = "尚未刷新";

    public InProcessWorkpieceOverviewViewModel(HomeViewModel home)
    {
        _home = home;
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = FilterItem;
    }

    public ObservableCollection<InProcessWorkpieceSnapshot> Items { get; } = new();
    public ICollectionView FilteredItems { get; }

    public IReadOnlyList<string> ScopeOptions { get; } =
        new[] { "全部流程", "全局", "1号线", "2号线", "动平衡", "研磨" };
    public IReadOnlyList<string> StatusOptions { get; } =
        new[] { "全部状态", "正常", "交接中", "信号不一致", "证据不足", "需人工确认" };

    public string SelectedScope
    {
        get => _selectedScope;
        set { if (SetField(ref _selectedScope, value)) FilteredItems.Refresh(); }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set { if (SetField(ref _selectedStatus, value)) FilteredItems.Refresh(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) FilteredItems.Refresh(); }
    }

    public int KnownIdentityCount { get => _knownIdentityCount; private set => SetField(ref _knownIdentityCount, value); }
    public int GlobalQueueCount { get => _globalQueueCount; private set => SetField(ref _globalQueueCount, value); }
    public int FrontInTransitCount { get => _frontInTransitCount; private set => SetField(ref _frontInTransitCount, value); }
    public int TransferRackCount { get => _transferRackCount; private set => SetField(ref _transferRackCount, value); }
    public int ProcessDeviceCount { get => _processDeviceCount; private set => SetField(ref _processDeviceCount, value); }
    public int MismatchCount { get => _mismatchCount; private set => SetField(ref _mismatchCount, value); }
    public int MultipleRecordCount { get => _multipleRecordCount; private set => SetField(ref _multipleRecordCount, value); }
    public string LastRefreshText { get => _lastRefreshText; private set => SetField(ref _lastRefreshText, value); }

    /// <summary>
    /// 在 UI 线程替换少量展示行。调用频率由页面控制；本方法不启动后台任务或设备读取。
    /// </summary>
    public void Refresh()
    {
        InProcessWorkpieceOverviewSnapshot snapshot;
        try
        {
            snapshot = _home.GetInProcessWorkpieceOverviewSnapshot();
        }
        catch
        {
            var nowUtc = DateTime.UtcNow;
            snapshot = new InProcessWorkpieceOverviewSnapshot(new[]
            {
                new InProcessWorkpieceSnapshot(InProcessWorkpieceKind.SourceFailure,
                    "全局", "页面", "在制工件", "快照暂不可用", null, "现有内存快照",
                    InProcessWorkpieceStatus.EvidenceUnavailable,
                    "页面聚合失败；未触发设备重读、重连、缓存清理或流程控制", nowUtc)
            }, nowUtc);
        }

        // 大多数秒级刷新内容并未变化；相同时保留现有行，避免DataGrid重复创建容器和重排。
        if (!HasSameDisplayContent(Items, snapshot.Items))
        {
            Items.Clear();
            foreach (var item in snapshot.Items) Items.Add(item);
        }

        KnownIdentityCount = snapshot.Items.Where(x => x.HasKnownIdentity)
            .Select(x => x.IdentityKey).Distinct(StringComparer.Ordinal).Count();
        GlobalQueueCount = snapshot.Items.Count(x => x.Kind == InProcessWorkpieceKind.GlobalQueue);
        FrontInTransitCount = snapshot.Items.Count(x =>
            (x.FilterScope == "1号线" || x.FilterScope == "2号线")
            && x.FlowScope.Contains("前端", StringComparison.Ordinal)
            && x.Kind is InProcessWorkpieceKind.FrontCurrent
                or InProcessWorkpieceKind.FrontCraneQueue
                or InProcessWorkpieceKind.CraneTask);
        TransferRackCount = snapshot.Items.Count(x =>
            x.Kind == InProcessWorkpieceKind.TransferRack && x.Workpiece != null);
        ProcessDeviceCount = snapshot.Items.Count(x =>
            x.Kind == InProcessWorkpieceKind.ProcessStation && x.Workpiece != null);
        MismatchCount = snapshot.Items.Count(x => x.Status == InProcessWorkpieceStatus.SignalMismatch);
        MultipleRecordCount = snapshot.Items.Where(x =>
                x.Status == InProcessWorkpieceStatus.Handoff && x.HasKnownIdentity)
            .Select(x => x.IdentityKey).Distinct(StringComparer.Ordinal).Count();
        LastRefreshText = snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        FilteredItems.Refresh();
    }

    private bool FilterItem(object value)
    {
        if (value is not InProcessWorkpieceSnapshot item) return false;
        if (SelectedScope != "全部流程" && !string.Equals(item.FilterScope, SelectedScope, StringComparison.Ordinal))
            return false;
        if (SelectedStatus != "全部状态" && !string.Equals(item.StatusText, SelectedStatus, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        string text = SearchText.Trim();
        return Contains(item.IdentityText, text)
            || Contains(item.FlowScope, text)
            || Contains(item.Location, text)
            || Contains(item.Stage, text)
            || Contains(item.Detail, text);
    }

    private static bool Contains(string? source, string value)
        => source?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool HasSameDisplayContent(IReadOnlyList<InProcessWorkpieceSnapshot> current,
        IReadOnlyList<InProcessWorkpieceSnapshot> next)
    {
        if (current.Count != next.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            // CapturedAtUtc只代表本轮复制时间，不是业务变化，因此不参与页面内容比较。
            if (a.Kind != b.Kind || a.FilterScope != b.FilterScope || a.FlowScope != b.FlowScope
                || a.Location != b.Location || a.Stage != b.Stage || a.Workpiece != b.Workpiece
                || a.Evidence != b.Evidence || a.Status != b.Status || a.Detail != b.Detail)
                return false;
        }
        return true;
    }
}
