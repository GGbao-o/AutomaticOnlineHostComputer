# Runtime Diagnostics Implementation Plan

> **For agentic workers:** Execute this plan inline in the current session. Subagent execution is intentionally disabled for this repository task, and the user explicitly requested no Git commit.

**Goal:** Add a read-only “运行诊断” page that explains current dispatch blockers, communication/snapshot health, crane tasks, rack identity mismatches, and manual-confirmation states without changing automatic-line behavior.

**Architecture:** Existing engines remain the only owners of control state. `HomeViewModel` synchronously aggregates their existing in-memory snapshots into immutable diagnostic rows; a dedicated view model maps those rows to two small UI collections and KPI counts. A page-local `DispatcherTimer` refreshes once per second only while the cached page is visible.

**Tech Stack:** C# 10, .NET 6 WPF, CommunityToolkit-style existing `ObservableObject`, Microsoft.Extensions.DependencyInjection, existing flow-engine snapshot records.

---

## File map

- Create `Service/FlowEngine/RuntimeDiagnosticsSnapshots.cs`: immutable diagnostic enums, item, and page snapshot.
- Modify `Presentation/ViewModels/Home/HomeViewModel.cs`: add one synchronous, read-only aggregation method plus private formatting helpers.
- Create `Presentation/ViewModels/Home/RuntimeDiagnosticsViewModel.cs`: UI collections and KPI mapping; no device-service dependency.
- Create `Views/RuntimeDiagnostics/RuntimeDiagnosticsView.xaml`: page layout using existing visual style.
- Create `Views/RuntimeDiagnostics/RuntimeDiagnosticsView.xaml.cs`: visible-only one-second timer lifecycle.
- Modify `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`: register the new view model as a singleton.
- Modify `Infrastructure/Navigation/CachedNavigationService.cs`: create/cache the new page.
- Modify `MainWindow.xaml`: place navigation after “全流程状态”.
- Modify `Docs/整线技术手册.md`: document page content and non-control boundary.
- Modify `Docs/日志审查与故障定位手册.md`: document the recommended diagnostic sequence.

### Task 1: Immutable diagnostic snapshot model

**Files:**
- Create: `Service/FlowEngine/RuntimeDiagnosticsSnapshots.cs`

- [ ] **Step 1: Add diagnostic types**

Define:

```csharp
public enum RuntimeDiagnosticCategory
{
    FrontReadiness,
    Communication,
    SignalFreshness,
    RackConsistency,
    CraneTask,
    ManualConfirmation
}

public enum RuntimeDiagnosticLevel
{
    Normal,
    Waiting,
    Warning,
    ManualConfirmation
}

public sealed record RuntimeDiagnosticItem(
    RuntimeDiagnosticCategory Category,
    string Section,
    string Scope,
    string Name,
    RuntimeDiagnosticLevel Level,
    string State,
    string Detail,
    DateTime CapturedAtUtc)
{
    public string LevelText => Level switch
    {
        RuntimeDiagnosticLevel.Normal => "正常",
        RuntimeDiagnosticLevel.Waiting => "等待",
        RuntimeDiagnosticLevel.Warning => "警告",
        RuntimeDiagnosticLevel.ManualConfirmation => "人工确认",
        _ => "未知"
    };
}

public sealed record RuntimeDiagnosticsSnapshot(
    IReadOnlyList<RuntimeDiagnosticItem> Items,
    DateTime CapturedAtUtc);
```

Add XML comments stating that these objects hold no device connection, lock, callback, or control command.

- [ ] **Step 2: Build to verify the standalone model compiles**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: exit code 0; no new compile error.

### Task 2: Read-only aggregation in HomeViewModel

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] **Step 1: Add the public aggregation entry point**

Add:

```csharp
public RuntimeDiagnosticsSnapshot GetRuntimeDiagnosticsSnapshot()
```

