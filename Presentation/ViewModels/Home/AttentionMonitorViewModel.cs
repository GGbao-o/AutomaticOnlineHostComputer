using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AutomaticOnlineHostComputer.Presentation.Services;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>异常监控页主从布局 ViewModel — 只读展示，不参与引擎/设备/任务控制。</summary>
public sealed class AttentionMonitorViewModel : ObservableObject, IDisposable
{
    private readonly IOperationalEventStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private readonly IOperationalEventSaveDialog _saveDialog;
    private readonly IOperationalEventExportService _exportService;
    private readonly OperationalEventFormatter _formatter;
    private readonly OperationalEventNavigationState? _navState;

    private readonly ObservableCollection<OperationalEventRowViewModel> _rows = new();
    private readonly List<OperationalEventRowViewModel> _allRows = new();
    private OperationalEventRowViewModel? _selectedRow;
    private long _appliedVersion;
    private long _cachedStoreVersion;
    // Store.Changed may come from device/background threads, while DoRefresh runs on the UI thread.
    // Use an interlocked 0/1 gate so a merge window can never be scheduled twice concurrently.
    private int _refreshQueued;
    private bool _disposed;
    private const int RefreshMergeWindowMs = 200;
    private string _operationStatusText = "";
    private bool _isExporting;

    // ── 筛选字段 ──
    private DateTime? _filterTimeFrom, _filterTimeTo;
    private OperationalEventSeverity? _filterSeverity;
    private OperationalEventCategory? _filterCategory;
    private string _filterScope = "全部";
    private string _filterEngine = "全部";
    private string _filterDeviceType = "全部";
    private string _filterDeviceNo = "";
    private string _filterStation = "";
    private string _filterWorkpiece = "";
    private string _filterKeyword = "";
    private PausedFilter _filterPaused = PausedFilter.全部;
    private RecoveryFilter _filterRecovery = RecoveryFilter.全部;
    private bool _filterDuplicateOnly;

    private int _safetyUnknownCount;
    private int _finalFailureCount;
    private int _warningCount;
    private int _recoveryCount;
    private int _stageStallCount;

    public enum PausedFilter { 全部, 已暂停, 未暂停, 未知 }
    public enum RecoveryFilter { 全部, 已恢复, 未恢复, 未知 }

    public AttentionMonitorViewModel(
        IOperationalEventStore store,
        OperationalEventFormatter formatter,
        IUiDispatcher dispatcher,
        IClipboardService clipboard,
        IOperationalEventSaveDialog saveDialog,
        IOperationalEventExportService exportService,
        AttentionEventCenter attentionCenter,
        OperationalEventNavigationState? navState = null)
    {
        _store = store;
        _formatter = formatter;
        _dispatcher = dispatcher;
        _clipboard = clipboard;
        _saveDialog = saveDialog;
        _exportService = exportService;
        _navState = navState;
        _store.Changed += QueueRefresh;
        _selectedRow = null;
        RefreshFromStore();
    }

    // ═══════════════════════════════════════════════════════════════
    //  公开绑定
    // ═══════════════════════════════════════════════════════════════
    public ObservableCollection<OperationalEventRowViewModel> FilteredRows => _rows;

