# Rear Load Discard Emergency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a successful skew-bed emergency abandon both rear-load `PauseOnCarrier` and `PauseAtTargetPendingHandoff` snapshots for that exact line and skew bed.

**Architecture:** Keep the strict target-pending settlement for the rare “continue this workpiece” path. Extend only the existing skew emergency abandon path, after its old operation exit and device/software cleanup have succeeded, to remove both discardable rear-load snapshot dispositions for the selected target bed. Source-reservation snapshots remain untouched.

**Tech Stack:** C# / .NET 8, xUnit source-contract tests, WPF emergency dialog, existing `FlowActionManualRegistry`.

---

### Task 1: Lock the emergency scope in a failing contract test

**Files:**

- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/CraneExceptionSafetyContractTests.cs`

- [ ] Add assertions for `PauseOnCarrier`, `PauseAtTargetPendingHandoff`, and the phrase `来源冻结快照必须保留` in each rear engine source.
- [ ] Run `dotnet test AutomaticOnlineHostComputer.Tests/AutomaticOnlineHostComputer.Tests.csproj --filter FullyQualifiedName~CraneExceptionSafetyContractTests`; it must fail before the engine change because target-pending is not yet selected by skew emergency.

### Task 2: Implement exact emergency discard matching for both lines

**Files:**

- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs:1128-1143`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs:1090-1105`

- [ ] Rename `SettleManualLoadOnCarrierForEmergency` to `SettleManualRearLoadForEmergency`.
- [ ] Replace its predicate with `FlowScope == current rear-load scope`, `Target == bed.Code`, and disposition equal to either `PauseOnCarrier` or `PauseAtTargetPendingHandoff`.
- [ ] Keep `PauseSourceReserved` outside the predicate, with a comment that X11 was not confirmed and the source plate may remain in place.
- [ ] Count both dispositions independently, remove only the matching snapshots, and report both counts in the success log.
- [ ] Keep the helper call after all old-action exit, device-clear, cache-clear, and task-clear work has succeeded. Do not alter `ResolveManualLoadTargetPending`.

### Task 3: Prove the mirror code and application build

**Files:**

- Modify: `AutomaticOnlineHostComputer.Tests/FlowEngine/CraneExceptionSafetyContractTests.cs`
- Modify: `Service/FlowEngine/Line1RearFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] Run the focused contract test after implementation; it must pass.
- [ ] Run `dotnet build AutomaticOnlineHostComputer.csproj`; it must finish with 0 errors.

### Task 4: Update operator documentation and final review

**Files:**

- Modify: `Docs/异常处理与人工恢复操作手册.md`
- Modify: `Docs/03-设备交互流程/后天车与斜床交互流程.md`
- Modify: `Docs/superpowers/plans/2026-08-04-rear-load-discard-emergency.md`

- [ ] State that a successful skew emergency settles the exact-line/exact-bed rear-load `PauseOnCarrier` and `PauseAtTargetPendingHandoff` snapshots, but never a source-freeze snapshot.
- [ ] State that continuing a board uses strict target-pending settlement; abandoning an already manually removed board uses skew emergency and does not resend CNC/PLC notifications.
- [ ] Run diff whitespace review, stage only these feature files, and commit them.
