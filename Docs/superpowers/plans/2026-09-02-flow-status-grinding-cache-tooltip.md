# 全流程状态研磨缓存悬浮明细 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让全流程状态页“研磨缓存”整张卡片悬浮时，按 FIFO 顺序显示全部缓存工件的版号、序号、直径和可用长度。

**Architecture:** 在 `HomeViewModel` 增加只读文本属性，基于 `GrindingFlowEngine` 的线程安全缓存快照逐行格式化全部工件。XAML 仅把该属性绑定到研磨缓存 KPI 外层 `Border` 的 `ToolTip`，不参与 PLC 通信、研磨调度或缓存修改。

**Tech Stack:** .NET 8、WPF、MVVM、现有 `WorkpieceCache` 和 `GrindingFlowEngine.GetCachedWorkpiecesSnapshot()`。

---

### Task 1: 为全部研磨缓存明细建立回归契约

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/Presentation/GrindingCacheTooltipContractTests.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [x] **Step 1: 写入失败的源代码契约测试**

```csharp
[Fact]
public void Grinding_cache_tooltip_formats_all_items_with_optional_identity_and_length()
{
    var source = File.ReadAllText(HomeViewModelPath);

    Assert.Contains("public string GrindingCacheTooltip", source);
    Assert.Contains("FormatGrindingCacheTooltip", source);
    Assert.Contains("wp.PlateNo", source);
    Assert.Contains("wp.Sequence", source);
    Assert.Contains("D={wp.Diameter}mm", source);
    Assert.Contains("wp.Length > 0", source);
    Assert.Contains("L={wp.Length}mm", source);
    Assert.DoesNotContain("Take(2)", ExtractMethod(source, "FormatGrindingCacheTooltip"));
}
```

`ExtractMethod` 沿用本测试项目现有的花括号匹配辅助函数，范围限定为 `FormatGrindingCacheTooltip` 方法体，避免把现有摘要 `FormatCacheDetail` 的 `Take(2)` 误判为问题。

- [x] **Step 2: 运行契约测试，确认当前代码失败**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingCacheTooltipContractTests
```

预期：失败，原因是当前 `HomeViewModel` 尚无 `GrindingCacheTooltip` 和 `FormatGrindingCacheTooltip`。

- [x] **Step 3: 添加完整缓存悬浮文本属性和纯格式化器**

在 `HomeViewModel` 的现有 `GrindingCachedCount`、`GrindingCacheDetail` 属性旁添加：

```csharp
/// <summary>研磨FIFO全部缓存工件的只读悬浮明细。</summary>
public string GrindingCacheTooltip
    => FormatGrindingCacheTooltip(_grindingEngine?.GetCachedWorkpiecesSnapshot());

private static string FormatGrindingCacheTooltip(IEnumerable<WorkpieceCache>? workpieces)
{
    var snapshot = workpieces?.ToArray() ?? Array.Empty<WorkpieceCache>();
    if (snapshot.Length == 0) return "无缓存工件";

    return string.Join(Environment.NewLine, snapshot.Select((wp, index) =>
    {
        var fields = new List<string>();
        if (!string.IsNullOrWhiteSpace(wp.PlateNo)) fields.Add($"版号={wp.PlateNo}");
        if (!string.IsNullOrWhiteSpace(wp.Sequence)) fields.Add($"序={wp.Sequence}");
        fields.Add($"D={wp.Diameter}mm");
        if (wp.Length > 0) fields.Add($"L={wp.Length}mm");
        return $"{index + 1}. {string.Join("，", fields)}";
    }));
}
```

保留既有 `FormatCacheDetail` 及其 `Take(2)`，确保卡片内摘要不改变。新格式化器不调用设备服务，也不改写快照、队列或引擎状态。

- [x] **Step 4: 在所有现有缓存刷新点通知 Tooltip 属性**

在下列与 `GrindingCachedCount`、`GrindingCacheDetail` 相邻的通知区增加：

```csharp
OnPropertyChanged(nameof(GrindingCacheTooltip));
```

位置包括状态轮询刷新、研磨应急清理结束后的刷新，以及 `LoadAsync` 轻量刷新。这样缓存入队、出队和应急清理后的下一次 UI 刷新都会更新 Tooltip。

- [x] **Step 5: 重跑契约测试，确认通过**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingCacheTooltipContractTests
```

