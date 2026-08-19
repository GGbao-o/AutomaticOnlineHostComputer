# 西门子研磨机联机模式 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** ST703、ST704 仅在 `40001-3=1` 联机时可自动接收新的研磨上料任务，并在全流程状态显示模式。

**Architecture:** 扩展 TypeA 状态快照与 `GrinderContext` 的最后有效扫描状态；`FindReadyGrinder` 增加 TypeA 联机准入，既有取料前 TypeA 二次读取作竞态复核。页面直接消费同一快照，不额外建立 PLC 连接。

**Tech Stack:** .NET 8、C#、WPF、Modbus TCP、xUnit。

---

### Task 1: TypeA 联机位与调度准入

**Files:**
- Modify: `Communication/DeviceAddresses/PlcGrinderAddress.cs`
- Modify: `Communication/DeviceServices/PlcGrinderService.cs`
- Modify: `Service/FlowEngine/GrindingFlowEngine.cs`
- Test: `AutomaticOnlineHostComputer.Tests/OperationalEvents/SiemensGrinderOnlineModeContractTests.cs`

- [x] 定义 `40001-3` 联机 bit，快照解析 `OnlineMode`，并写入/清理研磨机的最新联机状态。
- [x] 让 `FindReadyGrinder` 仅对 TypeA 追加联机准入；取料前二次确认切单机时回 FIFO、不暂停引擎。

### Task 2: 页面、日志和文档

**Files:**
- Modify: `Presentation/ViewModels/Home/GrinderCardViewModel.cs`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md`
- Modify: `Docs/全线业务逻辑详解.md`

- [x] 让设备总览和全流程状态的 TypeA 卡显示联机/单机与原始联机位。
- [x] 记录单机导致的单机禁派发语义，更新通信点位和上料前置条件。

### Task 3: 验证

- [x] 先运行新增契约测试确认缺少实现时失败，再实现并通过。
- [x] 运行目标测试、应用构建和 Git 差异检查。
