# 货叉及辅助 PLC 协议（三菱 MC 协议）

## 概述
- **PLC 型号**：三菱 FX3G + FX3U-ENET-L 扩展网口
- **协议**：三菱 MC 协议 3E 帧（二进制模式）
- **端口**：9000
- **代码**：`MitsubishiMcClient`（Communication/Clients/）

## 设备挂载关系
- **上料架 + 中转架**：共用 1 个三菱 PLC → 地址表【待补充】
- **下料架 + 动平衡架**：共用 1 个三菱 PLC → 地址表【待补充】
- **货叉**：独立三菱 PLC → ✅ 已实现

---

## 货叉信号（M 寄存器，bit 型）

### 输入信号（货叉 → 上位机，只读）

| M地址 | 信号名称 | 读取方式 |
|:---:|------|------|
| M900 | 货叉有版信号 | 读 M900 起始 1 字(16bit)，bit0 |
| M901 | 气缸1退回限位信号 | bit1 |
| M902 | 气缸1伸出限位信号 | bit2 |
| M903 | 气缸2退回限位信号 | bit3 |
| M904 | 气缸2伸出限位信号 | bit4 |
| M905 | 气缸3退回限位信号 | bit5 |
| M906 | 气缸3伸出在位信号 | bit6 |

### 输出信号（上位机 → 货叉，读-改-写）

| M地址 | 信号名称 | 写入方式 |
|:---:|------|------|
| M911 | 气缸1退回控制 | 读整字→改目标bit→写回 |
| M912 | 气缸1伸出控制 | 同上 |
| M913 | 气缸2退回控制 | 同上 |
| M914 | 气缸2伸出控制 | 同上 |
| M915 | 气缸3退回控制 | 同上 |
| M916 | 气缸3伸出控制 | 同上 |

### 地址定义文件

`Communication/DeviceAddresses/ForkAddress.cs`

### 服务封装

`Communication/DeviceServices/ForkService.cs`

```csharp
// 读取全部状态
var status = await forkSvc.ReadAllStatusAsync();
Console.WriteLine($"有版={status.HasPlate} 缸1={status.Cyl1State}");

// 控制气缸
await forkSvc.Cyl1ExtendAsync();  // 气缸1伸出
await forkSvc.Cyl1RetractAsync(); // 气缸1退回
```
