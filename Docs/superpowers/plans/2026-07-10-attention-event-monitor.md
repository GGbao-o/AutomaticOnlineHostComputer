# Attention Event Monitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a lifecycle-only WPF monitoring page for human-attention safety alarms, warnings, and emergency operations without affecting production behavior.

**Architecture:** A DI singleton stores a bounded, thread-safe in-memory event snapshot. Existing centralized HomeViewModel alarm handlers and the emergency dialog append structured events after their original business actions; a cached navigation page reads the snapshot through a dedicated view model and only renders/filter events.

**Tech Stack:** .NET 8, WPF, Microsoft.Extensions.DependencyInjection, `ObservableCollection`, `ICollectionView`.

---

### Task 1: Create bounded attention-event storage

**Files:**
- Create: `Service/AttentionEventCenter.cs`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create the event data model and event center**

```csharp
public enum AttentionEventKind { SafetyAlarm, Warning, Emergency }

public sealed record AttentionEvent(
    long Sequence, DateTime OccurredAt, AttentionEventKind Kind,
    string Scope, string Source, string Message, string? Result);

public sealed class AttentionEventCenter
{
    public const int Capacity = 500;
    private readonly object _gate = new();
    private readonly List<AttentionEvent> _events = new();
    private long _nextSequence;

    public event Action? Changed;

    public void Record(AttentionEventKind kind, string scope, string source,
        string message, string? result = null)
    {
        try
        {
            lock (_gate)
            {
                _events.Add(new AttentionEvent(++_nextSequence, DateTime.Now,
                    kind, scope, source, message, result));
                if (_events.Count > Capacity) _events.RemoveAt(0);
            }
            Changed?.Invoke();
        }
        catch { /* monitoring must never affect production */ }
    }

    public IReadOnlyList<AttentionEvent> Snapshot()
    {
        lock (_gate) return _events.OrderByDescending(x => x.Sequence).ToArray();
    }
}
```

- [ ] **Step 2: Register one lifecycle-scoped singleton**

```csharp
services.AddSingleton<AttentionEventCenter>();
```

- [ ] **Step 3: Build after the isolated storage change**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: build succeeds with no new errors.

### Task 2: Add a read-only monitoring view model and page

**Files:**
- Create: `Presentation/ViewModels/AttentionMonitorViewModel.cs`
- Create: `Views/AttentionMonitor/AttentionMonitorView.xaml`
- Create: `Views/AttentionMonitor/AttentionMonitorView.xaml.cs`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add a singleton view model that only reads event snapshots**

Implement `AttentionMonitorViewModel` with `ObservableCollection<AttentionEvent> Events`, `ICollectionView FilteredEvents`, summary properties for each kind and last-event text, and string filter properties `SelectedKind` / `SelectedScope`. Subscribe to `AttentionEventCenter.Changed`; use `Application.Current.Dispatcher.BeginInvoke` to replace the UI collection from `Snapshot()`. The filter must accept `全部`, `红色安全异常`, `黄色警告`, `应急操作` and scopes `全部`, `1号线`, `2号线`, `动平衡`, `研磨`.

- [ ] **Step 2: Register the view model**

```csharp
services.AddSingleton<AttentionMonitorViewModel>();
```

- [ ] **Step 3: Create the WPF page**

Bind four cards to `SafetyAlarmCount`, `WarningCount`, `EmergencyCount`, and `LastEventText`. Bind two ComboBoxes to the filter properties and a read-only DataGrid to `FilteredEvents` with columns `OccurredAt`, `Kind`, `Source`, `Message`, and `Result`. Use the same light background, card borders, red/yellow/blue accent colors, and scroll behavior as `Views/FlowStatus/FlowStatusView.xaml`. Include the fixed text `仅保留当前程序生命周期；程序关闭后自动清空`.

- [ ] **Step 4: Create page code-behind with DI data context only**

