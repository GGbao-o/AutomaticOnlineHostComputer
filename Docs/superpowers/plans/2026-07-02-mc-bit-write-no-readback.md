# MC Completion Bit Write Without Readback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成通知位只执行一次带共享锁的整字读改写；真实连接、读取或写入失败时重连并最多进行三次完整尝试。

**Architecture:** 修改 `MitsubishiMcClient.WriteMBitInWordAsync` 的底层统一语义，保持所有调用点不变。进程级M字锁覆盖完整重试过程，每次失败后重新连接并重新读取最新整字，不再进行写后目标bit回读。

**Tech Stack:** C# 12、.NET 8、三菱 MC 1E/3E TCP 客户端、WPF。

---

### Task 1: 修改 MC 位写底层

**Files:**
- Modify: `Communication/Clients/MitsubishiMcClient.cs:149-203`

- [x] **Step 1:** 保留参数校验、地址对齐、共享M字锁和写前整字读取。
- [x] **Step 2:** 删除写后回读、目标bit比较、20ms间隔和回读不符重试。
- [x] **Step 3:** 将最多三次尝试改为仅捕获真实通信/协议失败；调用方取消立即向上抛。
- [x] **Step 4:** 每次失败后记录失败阶段，等待500ms，调用同一客户端 `ConnectAsync` 重连；下一次必须重新读取最新整字。
- [x] **Step 5:** 三次失败抛出包含PLC、M字、目标bit、期望值、阶段、次数和最后异常的 `IOException`。

### Task 2: 更新通信和业务文档

**Files:**
- Modify: `Docs/02-通信协议手册/货叉及辅助PLC协议_三菱MC.md:315-333`
- Modify: `Docs/全线业务逻辑详解.md:716-725`

- [x] **Step 1:** 将锁内顺序改为“连接确认/重连 → 读取整字 → 合并目标bit → 写回整字 → 校验PLC协议应答”。
- [x] **Step 2:** 明确不回读完成通知位、不因PLC快速清零重复发送。
- [x] **Step 3:** 明确真实通信失败最多三次完整尝试，且每次重新读取最新整字。

### Task 3: 验证

**Files:**
- Verify: all `WriteMBitInWordAsync` call sites

- [x] **Step 1:** 搜索全部调用点，确认地址、bit和调用签名未变化。
- [x] **Step 2:** 搜索旧的“回读校验/20ms/实际值”文本，确认只删除目标范围内旧语义。
- [x] **Step 3:** 执行 `git diff --check`，预期无新增空白错误。
- [x] **Step 4:** 执行 `dotnet build AutomaticOnlineHostComputer.csproj`，预期0错误。
- [x] **Step 5:** 检查完整差异，确认未修改业务条件、动作顺序或调用点。
