# Line2 Drop Rack Handshake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Update only line 2 ST020/ST021 so PLC bits represent place/pick handshakes instead of plate-present signals.

**Architecture:** Keep existing software cache keys (`M818`, `M821`) and region locks (`_lockM818`, `_lockM821`) for compatibility. Treat PLC bits as action permissions/completions: rear crane uses M818/M820 plus M819/M821, M2/M3 use M823/M825 plus M824/M826, and software cache remains the source of workpiece identity.

**Tech Stack:** C# .NET 8 WPF, Mitsubishi MC A-1E client, existing WPF view models and Markdown docs.

---

### Task 1: Balance Engine Handshake Snapshot

**Files:**
- Modify: `Service/FlowEngine/BalancingFlowEngine.cs`

- [ ] **Step 1: Replace line 2 plate-present variables with explicit handshake variables**

Add fields for M823/M825 pick permission and M824/M826 pick completion while keeping public compatibility properties for UI or emergency code that still names `M818`/`M821`.

- [ ] **Step 2: Read MC63 M816 word once**

Keep M817 as 1号线 plate-present. Decode M818/M819/M820/M821/M823/M824/M825/M826 from the M816 word. Do not read non-aligned single M bits.

- [ ] **Step 3: Change M2 trigger**

M2 should trigger on `M817` or `(M823CanPick && TryGetBalWp("M818"))`. M818 PLC bit is no longer a plate-present trigger.

- [ ] **Step 4: Change M3 trigger**

M3 should trigger on `M700` or `(M825CanPick && TryGetBalWp("M821"))`. M821 PLC bit is no longer a plate-present trigger.

- [ ] **Step 5: Write pick completion bits**

After M2 successfully picks ST020 and returns Z safe, write M824. After M3 successfully picks ST021 and returns Z safe, write M826.

### Task 2: Rear Crane Completion Review

**Files:**
- Review/modify: `Service/FlowEngine/Line2RearFlowEngine.cs`

- [ ] **Step 1: Confirm rear crane precheck**

Long/forced balancing checks M818 allow-place. Short checks M820 allow-place. Both keep `_lockM818 + _lockM821`.

- [ ] **Step 2: Confirm rear crane completion writes**

ST020 writes M819 after Z returns safe. ST021 writes M821 after Z returns safe.

### Task 3: Home Page Status

**Files:**
- Modify: `Presentation/ViewModels/Home/HomeViewModel.cs` and related status-card bindings if needed.

- [ ] **Step 1: Replace ST020/ST021 display text**

Show M818/M819/M823/M824 for ST020 and M820/M821/M825/M826 for ST021, plus software cache presence.

- [ ] **Step 2: Remove old "M818/M821 has plate" wording**

Only line 2 ST020/ST021 wording changes. Leave 1号线 M817 and MC65 M700/M710/M720 wording intact.

### Task 4: Documentation

**Files:**
- Modify: `Docs/全线业务逻辑详解.md`

- [ ] **Step 1: Update destination checks**

Document M818/M820 as rear-crane allow-place signals.

- [ ] **Step 2: Update M2/M3 sections**

Document M823/M825 as pick permissions and M824/M826 as pick completions. State that software cache is required before taking line 2 ST020/ST021 material.

- [ ] **Step 3: Update signal table**

List M818/M819/M820/M821/M823/M824/M825/M826 bit offsets in the M816 word.

### Task 5: Verification

**Files:**
- Build project root.

- [ ] **Step 1: Build**

Run `dotnet build AutomaticOnlineHostComputer.csproj` and fix compile errors caused by the rename.

- [ ] **Step 2: Search old semantics**

Run `rg` for line 2 old wording around M818/M821 and confirm remaining references are either compatibility cache names or docs explicitly describing legacy names.