The method creates a local `List<RuntimeDiagnosticItem>` and `DateTime nowUtc`, then performs only these existing reads:

- private `GetFrontDispatchReadiness(1/2)`;
- front engine `DeviceStatus` fields;
- rear engine `DeviceStatus`, `GetCraneTaskSnapshot()`, `GetSkewStationSnapshots()`, and `GetTransferRackSnapshots()`;
- front engine `GetCraneTaskSnapshot()`;
- grinding engine `GetCraneTaskSnapshot()` and `GetGrinderStationSnapshots()`.

It must not be `async`, and must not call any method containing a protocol read, connection attempt, database access, file access, control command, or `OnPropertyChanged`.

- [ ] **Step 2: Add exact diagnostic mapping helpers**

Add private static helpers with these responsibilities:

```csharp
private static void AddFrontReadinessDiagnostic(
    ICollection<RuntimeDiagnosticItem> items,
    FrontDispatchReadinessSnapshot readiness,
    DateTime nowUtc)
```

Map `CanAccept=true` to `Normal/可接板`; otherwise map to `Waiting/暂不可接板` and preserve `RejectReason` exactly.

```csharp
private static void AddCommunicationDiagnostic(
    ICollection<RuntimeDiagnosticItem> items,
    string scope,
    string name,
    bool engineRunning,
    bool connected,
    DateTime nowUtc)
```

Map an unstarted engine to `Waiting/未启动`; a running but disconnected device to `Warning/断开`; a connected device to `Normal/已连接`.

```csharp
private static void AddFreshnessDiagnostic(
    ICollection<RuntimeDiagnosticItem> items,
    string scope,
    string name,
    bool engineRunning,
    bool valid,
    DateTime capturedAtUtc,
    DateTime nowUtc)
```

Use the same 3-second freshness threshold as front dispatch readiness. An unstarted engine is `Waiting/未启动`; invalid or older than 3 seconds is `Warning/无有效快照`; otherwise it is `Normal/快照有效`. Do not perform a fresh device read.

```csharp
private static void AddRackDiagnostics(
    ICollection<RuntimeDiagnosticItem> items,
    string scope,
    IEnumerable<TransferRackSnapshot> racks,
    DateTime nowUtc)
```

Map unavailable physical signals to `Warning/信号不可用`; identity mismatch to `Warning/身份不一致`; otherwise `Normal/一致`. Detail includes physical plate state and cached workpiece identity.

```csharp
private static void AddCraneDiagnostic(
    ICollection<RuntimeDiagnosticItem> items,
    string scope,
    CraneTaskSnapshot? snapshot,
    DateTime nowUtc)
```

Always add one row per crane. A null snapshot becomes `Waiting/未初始化`. A manual-confirmation task becomes `ManualConfirmation/需人工确认`; other active tasks become `Normal/执行中`; idle becomes `Normal/空闲`. Detail includes stage, source/target, workpiece identity, and manual text when present.

```csharp
private static void AddManualStationDiagnostics(
    ICollection<RuntimeDiagnosticItem> items,
    string scope,
    IEnumerable<ProcessStationSnapshot> stations,
    DateTime nowUtc)
```

Only add rows whose `NeedsManualWorkpiece` is true, with level `ManualConfirmation` and detail containing station state and any known workpiece identity.

```csharp
private static void AddSnapshotFailure(
    ICollection<RuntimeDiagnosticItem> items,
    string section,
    string scope,
    string name,
    DateTime nowUtc)
```

When one source throws, add exactly one `Warning/快照暂不可用` row. Do not write a repeated Console log from the one-second UI refresh.

- [ ] **Step 3: Isolate each source failure**

Wrap front readiness, each front device-status group, each rear group, and grinding snapshots in separate `try/catch` blocks. A failure in one block may only add that block’s failure row and must not stop other rows from being collected.

