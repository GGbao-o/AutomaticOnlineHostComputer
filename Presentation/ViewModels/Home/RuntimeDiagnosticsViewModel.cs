using System;
using System.Collections.ObjectModel;
using System.Linq;
using AutomaticOnlineHostComputer.Presentation.ViewModels;
using AutomaticOnlineHostComputer.Service;

namespace AutomaticOnlineHostComputer.Presentation.ViewModels.Home;

/// <summary>
/// “运行诊断”页面的纯展示 ViewModel。
/// 只把 HomeViewModel 提供的不可变内存快照映射到 UI，不持有任何设备服务，也不执行流程控制。
/// </summary>
public sealed class RuntimeDiagnosticsViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private int _readyLineCount;
    private int _communicationIssueCount;
    private int _staleSnapshotCount;
    private int _manualConfirmationCount;
    private string _lastRefreshText = "尚未刷新";
    private string _attentionEmptyText = "当前无人工确认项";

    public RuntimeDiagnosticsViewModel(HomeViewModel home)
    {
        _home = home;
    }

    /// <summary>前端准入、通信、信号新鲜度和中转架一致性。</summary>
    public ObservableCollection<RuntimeDiagnosticItem> HealthItems { get; } = new();

    /// <summary>五台天车当前任务，以及确实需要人工核对的工位/中转架。</summary>
    public ObservableCollection<RuntimeDiagnosticItem> AttentionItems { get; } = new();

    public int ReadyLineCount { get => _readyLineCount; private set => SetField(ref _readyLineCount, value); }
    public int CommunicationIssueCount { get => _communicationIssueCount; private set => SetField(ref _communicationIssueCount, value); }
    public int StaleSnapshotCount { get => _staleSnapshotCount; private set => SetField(ref _staleSnapshotCount, value); }
    public int ManualConfirmationCount { get => _manualConfirmationCount; private set => SetField(ref _manualConfirmationCount, value); }
    public string LastRefreshText { get => _lastRefreshText; private set => SetField(ref _lastRefreshText, value); }
    public string AttentionEmptyText { get => _attentionEmptyText; private set => SetField(ref _attentionEmptyText, value); }

    /// <summary>
    /// 在 UI 线程同步刷新少量内存项。
    /// 页面负责控制调用频率；本方法不启动线程、定时器或设备读取。
    /// </summary>
    public void Refresh()
    {
        RuntimeDiagnosticsSnapshot snapshot;
        try
        {
            snapshot = _home.GetRuntimeDiagnosticsSnapshot();
        }
        catch
        {
            // 最外层保险只保护页面可用性；即使聚合出现未知异常，也不能影响主页面或生产引擎。
            var nowUtc = DateTime.UtcNow;
            snapshot = new RuntimeDiagnosticsSnapshot(new[]
            {
                new RuntimeDiagnosticItem(RuntimeDiagnosticCategory.SignalFreshness, "页面",
                    "运行诊断", "快照聚合", RuntimeDiagnosticLevel.Warning, "暂不可用",
                    "读取内存诊断快照失败；页面未执行设备重读、重连或流程控制", nowUtc)
            }, nowUtc);
        }

        var health = snapshot.Items.Where(x =>
            x.Category is RuntimeDiagnosticCategory.FrontReadiness
                or RuntimeDiagnosticCategory.Communication
                or RuntimeDiagnosticCategory.SignalFreshness
                or RuntimeDiagnosticCategory.RackConsistency);
        Replace(HealthItems, health);

        // 天车无论忙闲始终显示；人工确认项则按当前快照追加，便于操作员一次看全。
        var attention = snapshot.Items.Where(x =>
            x.Category == RuntimeDiagnosticCategory.CraneTask
            || x.Category == RuntimeDiagnosticCategory.ManualConfirmation);
        Replace(AttentionItems, attention);

        ReadyLineCount = snapshot.Items.Count(x =>
            x.Category == RuntimeDiagnosticCategory.FrontReadiness
            && x.Level == RuntimeDiagnosticLevel.Normal);
        CommunicationIssueCount = snapshot.Items.Count(x =>
            x.Category == RuntimeDiagnosticCategory.Communication
            && x.Level == RuntimeDiagnosticLevel.Warning);
        StaleSnapshotCount = snapshot.Items.Count(x =>
            x.Category == RuntimeDiagnosticCategory.SignalFreshness
            && x.Level == RuntimeDiagnosticLevel.Warning);
        ManualConfirmationCount = snapshot.Items.Count(x =>
            x.Level == RuntimeDiagnosticLevel.ManualConfirmation);
        LastRefreshText = snapshot.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        AttentionEmptyText = ManualConfirmationCount == 0
            ? "当前无人工确认项；下表仍显示五台天车当前任务。"
            : $"当前有 {ManualConfirmationCount} 项需要人工核对；页面本身不会执行清除或恢复。";
    }

    private static void Replace(ObservableCollection<RuntimeDiagnosticItem> target,
        System.Collections.Generic.IEnumerable<RuntimeDiagnosticItem> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
