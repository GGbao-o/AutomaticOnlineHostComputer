# 主页面单设备清空状态 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将斜床和研磨机清空状态操作拆成14个单设备按钮，并完整清除目标设备的上位机写入数据。

**Architecture:** 对话框只负责选定单一设备、确认和记录日志；真实地址与重试继续由已有设备服务封装。引擎应急状态、缓存和锁保持独立。

**Tech Stack:** C# 12、.NET 8、WPF、Modbus TCP、FANUC FOCAS。

---

### Task 1: 单设备按钮布局

**Files:**
- Modify: `Views/Home/Dialogs/ClearMachineStatusDialog.xaml`

- [x] 将10台斜床和4台研磨机改为独立按钮，站号写入Tag；1号线按现场物理顺序 ST108、ST109、ST111、ST110、ST112 显示。
- [ ] 删除成组清理按钮和一键清空全部按钮。

### Task 2: 单设备执行逻辑

**Files:**
- Modify: `Views/Home/Dialogs/ClearMachineStatusDialog.xaml.cs`
- Modify: `Views/Home/HomeView.xaml.cs`

- [ ] 增加通用单设备事件、确认弹窗及全窗口操作互斥。
- [ ] ST108~ST111调用Modbus斜床 `ClearEmergencyRegistersAsync`。
- [ ] ST112/ST606~ST610调用FANUC `ClearEmergencyRegistersAsync`。
- [ ] ST701~ST704调用研磨机 `ClearEmergencyRegistersAsync`。
- [ ] 保持双头镗两个独立按钮不变。

### Task 3: 文档与验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/02-通信协议手册/斜床协议/锦州斜床_ModbusTCP.md`
- Modify: `Docs/02-通信协议手册/斜床协议/沈阳斜床_FANUC_FOCAS.md`
- Modify: `Docs/02-通信协议手册/研磨机协议_ModbusTCP.md`

- [ ] 记录单设备清理范围、地址和与应急处理的边界。
- [ ] 执行 `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`，预期0错误。
- [ ] 检查差异，确认未修改引擎业务、地址常量和任务状态。