- [ ] **Step 4: Build and statically verify the aggregation boundary**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj
rg -n "GetRuntimeDiagnosticsSnapshot|Read|Write|Connect|OpenAsync|ExecuteAsync|Start\(|Pause\(|Resume\(|Emergency|Move" Presentation\ViewModels\Home\HomeViewModel.cs
```

Expected: build succeeds. Manual review confirms the new method calls only existing snapshot getters and reads memory fields; any protocol/control matches belong to pre-existing methods outside the new diagnostic block.

### Task 3: RuntimeDiagnosticsViewModel

**Files:**
- Create: `Presentation/ViewModels/Home/RuntimeDiagnosticsViewModel.cs`
- Modify: `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add the view model**

The class derives from existing `ObservableObject`, receives `HomeViewModel` in its constructor, and exposes:

```csharp
public ObservableCollection<RuntimeDiagnosticItem> HealthItems { get; }
public ObservableCollection<RuntimeDiagnosticItem> AttentionItems { get; }
public int ReadyLineCount { get; private set; }
public int CommunicationIssueCount { get; private set; }
public int StaleSnapshotCount { get; private set; }
public int ManualConfirmationCount { get; private set; }
public string LastRefreshText { get; private set; }
public string AttentionEmptyText { get; private set; }
public void Refresh()
```

`Refresh()` replaces `HealthItems` with categories `FrontReadiness`, `Communication`, `SignalFreshness`, and `RackConsistency`. It replaces `AttentionItems` with all five `CraneTask` rows plus `ManualConfirmation` rows. Counts are calculated from the immutable snapshot. If aggregation unexpectedly throws, keep the UI alive, clear collections, add one local warning row, and show the current refresh time.

- [ ] **Step 2: Register the view model**

Add `services.AddSingleton<RuntimeDiagnosticsViewModel>();` immediately after `AttentionMonitorViewModel` registration, with a comment that it is a presentation-only singleton and does not own device connections.

- [ ] **Step 3: Build**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: exit code 0.

### Task 4: Page and visible-only refresh lifecycle

**Files:**
- Create: `Views/RuntimeDiagnostics/RuntimeDiagnosticsView.xaml`
- Create: `Views/RuntimeDiagnostics/RuntimeDiagnosticsView.xaml.cs`

- [ ] **Step 1: Add the XAML page**

Use the same `#E9EEF5` page background, white rounded panels, border colors, typography, and read-only `DataGrid` defaults as `AttentionMonitorView`.

Layout:

1. title “运行诊断” and subtitle “只解释当前内存状态，不读取设备、不控制流程”；
2. five KPI cards for available lines, communication issues, stale snapshots, manual confirmations, and last refresh;
3. left panel “当前等待与设备健康” bound to `HealthItems`;
4. right panel “天车任务与人工确认” bound to `AttentionItems`.

Both grids use row virtualization and read-only columns: `范围`, `项目`, `等级`, `状态`, `说明`. DataGrid row triggers color `警告` light yellow and `人工确认` light red; normal task execution remains neutral/green and normal waits remain neutral.

- [ ] **Step 2: Add page lifecycle code**

Implement a page-owned `DispatcherTimer` with `Interval = TimeSpan.FromSeconds(1)`. Resolve the singleton view model from `App.Services`, set `DataContext`, and use `IsVisibleChanged` plus `Unloaded`:

```csharp
private void UpdateRefreshState()
{
    if (IsVisible)
    {
        _viewModel.Refresh();
        _refreshTimer.Start();
    }
    else
    {
        _refreshTimer.Stop();
    }
}
```

The tick handler first checks `IsVisible`; if false it stops the timer. No `Task.Run`, no background thread, and no device call is permitted.

- [ ] **Step 3: Build**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: exit code 0 and XAML compilation succeeds.

### Task 5: Navigation integration

**Files:**
- Modify: `Infrastructure/Navigation/CachedNavigationService.cs`
- Modify: `MainWindow.xaml`

- [ ] **Step 1: Add page namespace and cache mapping**

