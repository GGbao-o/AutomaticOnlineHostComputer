# Boring Handshake Without Business Timeout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 取消双头镗R6101/R6103的120秒业务超时，同时保持通信硬超时、安全暂停和原握手顺序。

**Architecture:** 两条前端引擎使用每轮统一的双头镗状态快照驱动状态机和页面。新增等待R6103阶段，信号未到时保持阶段无限等待；只有实际读写异常才沿用现有暂停保护。

**Tech Stack:** C#、.NET 8、WPF、Modbus TCP

---

### Task 1: 改造两条前端双头镗状态机

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [x] 在枚举中增加等待R6103阶段。
- [x] 让R6101/R6103判断复用本轮`ReadAllSignalsAsync`快照。
- [x] 删除`WaitForBoringSignalAsync`及50ms后台无限读取入口。
- [x] 保留参数/R6102/R6104/R6108写入异常暂停。

### Task 2: 增强快照诊断

**Files:**
- Modify: `Service/FlowEngine/Line1FrontFlowEngine.cs`
- Modify: `Service/FlowEngine/Line2FrontFlowEngine.cs`

- [x] 快照读取失败时节流记录异常类型和消息。
- [x] 保持页面使用同一轮快照，不增加连接和额外高频读取。

### Task 3: 验证

- [x] 检索确认双头镗不再使用`HandshakeTimeoutMs`。
- [x] 检查1/2号线实现对称。
- [x] 运行`dotnet build AutomaticOnlineHostComputer.csproj`，结果0错误。