预期：通过；格式化器包含全部快照、可选版号/序号、始终直径和可选长度，且不截断到两件。

### Task 2: 将整张研磨缓存卡片绑定到悬浮明细

**Files:**
- Modify: `Views/FlowStatus/FlowStatusView.xaml:473-488`
- Modify: `AutomaticOnlineHostComputer.Tests/Presentation/GrindingCacheTooltipContractTests.cs`

- [x] **Step 1: 扩展失败的 XAML 契约**

在同一测试文件新增：

```csharp
[Fact]
public void Grinding_cache_kpi_card_binds_the_full_tooltip_on_its_outer_border()
{
    var xaml = File.ReadAllText(FlowStatusViewPath);
    var card = ExtractBetween(xaml, "<TextBlock Foreground=\"{StaticResource MutedBrush}\" Text=\"研磨缓存\" />", "</Border>");

    Assert.Contains("ToolTip=\"{Binding GrindingCacheTooltip}\"", card);
    Assert.Contains("ToolTipService.InitialShowDelay=\"1000\"", card);
    Assert.Contains("Text=\"{Binding GrindingCachedCount", card);
    Assert.Contains("Text=\"{Binding GrindingCacheDetail", card);
}
```

- [x] **Step 2: 运行契约测试，确认当前 XAML 失败**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingCacheTooltipContractTests
```

预期：失败，原因是当前研磨缓存 KPI 外层 `Border` 没有 `GrindingCacheTooltip` 绑定。

- [x] **Step 3: 为研磨缓存外层 KPI Border 添加 Tooltip**

将 `Views/FlowStatus/FlowStatusView.xaml` 中“研磨缓存”对应外层 `Border` 改为：

```xml
<Border
    Height="98"
    Style="{StaticResource KpiStyle}"
    ToolTip="{Binding GrindingCacheTooltip}"
    ToolTipService.InitialShowDelay="1000">
```

只修改研磨缓存这一张卡片；不修改 1 号线缓存、2 号线缓存、卡片高度、现有数量绑定或两件摘要绑定。

- [x] **Step 4: 重跑契约测试，确认 ViewModel 与 XAML 通过**

运行：

```powershell
dotnet test AutomaticOnlineHostComputer.Tests\AutomaticOnlineHostComputer.Tests.csproj --no-restore --filter FullyQualifiedName~GrindingCacheTooltipContractTests
```

预期：通过。

### Task 3: 构建与范围验证

**Files:**
- Modify: `Docs/superpowers/plans/2026-09-02-flow-status-grinding-cache-tooltip.md`（勾选实际执行结果）

- [x] **Step 1: 编译主项目**

运行：

```powershell
dotnet build AutomaticOnlineHostComputer.csproj --no-restore
```

预期：0 errors；如存在 HEAD 已有警告，单独记录且不归因于本功能。

- [x] **Step 2: 检查只读边界和改动范围**

运行：

```powershell
rg -n "GrindingCacheTooltip|FormatGrindingCacheTooltip|ToolTip=\"\{Binding GrindingCacheTooltip\}" Presentation\ViewModels\Home\HomeViewModel.cs Views\FlowStatus\FlowStatusView.xaml
git diff --check
git diff --stat
```

预期：Tooltip 仅读取 `GetCachedWorkpiecesSnapshot()`；没有 PLC 读写、FIFO 入队/出队或引擎状态改变；无空白错误。

- [ ] **Step 3: 手工界面检查**

启动程序并打开全流程状态页，依次检查：空缓存、仅直径、长度+直径、带版号/序号、多件缓存。确认鼠标位于研磨缓存卡片任意空白或文字区域时，都显示按 FIFO 顺序的全部工件；卡片内摘要仍至多两件。此步骤不向现场 PLC 写入任何信号。