Import `AutomaticOnlineHostComputer.Views.RuntimeDiagnostics` and add:

```csharp
"runtimeDiagnostics" => new RuntimeDiagnosticsView(),
```

between `flowStatus` and `attentionMonitor`.

- [ ] **Step 2: Add the main menu item**

Insert:

```xml
<MenuItem
    Click="MenuItem_Click"
    Header="运行诊断"
    Tag="runtimeDiagnostics" />
```

between “全流程状态” and “异常监控”.

- [ ] **Step 3: Build and verify navigation tags**

Run:

```powershell
dotnet build AutomaticOnlineHostComputer.csproj
rg -n "flowStatus|runtimeDiagnostics|attentionMonitor" MainWindow.xaml Infrastructure\Navigation\CachedNavigationService.cs
```

Expected: build succeeds and both files show the same three tags in the required order.

### Task 6: Operator documentation

**Files:**
- Modify: `Docs/整线技术手册.md`
- Modify: `Docs/日志审查与故障定位手册.md`

- [ ] **Step 1: Document the page in the technical manual**

Add a “运行诊断页面（只读）” section describing the KPI meanings, two lists, one-second visible-only refresh, and the explicit limits: no device reads, no database, no control command, no automatic recovery.

- [ ] **Step 2: Document the troubleshooting sequence**

Add this recommended sequence:

1. First inspect front readiness and its first rejection reason;
2. Then inspect communication/snapshot health;
3. Then inspect rack physical/software identity consistency;
4. Finally inspect crane/manual-confirmation rows and use the existing emergency center if action is required;
5. Use logs for detailed chronology after the page narrows the scope.

State that “等待” can be a normal process condition and that page data is an existing in-memory snapshot, not an extra real-time device read.

- [ ] **Step 3: Scan documentation for contradictory claims**

Run:

```powershell
rg -n "运行诊断|只读|不读取设备|不控制流程|1 秒|一秒" Docs\整线技术手册.md Docs\日志审查与故障定位手册.md Docs\superpowers\specs\2026-07-15-runtime-diagnostics-design.md
```

Expected: both user-facing manuals document the same isolation boundary as the design spec.

### Task 7: Final verification and business-boundary review

**Files:**
- Review all files changed by Tasks 1–6.

- [ ] **Step 1: Run a fresh complete build**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: exit code 0, zero errors; compare warning count with the pre-change build and ensure no new warning is introduced.

- [ ] **Step 2: Scan new files for forbidden dependencies**

Run:

```powershell
rg -n "Mitsubishi|Modbus|Fanuc|Plc|ConnectAsync|ReadAsync|WriteAsync|OpenAsync|ExecuteAsync|EmergencyClear|Start\(|Pause\(|Resume\(|Move" Service\FlowEngine\RuntimeDiagnosticsSnapshots.cs Presentation\ViewModels\Home\RuntimeDiagnosticsViewModel.cs Views\RuntimeDiagnostics
```

Expected: no matches except plain explanatory comments; the new page/model/view model have no protocol or control dependency.

- [ ] **Step 3: Review the production-code diff**

Run:

```powershell
git diff -- MainWindow.xaml Infrastructure\Navigation\CachedNavigationService.cs Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs Presentation\ViewModels\Home\HomeViewModel.cs Service\FlowEngine\RuntimeDiagnosticsSnapshots.cs Presentation\ViewModels\Home\RuntimeDiagnosticsViewModel.cs Views\RuntimeDiagnostics\RuntimeDiagnosticsView.xaml Views\RuntimeDiagnostics\RuntimeDiagnosticsView.xaml.cs
```

Expected: changes are limited to immutable snapshots, display aggregation, DI, navigation, and UI. No existing action sequence, state transition, timeout, protocol address, signal handshake, or emergency method is modified.

- [ ] **Step 4: Do not commit**

Leave all changes in the working tree as requested by the user. Report build evidence and the isolation review in the handoff.
