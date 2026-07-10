# Flow Cache Detail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show up to two cached workpiece diameter/length summaries beneath each existing full-flow cache count.

**Architecture:** The three flow engines expose lock-protected, copied cache snapshots. HomeViewModel formats those snapshots into three read-only binding strings, and FlowStatusView renders the strings beneath the existing counts; no code writes or mutates a queue.

**Tech Stack:** .NET 8, WPF, existing `WorkpieceCache` and `ObservableObject` bindings.

---

### Task 1: Expose read-only cache snapshots

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs:595-614`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs:591-624`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs:68-76`

- [ ] **Step 1: Add identical safe snapshot methods beside each existing CachedCount property**

```csharp
public WorkpieceCache[] GetCachedWorkpiecesSnapshot()
{
    lock (_cacheLock) return _cachedList.ToArray();
}
```

The method only copies the list guarded by the existing cache lock. It must not read the FIFO queue, PLC, device service, or mutate any cache state.

- [ ] **Step 2: Build the snapshot-only change**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: exit code 0.

### Task 2: Format summaries in the home view model

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:386-388`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:1071-1081`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs:528-536,1608-1611,2621-2623,2650-2651`

- [ ] **Step 1: Add three read-only binding properties**

```csharp
public string Line1CacheDetail => FormatCacheDetail(_line1Engine?.GetCachedWorkpiecesSnapshot());
public string Line2CacheDetail => FormatCacheDetail(_line2Engine?.GetCachedWorkpiecesSnapshot());
public string GrindingCacheDetail => FormatCacheDetail(_grindingEngine?.GetCachedWorkpiecesSnapshot());
```

- [ ] **Step 2: Add the formatter**

```csharp
private static string FormatCacheDetail(IEnumerable<WorkpieceCache>? workpieces)
{
    var firstTwo = workpieces?.Take(2).ToArray() ?? Array.Empty<WorkpieceCache>();
    if (firstTwo.Length == 0) return "无缓存";
    return string.Join("\n", firstTwo.Select(wp => wp.Length > 0
        ? $"D={wp.Diameter}mm  L={wp.Length}mm"
        : $"D={wp.Diameter}mm"));
}
```

- [ ] **Step 3: Raise detail property changes at every existing cache-count refresh**

Whenever existing code calls `OnPropertyChanged(nameof(Line1CachedCount))`, add `OnPropertyChanged(nameof(Line1CacheDetail))`; do the equivalent for line 2 and grinding. Do not add a timer or a new refresh loop.

- [ ] **Step 4: Build the view-model change**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: exit code 0.

### Task 3: Render the summaries in full-flow status cards

**Files:**
- Modify: `Views/FlowStatus/FlowStatusView.xaml:441-470`

- [ ] **Step 1: Add one wrapping detail TextBlock below each existing count**

```xml
<TextBlock Margin="0,4,0,0" FontSize="11" Foreground="{StaticResource MutedBrush}"
           Text="{Binding Line1CacheDetail, FallbackValue=无缓存}"
           TextWrapping="Wrap" />
```

Bind the second and third cards to `Line2CacheDetail` and `GrindingCacheDetail`. Keep the existing colors, count bindings, card widths, and all process-status controls unchanged.

- [ ] **Step 2: Build and statically check isolation**

Run: `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`

Expected: exit code 0.

Run: `rg -n "GetCachedWorkpiecesSnapshot|CacheDetail|_cacheQueue\.Enqueue|_cacheQueue\.TryDequeue|Write.*Async" Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs Views/FlowStatus/FlowStatusView.xaml`

Expected: new symbols appear only in snapshot methods, display properties, notifications, and bindings; no new queue write or device-write call is introduced.

- [ ] **Step 3: Commit the isolated display enhancement**

```bash
git add -- Service/FlowEngine/Line1FrontFlowEngine.cs Service/FlowEngine/Line2FrontFlowEngine.cs Service/FlowEngine/GrindingFlowEngine.cs Presentation/ViewModels/Home/HomeViewModel.cs Views/FlowStatus/FlowStatusView.xaml Docs/superpowers/plans/2026-07-10-flow-cache-detail.md
git commit -m "feat: show flow cache workpiece details"
```
