# 页面职责与排障导航手册 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `Docs` 创建一份面向上位机工程师的 SCADA+轻量 MES 页面职责、数据边界与排障导航手册。

**Architecture:** 手册以“故障现象 → 首选页面 → 深入页面/文档/源码”为主导航，再以八个固定页面章节解释职责、数据源、操作边界和追溯位置。所有结论只基于现有代码和正式 Docs，不新增业务规则。

**Tech Stack:** Markdown、WPF XAML/C# 源码、项目 `Docs` 正式业务文档。

---

### Task 1: 固化页面映射和证据范围

**Files:**
- Read: `MainWindow.xaml`
- Read: `Infrastructure/Navigation/CachedNavigationService.cs`
- Read: `Docs/README.md`

- [x] **Step 1: 核对八个未注释菜单与页面工厂映射**

Run: `rg -n 'Tag=' MainWindow.xaml` 和 `rg -n '=> new' Infrastructure/Navigation/CachedNavigationService.cs`

Expected: 主界面、机器管理、运动参数设置、设备总览、全流程状态、运行诊断、在制工件、异常监控各有一个 Tag 与一个对应 View。

- [x] **Step 2: 固化正式文档优先级**

阅读 `Docs/README.md`，将业务主文档、设备交互流程、日志定位手册作为正文引用来源；历史 `Docs/superpowers/plans` 仅作变更追溯，不作为业务真值。

### Task 2: 撰写正式页面手册

**Files:**
- Create: `Docs/SCADA+轻量MES_页面职责与排障导航手册.md`
- Read: `Views/Home/HomeView.xaml.cs`
- Read: `Views/Machine/MachineManagementView.xaml.cs`
- Read: `Views/Motion/MotionSettingsView.xaml`
- Read: `Views/Overview/EquipmentOverviewView.xaml`
- Read: `Views/FlowStatus/FlowStatusView.xaml.cs`
- Read: `Views/RuntimeDiagnostics/RuntimeDiagnosticsView.xaml.cs`
- Read: `Views/InProcessWorkpieces/InProcessWorkpieceOverviewView.xaml.cs`
- Read: `Views/AttentionMonitor/AttentionMonitorView.xaml.cs`

- [x] **Step 1: 写系统边界与排障总表**

明确 SCADA 页面、MES 任务行和流程引擎之间的职责；覆盖“任务不派发、设备/工位状态异常、工件位置不明、通信或快照过期、红黄报警或应急、需要手动干预”六类入口。

- [x] **Step 2: 写八个页面的标准化章节**

每章包含：页面定位、负责模块、关键显示内容、数据来源与刷新、允许操作、禁止误解、遇到什么问题先看本页、下一步追溯文件。对控制页注明人工确认和停线要求，对只读页注明不会写设备或改变流程。

- [x] **Step 3: 写工程师追溯索引**

将每页关联到具体 View、ViewModel、服务/流程引擎，并链接 `Docs/整线技术手册.md`、`Docs/全线业务逻辑详解.md`、`Docs/日志审查与故障定位手册.md`、`Docs/03-设备交互流程/整线引擎流程总览_按代码.md`。

### Task 3: 静态校验文档

**Files:**
- Verify: `Docs/SCADA+轻量MES_页面职责与排障导航手册.md`

- [x] **Step 1: 检查八个页面是否均有独立章节**

Run: `rg -n '^## ' 'Docs/SCADA+轻量MES_页面职责与排障导航手册.md'`

Expected: 八个页面名称均出现，且另有系统边界、排障导航和追溯索引章节。

- [x] **Step 2: 检查无未完成占位符且链接文件存在**

Run: `rg -n 'TODO|TBD|待补充' 'Docs/SCADA+轻量MES_页面职责与排障导航手册.md'`

Expected: 无匹配。手工核对每个项目内相对链接的目标文件存在。

- [x] **Step 3: 检查本次变更没有生产代码**

Run: `git diff --check -- Docs task_plan.md findings.md progress.md`

Expected: 无空白错误；本次新增和修改仅为 Markdown 文档及计划记录。
