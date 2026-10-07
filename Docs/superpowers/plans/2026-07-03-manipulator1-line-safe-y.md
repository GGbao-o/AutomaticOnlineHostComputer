# 机械手1分线路安全Y Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将机械手1安全Y拆为1号线1000和2号线11500，并让前天车接受任一已确认安全点。

**Architecture:** 安全坐标和通用判定集中在 `MotionConfig.SkewBedSection`。线路引擎使用本线目标完成回位与货叉启动保护，前天车和主页面使用“任一安全点”判断。

**Tech Stack:** C# 12、.NET 8、WPF、System.Text.Json。

---

### Task 1: 配置模型与生产配置

**Files:**
- Modify: `Infrastructure/Config/MotionConfig.cs`
- Modify: `Config/motion_settings.json`

- [ ] 新增1/2号线安全Y属性、按线路取值方法和任一安全点判断方法。
- [ ] 配置生产值为1号线1000、2号线11500，并保留旧字段兼容。

### Task 2: 两线前端流程

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [ ] 机械手回位和货叉启动使用本线路安全Y。
- [ ] 前天车等待与页面设备快照使用任一安全点。
- [ ] 保持M801、M912/M914和双头镗状态机顺序不变。

### Task 3: 配置与主页面

**Files:**
- Modify: `Presentation/ViewModels/Config/ConfigPageViewModel.cs`
- Modify: `Views/Config/ConfigPageView.xaml`
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs`

- [ ] 配置页面新增“1线M1”和“2线M1”输入框。
- [ ] 主页面显示两个安全Y，并在任一安全点显示安全。

### Task 4: 文档与验证

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`
- Modify: `Docs/03-设备交互流程/整线引擎流程总览_按代码.md`
- Modify: `Docs/03-设备交互流程/货叉与双头镗交互流程.md`

- [ ] 记录分线路回位、货叉本线安全门和前天车双安全点规则。
- [ ] 执行 `dotnet build AutomaticOnlineHostComputer.csproj --no-restore`，预期0错误。
- [ ] 检查目标文件差异，确认地址、握手和任务派发未变化。

