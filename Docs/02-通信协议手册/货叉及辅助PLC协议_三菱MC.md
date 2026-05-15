# 货叉及辅助 PLC 协议（三菱 MC 协议）

## 概述
- **PLC 型号**：三菱 FX3G + FX3U-ENET-L 扩展网口
- **协议**：三菱 MC 协议 3E 帧（二进制模式）
- **端口**：9000
- **代码**：`MitsubishiMcClient`（Communication/Clients/）、`ForkService`（Communication/DeviceServices/）

## 设备挂载关系
- **上料架 + 中转架**：共用 1 个三菱 PLC → 地址表【待补充】
- **下料架 + 动平衡架**：共用 1 个三菱 PLC → 地址表【待补充】
- **货叉**：独立三菱 PLC → ✅ 已实现

---

## 货叉信号（M 寄存器，bit 型）

货叉有 4 个工位：待机位 / 1号位 / 2号位 / 3号位。
读 M900 起始 1 字（16bit）可覆盖全部信号。

### 输入信号（货叉 → 中控，只读）

| M地址 | 常量名 | 信号名称 | 位偏移 |
|:---:|------|------|:---:|
| M900 | `M_HasPlate` | 货叉有版信号 | bit0 |
| M901 | `M_AtStandbyPos` | 货叉在待机位状态 | bit1 |
| M902 | `M_AtPos1` | 货叉在1号位状态 | bit2 |
| M903 | `M_AtPos2` | 货叉在2号位状态 | bit3 |
| M904 | `M_AtPos3` | 货叉在3号位状态 | bit4 |

### 输出信号（中控 → 货叉，读-改-写）

| M地址 | 常量名 | 信号名称 | 位偏移 |
|:---:|------|------|:---:|
| M911 | `M_GoStandby` | 货叉回待机位控制 | bit11 |
| M912 | `M_GoPos1` | 货叉去1号位置控制 | bit12 |
| M913 | `M_GoPos2` | 货叉去2号位置控制 | bit13 |
| M914 | `M_GoPos3` | 货叉去3号位置控制 | bit14 |

### 地址定义文件

`Communication/DeviceAddresses/ForkAddress.cs`

### 服务封装

`Communication/DeviceServices/ForkService.cs`

```csharp
// 读取全部状态
var status = await forkSvc.ReadAllStatusAsync();
Console.WriteLine($"有版={status.HasPlate} 当前位置={status.CurrentPosition}");

// 控制货叉移动
await forkSvc.GoStandbyAsync();  // 回待机位
await forkSvc.GoPos1Async();     // 去1号位
await forkSvc.GoPos2Async();     // 去2号位
await forkSvc.GoPos3Async();     // 去3号位
```
