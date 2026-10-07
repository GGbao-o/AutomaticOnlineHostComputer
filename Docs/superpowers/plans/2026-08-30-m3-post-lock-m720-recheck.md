# M3 Post-Lock M720 Recheck Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 机械手3取得M821/M720两把位置锁后重新读取M720，目标已不可用时无动作退出，通信异常重试三次后安全暂停。

**Architecture:** 在`M3Flow`锁获取段后插入一个独立的一次性门禁方法。该方法只负责锁后M720读取、有限重试、重连和日志；持件后的既有无限等待方法保持独立不变，现有finally继续统一清理锁、Busy和页面状态。

**Tech Stack:** C#、.NET、xUnit源码合同测试、三菱MC 1E客户端。

---

### Task 1: 添加失败的源码合同测试

**Files:**
- Create: `AutomaticOnlineHostComputer.Tests/FlowEngine/BalancingM3PostLockM720ContractTests.cs`

- [x] **Step 1: 写入锁后调用顺序、三次重试、日志和持件后等待隔离测试**

```csharp
[Fact]
public void M3_rechecks_M720_after_both_locks_before_source_work()
{
    string source = ReadFlowSource();
    int flowStart = source.IndexOf("private async Task M3Flow", StringComparison.Ordinal);
    int flowEnd = source.IndexOf("private async Task ConfigureManipulatorAbsSpeedAsync", flowStart, StringComparison.Ordinal);
    string flow = source[flowStart..flowEnd];
    Assert.True(flow.IndexOf("MarkM3Lock(\"M720\", true)", StringComparison.Ordinal) <
                flow.IndexOf("ConfirmM720AfterM3LocksAsync", StringComparison.Ordinal));
    Assert.True(flow.IndexOf("ConfirmM720AfterM3LocksAsync", StringComparison.Ordinal) <
                flow.IndexOf("string pickReg", StringComparison.Ordinal));
}
```

- [x] **Step 2: 运行目标测试并确认因方法缺失失败**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~BalancingM3PostLockM720ContractTests`

Expected: FAIL，报告找不到`ConfirmM720AfterM3LocksAsync`、三次重试或对应日志合同。

### Task 2: 实现锁后复查和日志

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:1820-1845`
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs:2430-2490`

- [x] **Step 1: 在取得M720锁后调用一次性门禁**

```csharp
if (!await ConfirmM720AfterM3LocksAsync(ct))
{
    Console.WriteLine("[平衡引擎] [M3] 锁后复查M720=0，本趟尚未取料，正常结束并释放M821/M720锁及Busy");
    return;
}
```

- [x] **Step 2: 添加三次读取、500ms间隔、连接失效重连和最终暂停方法**

```csharp
private async Task<bool> ConfirmM720AfterM3LocksAsync(CancellationToken ct)
{
    const int maxAttempts = 3;
    const int retryDelayMs = 500;
    Exception? lastError = null;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            _mc65 = await _mcCache.GetOrCreateAsync("192.168.2.65", 9000, ct);
            var r = await ReadMc65MAlignedWordAsync(720, "M3锁后M720复查", ct);
            if (r.IntValues.Length < 1) throw new InvalidOperationException("MC65返回字数不足");
            bool canPlace = (r.IntValues[0] & 1) != 0;
            return canPlace;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lastError = ex;
            await _mcCache.InvalidateAsync("192.168.2.65", 9000);
            _mc65 = null;
            if (attempt < maxAttempts) await Task.Delay(retryDelayMs, ct);
        }
    }
    _paused = true;
    string message = $"机械手3取得位置锁后连续{maxAttempts}次读取M720失败，目标状态未知，引擎已暂停。最后异常：{lastError?.Message}";
    OnSafetyAlarm?.Invoke(message);
    throw new InvalidOperationException(message, lastError);
}
```

实现时补齐`OperationalLog`和控制台日志字段，但不改变返回值和异常边界。

- [x] **Step 3: 运行目标测试确认通过**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~BalancingM3PostLockM720ContractTests`

Expected: PASS，0 failed。

### Task 3: 更新业务文档并全面验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md:715-745`
- Modify: `findings.md`
- Modify: `progress.md`

- [x] **Step 1: 把11.5锁后复查规则写入业务文档**

文档明确区分“尚未取料时M720=0正常放弃”和“已经持件时M720=0持锁等待”，并写明三次、500ms和最终暂停。

- [x] **Step 2: 运行M3目标合同测试和相邻安全合同测试**

Run: `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter "FullyQualifiedName~BalancingM3PostLockM720ContractTests|FullyQualifiedName~M2RearDispatchSafetyContractTests|FullyQualifiedName~BalancingEarlyLockReleaseContractTests"`

Expected: PASS，0 failed。

Actual: 新增M3合同3/3通过；相邻筛选合计10项中7项通过、3项为HEAD基线已存在的陈旧断言失败，未修改无关现场配置和旧合同。

- [x] **Step 3: 编译主项目**

Run: `dotnet build AutomaticOnlineHostComputer.csproj`

Expected: Build succeeded，0 errors。

- [x] **Step 4: 检查最终差异只包含本需求相关文件**

Run: `git diff -- Service/FlowEngine/BalancingFlowEngine.cs AutomaticOnlineHostComputer.Tests/FlowEngine/BalancingM3PostLockM720ContractTests.cs Docs/全线业务逻辑详解.md Docs/superpowers/specs/2026-08-30-m3-post-lock-m720-recheck-design.md Docs/superpowers/plans/2026-08-30-m3-post-lock-m720-recheck.md`

Expected: 只有锁后M720复查、日志、测试和文档变化；不改变持件后的M720等待语义。