```csharp
public partial class AttentionMonitorView : UserControl
{
    public AttentionMonitorView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AttentionMonitorViewModel>();
    }
}
```

- [ ] **Step 5: Build after the page change**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: build succeeds with the new XAML compiled.

### Task 3: Add cached navigation and menu placement

**Files:**
- Modify: `MainWindow.xaml`
- Modify: `Infrastructure/Navigation/CachedNavigationService.cs`

- [ ] **Step 1: Add the menu item immediately after 全流程状态**

```xml
<MenuItem Click="MenuItem_Click" Header="异常监控" Tag="attentionMonitor" />
```

- [ ] **Step 2: Add the cached page mapping**

```csharp
"attentionMonitor" => new AttentionMonitorView(),
```

- [ ] **Step 3: Build after navigation integration**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: build succeeds and the menu target resolves.

### Task 4: Record existing red and yellow attention events

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 1: Inject the event center without changing alarm control flow**

Change the constructor to receive `AttentionEventCenter attentionEvents`, assign `_attentionEvents`, and leave engine construction unchanged.

- [ ] **Step 2: Add one record call at each centralized alarm point**

Append a non-blocking `_attentionEvents.Record(...)` after the existing pause decisions and before UI dispatch in:

```csharp
HandleCraneZeroPositionAlarm(alarm) // SafetyAlarm, scope from crane number
HandleLineSafetyAlarm(line, source, message) // SafetyAlarm, $"{line}号线"
HandleSharedManipulatorSafetyAlarm(message) // SafetyAlarm, "全局"
HandleStandaloneSafetyAlarm(engine, message) // SafetyAlarm, engine
HandleRearCraneWarning(line, message) // Warning, $"{line}号线"
```

Make `HandleRearCraneWarning` an instance method so it can use `_attentionEvents`; preserve its dispatch and MessageBox behavior exactly.

- [ ] **Step 3: Build after alarm recording integration**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: build succeeds and no engine/device API changed.

### Task 5: Record emergency operations from the existing dialog

**Files:**
- Modify: `Views/Home/Dialogs/EmergencyCenterDialog.xaml.cs`

- [ ] **Step 1: Resolve the event center once in the dialog constructor**

```csharp
_attentionEvents = App.Services.GetRequiredService<AttentionEventCenter>();
```

- [ ] **Step 2: Record every completed front, skew, balancing, and grinding emergency call**

After each awaited result, call `Record(AttentionEventKind.Emergency, scope, source, result, resultState)` where `resultState` is `仅清软件` for the second-stage action, `失败` if `ShowEmergencyFailureIfNeeded` would classify it as failure, otherwise `成功`. In every corresponding `catch`, record `失败` with exception type and message before retaining the existing dialog error behavior.

- [ ] **Step 3: Build after emergency recording integration**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: build succeeds and emergency methods still return their existing text unchanged.

### Task 6: Verify lifecycle, filters, and production isolation

**Files:**
- Modify: `task_plan.md`
- Modify: `progress.md`

- [ ] **Step 1: Run the complete build**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: exit code 0.

- [ ] **Step 2: Perform static call-path checks**

Run: `rg -n "AttentionEventCenter|AttentionMonitor|\.Record\(" Service Presentation Views Infrastructure MainWindow.xaml`

Expected: records appear only in HomeViewModel and EmergencyCenterDialog; no FlowEngine, PLC client, database, or Console interception changes appear.

- [ ] **Step 3: Update task tracking and commit implementation**

```bash
git add -- Service/AttentionEventCenter.cs Presentation/ViewModels/AttentionMonitorViewModel.cs Views/AttentionMonitor MainWindow.xaml Infrastructure/Navigation/CachedNavigationService.cs Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs Presentation/ViewModels/Home/HomeViewModel.cs Views/Home/Dialogs/EmergencyCenterDialog.xaml.cs task_plan.md progress.md
git commit -m "feat: add lifecycle attention event monitor"
```