    public OperationalEventRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetField(ref _selectedRow, value)) return;
            OnPropertyChanged(nameof(SelectedDetailTitle));
            OnPropertyChanged(nameof(SelectedDetailText));
        }
    }

    public string SelectedDetailTitle =>
        _selectedRow == null ? "未选择事件" : $"{_selectedRow.SeverityText} — {_selectedRow.Title}";

    public string SelectedDetailText
    {
        get
        {
            if (_selectedRow == null) return "请从左侧列表选择事件查看详情。";
            try { return _selectedRow.FullDetailText; }
            catch { return "详情文本生成失败，原事件数据已保留并可导出。"; }
        }
    }

    public string OperationStatusText
    {
        get => _operationStatusText;
        set { if (SetField(ref _operationStatusText, value ?? "")) OnPropertyChanged(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrWhiteSpace(_operationStatusText);
    public bool IsExporting { get => _isExporting; private set => SetField(ref _isExporting, value); }
    public bool CanCopy => _selectedRow != null;
    public bool CanExport => _allRows.Count > 0;

    // ── 诊断/KPI（WPF ComboBox Text/SelectedItem 默认 TwoWay，必须提供 setter）──
    public long TotalReceived { get => _store.GetDiagnosticsSnapshot().TotalReceived; private set { } }
    public long AggregatedCount { get => _store.GetDiagnosticsSnapshot().AggregatedCount; private set { } }
    public long EvictedCount { get => _store.GetDiagnosticsSnapshot().EvictedCount; private set { } }
    public long ReporterFailureCount { get => _store.GetDiagnosticsSnapshot().ReporterFailureCount; private set { } }
    public long CurrentTotalEvents { get => _allRows.Count; private set { } }
    public int SafetyUnknownCount { get => _safetyUnknownCount; private set { SetField(ref _safetyUnknownCount, value); } }
    public int FinalFailureCount { get => _finalFailureCount; private set { SetField(ref _finalFailureCount, value); } }
    public int WarningCount { get => _warningCount; private set { SetField(ref _warningCount, value); } }
    public int RecoveryCount { get => _recoveryCount; private set { SetField(ref _recoveryCount, value); } }
    public int StageStallCount { get => _stageStallCount; private set { SetField(ref _stageStallCount, value); } }

    // ═══════════════════════════════════════════════════════════════
    //  筛选选项
    // ═══════════════════════════════════════════════════════════════
    public IReadOnlyList<string> ScopeOptions { get; } = new[] { "全部", "1号线", "2号线", "动平衡", "研磨", "全局" };
    public IReadOnlyList<string> EngineOptions { get; } = new[] { "全部", "前端", "后端", "动平衡", "研磨", "主页面" };
    public IReadOnlyList<string> SeverityOptions { get; } = new[] { "全部", "严重", "错误", "警告", "信息" };
    public IReadOnlyList<string> CategoryOptions { get; } = new[] { "全部", "安全", "最终失败", "结果未知", "自动恢复", "通信", "阶段滞留", "微调", "运动", "磁铁", "传感器", "握手", "应急" };
    public IReadOnlyList<string> PausedFilterOptions { get; } = new[] { "全部", "已暂停", "未暂停", "未知" };
    public IReadOnlyList<string> RecoveryFilterOptions { get; } = new[] { "全部", "已恢复", "未恢复", "未知" };

    public string FilterScope { get => _filterScope; set { if (SetField(ref _filterScope, value ?? "全部")) ApplyFilters(); } }
    public string FilterEngine { get => _filterEngine; set { if (SetField(ref _filterEngine, value ?? "全部")) ApplyFilters(); } }
    public string FilterSeverityText { get => _filterSeverityText; set { ParseSeverity(value); OnPropertyChanged(); } }
    private string _filterSeverityText = "全部";
    public string FilterCategoryText { get => _filterCategoryText; set { ParseCategory(value); OnPropertyChanged(); } }
    private string _filterCategoryText = "全部";
    public string FilterDeviceType { get => _filterDeviceType; set { if (SetField(ref _filterDeviceType, value ?? "全部")) ApplyFilters(); } }
    public string FilterDeviceNo { get => _filterDeviceNo; set { if (SetField(ref _filterDeviceNo, value ?? "")) ApplyFilters(); } }
    public string FilterStation { get => _filterStation; set { if (SetField(ref _filterStation, value ?? "")) ApplyFilters(); } }
    public string FilterWorkpiece { get => _filterWorkpiece; set { if (SetField(ref _filterWorkpiece, value ?? "")) ApplyFilters(); } }
    public string FilterKeyword { get => _filterKeyword; set { if (SetField(ref _filterKeyword, value ?? "")) ApplyFilters(); } }

    public string FilterPausedText { get => _filterPausedText; set { ParsePaused(value); OnPropertyChanged(); } }
    private string _filterPausedText = "全部";
    public string FilterRecoveryText { get => _filterRecoveryText; set { ParseRecovery(value); OnPropertyChanged(); } }
    private string _filterRecoveryText = "全部";
    public string FilterTimeFromText { get => _filterTimeFromText; set { ParseTimeFrom(value); OnPropertyChanged(); } }
    private string _filterTimeFromText = "";
    public string FilterTimeToText { get => _filterTimeToText; set { ParseTimeTo(value); OnPropertyChanged(); } }
    private string _filterTimeToText = "";
    public bool FilterDuplicateOnly { get => _filterDuplicateOnly; set { if (SetField(ref _filterDuplicateOnly, value)) ApplyFilters(); } }

    public IReadOnlyList<string> DeviceTypeOptions { get; } = new[] { "全部", "天车", "机械手", "货叉", "双头镗", "斜床", "研磨机", "PLC", "打号机", "上料架" };

    private void ParseSeverity(string text)
    {
        _filterSeverityText = text ?? "全部";
        _filterSeverity = text switch { "严重" => OperationalEventSeverity.Critical, "错误" => OperationalEventSeverity.Error, "警告" => OperationalEventSeverity.Warning, "信息" => OperationalEventSeverity.Information, _ => null };
        ApplyFilters();
    }
    private void ParseCategory(string text)
    {
        _filterCategoryText = text ?? "全部";
        _filterCategory = text switch
        {
            "安全" => OperationalEventCategory.Safety, "最终失败" => OperationalEventCategory.FinalFailure,
            "结果未知" => OperationalEventCategory.PhysicalUnknown, "自动恢复" => OperationalEventCategory.AutomaticRecovery,
            "通信" => OperationalEventCategory.Communication, "阶段滞留" => OperationalEventCategory.StageStall,
            "微调" => OperationalEventCategory.FineTune, "运动" => OperationalEventCategory.Motion,
            "磁铁" => OperationalEventCategory.Magnet, "传感器" => OperationalEventCategory.Sensor,
            "握手" => OperationalEventCategory.Handshake, "应急" => OperationalEventCategory.Emergency, _ => null
        };
        ApplyFilters();
    }
    private void ParsePaused(string text)
    {
        _filterPausedText = text ?? "全部";
        _filterPaused = text switch { "已暂停" => PausedFilter.已暂停, "未暂停" => PausedFilter.未暂停, "未知" => PausedFilter.未知, _ => PausedFilter.全部 };
        ApplyFilters();
    }
    private void ParseRecovery(string text)
    {
        _filterRecoveryText = text ?? "全部";
        _filterRecovery = text switch { "已恢复" => RecoveryFilter.已恢复, "未恢复" => RecoveryFilter.未恢复, "未知" => RecoveryFilter.未知, _ => RecoveryFilter.全部 };
        ApplyFilters();
    }
    private void ParseTimeFrom(string text)
    {
        _filterTimeFromText = text ?? "";
        _filterTimeFrom = DateTime.TryParse(text, out var dt) ? dt : null;
        ApplyFilters();
    }
    private void ParseTimeTo(string text)
    {
        _filterTimeToText = text ?? "";
        _filterTimeTo = DateTime.TryParse(text, out var dt) ? dt : null;
        ApplyFilters();
    }

    // ═══════════════════════════════════════════════════════════════
    //  筛选逻辑
    // ═══════════════════════════════════════════════════════════════
    private bool MatchesFilter(OperationalEventRowViewModel row)
    {
        var e = row.SourceEvent;

        if (_filterTimeFrom.HasValue && e.LastOccurredAtUtc < _filterTimeFrom.Value) return false;
        if (_filterTimeTo.HasValue && e.LastOccurredAtUtc > _filterTimeTo.Value.AddDays(1)) return false;
        if (_filterSeverity.HasValue && e.Severity != _filterSeverity.Value) return false;
        if (_filterCategory.HasValue && e.Category != _filterCategory.Value) return false;
        if (!MatchString(_filterScope, e.Scope)) return false;
        if (!MatchString(_filterEngine, e.Engine)) return false;
        if (!MatchString(_filterDeviceType, e.DeviceType)) return false;
        if (!string.IsNullOrWhiteSpace(_filterDeviceNo) && !ContainsIgnoreCase(e.DeviceNo, _filterDeviceNo)) return false;
        if (!string.IsNullOrWhiteSpace(_filterStation) && !ContainsIgnoreCase(e.Station, _filterStation)) return false;
        if (!string.IsNullOrWhiteSpace(_filterWorkpiece) && !ContainsIgnoreCase(row.WorkpieceText, _filterWorkpiece)) return false;

        if (_filterPaused != PausedFilter.全部)
        {
            var paused = e.LatestBusinessPaused;
            bool match = _filterPaused switch
            {
                PausedFilter.已暂停 => paused.HasValue && paused.Value,
                PausedFilter.未暂停 => paused.HasValue && !paused.Value,
                PausedFilter.未知 => !paused.HasValue,
                _ => true
            };
            if (!match) return false;
        }

        if (_filterRecovery != RecoveryFilter.全部)
        {
            bool hasRecovery = e.Category == OperationalEventCategory.AutomaticRecovery
                || (e.LatestEvidence?.Recovery?.Attempted.HasValue == true && e.LatestEvidence.Recovery.Attempted.Value);
            bool match = _filterRecovery switch
            {
                RecoveryFilter.已恢复 => hasRecovery,
                RecoveryFilter.未恢复 => !hasRecovery,
                RecoveryFilter.未知 => !e.LatestEvidence?.Recovery?.Attempted.HasValue ?? true,
                _ => true
            };
            if (!match) return false;
        }

        if (_filterDuplicateOnly && e.OccurrenceCount <= 1) return false;

        if (!string.IsNullOrWhiteSpace(_filterKeyword))
        {
            var search = row.SearchText;
            if (!ContainsIgnoreCase(search, _filterKeyword)
                && !ContainsIgnoreCase(e.EventId, _filterKeyword)
                && !ContainsIgnoreCase(e.FirstActionId, _filterKeyword)
                && !ContainsIgnoreCase(e.LatestActionId, _filterKeyword))
                return false;
        }

        return true;
    }

    private static bool MatchString(string filter, string value)
        => filter == "全部" || string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string source, string keyword)
        => source.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilters()
    {
        _rows.Clear();
        foreach (var row in _allRows)
            if (MatchesFilter(row))
                _rows.Add(row);
        UpdateKpis();
    }

    // ═══════════════════════════════════════════════════════════════
    //  刷新
    // ═══════════════════════════════════════════════════════════════
    private void QueueRefresh()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _cachedStoreVersion, _store.CurrentVersion);
        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) return;
        ScheduleRefresh();
    }

    public void RequestRefresh()
    {
        if (_disposed) return;
        long storeVer = _store.CurrentVersion;
        if (storeVer == Interlocked.Read(ref _appliedVersion)) return;
        Interlocked.Exchange(ref _cachedStoreVersion, storeVer);
        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) return;
        ScheduleRefresh();
    }

    /// <summary>
    /// 合并刷新：事件风暴时最多每200ms触发一次重建，减少UI线程全量刷新频率。
    /// 所有集合操作仍留在 <see cref="DoRefresh"/> 的UI线程内执行。
    /// </summary>
    private void ScheduleRefresh()
    {
        try
        {
            _ = Task.Delay(RefreshMergeWindowMs).ContinueWith(_ =>
            {
                if (_disposed) return;
                try { _dispatcher.BeginInvoke(() => DoRefresh()); }
                catch { Interlocked.Exchange(ref _refreshQueued, 0); }
            });
        }
        catch { Interlocked.Exchange(ref _refreshQueued, 0); }
    }

    private void DoRefresh()
    {
        if (_disposed) return;
        bool scheduleFollowup = false;
        try
        {
            bool alreadySelected = _selectedRow != null;
            string? selectedId = _selectedRow?.SourceEvent.EventId;
            int selIdx = -1;

            var snapshot = _store.Snapshot();
            var existingMap = new Dictionary<string, OperationalEventRowViewModel>(StringComparer.Ordinal);
            foreach (var r in _allRows) existingMap[r.SourceEvent.EventId] = r;

            _allRows.Clear();
            foreach (var evt in snapshot.Events)
            {
                if (existingMap.TryGetValue(evt.EventId, out var existing)
                    && existing.SourceEvent.LastOccurredAtUtc == evt.LastOccurredAtUtc
                    && existing.SourceEvent.OccurrenceCount == evt.OccurrenceCount)
                {
                    _allRows.Add(existing);
                }
                else
                {
                    _allRows.Add(new OperationalEventRowViewModel(evt));
                }
            }

            ApplyFilters();

            // 保持选择
            if (alreadySelected && selectedId != null)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i].SourceEvent.EventId == selectedId) { selIdx = i; break; }
                }
                if (selIdx >= 0) SelectedRow = _rows[selIdx];
            }

            Interlocked.Exchange(ref _appliedVersion, snapshot.Version);

            // 版本追赶
            long latestStore = _store.CurrentVersion;
            if (latestStore > Interlocked.Read(ref _appliedVersion))
            {
                Interlocked.Exchange(ref _cachedStoreVersion, latestStore);
                // 版本追赶也必须经过合并窗口，不能在事件风暴中连续重建UI集合。
                scheduleFollowup = true;
                return;
            }
        }
        catch { }
        finally
        {
            UpdateKpis();
            OnPropertyChanged(nameof(CanCopy));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CurrentTotalEvents));
            if (scheduleFollowup && !_disposed)
            {
                // 保持排队标志，避免事件处理器在等待窗口内重复投递。
                ScheduleRefresh();
            }
            else
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                // 清除标志与读取版本之间的竞态由再次检查覆盖：期间到达的事件要么已自行排队，
                // 要么会在这里补排一次，不能永久遗漏刷新。
                if (!_disposed && _store.CurrentVersion > Interlocked.Read(ref _appliedVersion))
                    QueueRefresh();
            }
        }
    }

    /// <summary>进入页面时的全量刷新</summary>
    public void EnterPage()
    {
        _navState?.EnterViewing();
        RequestRefresh();
    }

    /// <summary>离开页面</summary>
    public void LeavePage()
    {
        _navState?.LeaveViewing();
    }

    // ═══════════════════════════════════════════════════════════════
    //  操作
    // ═══════════════════════════════════════════════════════════════
    public void CopyDetail()
    {
        if (_selectedRow == null)
        {
            OperationStatusText = "未选择事件，无法复制。";
            return;
        }
        try
        {
            _clipboard.SetText(_selectedRow.FullDetailText);
            OperationStatusText = "已复制完整详情到剪贴板。";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"复制失败：{ex.Message}";
        }
    }

    public async Task ExportFilteredAsync()
    {
        var events = _rows.Select(r => r.SourceEvent).ToArray();
        await ExportAsync(events, "当前筛选");
    }

    public async Task ExportAllAsync()
    {
        var events = _allRows.Select(r => r.SourceEvent).ToArray();
        await ExportAsync(events, "全部");
    }

    private async Task ExportAsync(IReadOnlyList<OperationalEvent> events, string scope)
    {
        if (events.Count == 0)
        {
            OperationStatusText = "无事件可导出。";
            return;
        }

        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string defaultName = $"异常监控_{scope}_{ts}";
        var target = _saveDialog.ShowSaveDialog(defaultName, "CSV 文件 (*.csv)|*.csv", "TXT 文件 (*.txt)|*.txt");
        if (target == null) return;

        if (IsExporting) { OperationStatusText = "导出正在进行中，请稍候。"; return; }
        IsExporting = true;
        OperationStatusText = "导出中...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _exportService.ExportAsync(target, events, _formatter, cts.Token);
            OperationStatusText = $"已导出 {events.Count} 条事件到 {System.IO.Path.GetFileName(target.Path)}。";
        }
        catch (OperationCanceledException)
        {
            OperationStatusText = "导出已取消或超时。";
        }
        catch (Exception ex)
        {
            OperationStatusText = $"导出失败：{ex.Message}";
        }
        finally { IsExporting = false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  非筛选刷新
    // ═══════════════════════════════════════════════════════════════
    private void RefreshFromStore()
    {
        var snapshot = _store.Snapshot();
        _allRows.Clear();
        foreach (var evt in snapshot.Events)
            _allRows.Add(new OperationalEventRowViewModel(evt));
        Interlocked.Exchange(ref _appliedVersion, snapshot.Version);
        ApplyFilters();
        UpdateKpis();
    }

    private void UpdateKpis()
    {
        SafetyUnknownCount = _allRows.Count(r => r.SourceEvent.Category == OperationalEventCategory.Safety || r.SourceEvent.Category == OperationalEventCategory.PhysicalUnknown);
        FinalFailureCount = _allRows.Count(r => r.SourceEvent.Category == OperationalEventCategory.FinalFailure);
        WarningCount = _allRows.Count(r => r.SourceEvent.Severity == OperationalEventSeverity.Warning);
        RecoveryCount = _allRows.Count(r => r.SourceEvent.Category == OperationalEventCategory.AutomaticRecovery);
        StageStallCount = _allRows.Count(r => r.SourceEvent.Category == OperationalEventCategory.StageStall);
        OnPropertyChanged(nameof(TotalReceived));
        OnPropertyChanged(nameof(AggregatedCount));
        OnPropertyChanged(nameof(EvictedCount));
        OnPropertyChanged(nameof(ReporterFailureCount));
        OnPropertyChanged(nameof(CurrentTotalEvents));
        OnPropertyChanged(nameof(CanExport));
    }

    private int CountBy(params OperationalEventCategory[] categories)
        => _allRows.Count(r => categories.Contains(r.SourceEvent.Category));

    private int CountBySeverity(OperationalEventSeverity severity)
        => _allRows.Count(r => r.SourceEvent.Severity == severity);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= QueueRefresh;
        _navState?.Dispose();
    }

}
