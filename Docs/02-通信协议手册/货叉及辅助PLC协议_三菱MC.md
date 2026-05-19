# 货叉及辅助 PLC 协议（三菱 MC 协议）

## 概述
- **PLC 型号**：三菱 FX3G + FX3U-ENET-L 扩展网口
- **协议**：三菱 MC 协议 3E 帧（二进制模式）
- **端口**：9000
- **代码**：`MitsubishiMcClient`（Communication/Clients/）、`ForkService`（Communication/DeviceServices/）

## 设备挂载关系
- **货叉 ×2**：独立三菱 PLC → ✅ 已实现
  - 一号线货叉 ST711：192.168.2.88:9000
  - 二号线货叉 ST712：192.168.2.89:9000
- **总上料机架 + 中转架 (ST007, 2.63)**：192.168.2.63:9000
  - M800~M816 信号 + D100 板长（总上料架请求取料 / 中转架有版无版 / 取料完成）
- **动平衡料架 + 研磨机上料架 (ST709, 2.65)**：192.168.2.65:9000
  - M817~M824 信号 + D100 板长（动平衡架状态 / 研磨机上料架各位状态）
- **研磨机下料架 ST710**：192.168.2.64:9000
  - M824 研磨机上料架(第一个)信号
- **货叉**：三菱 FX3G，4工位：待机位/1号位/2号位/3号位
  - 气缸1：有料时推着往前送一点
  - 气缸2：跟天车交互时往上送，方便天车够到
  - 气缸3：直接送双头镗，往前送一大段

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

---

## 研磨机上料架 ST709 / 下料架 ST710 信号（M 寄存器 + D 寄存器）

> 上下料架共用同一套信号表，不同 IP。PLC 型号同货叉：三菱 FX3G + FX3U-ENET-L。
> - ST709（上料架）：192.168.2.65:9000
> - ST710（下料架）：192.168.2.64:9000

### 输入信号（上料架 → 中控，只读）

| M/D地址 | 常量名 | 信号名称 | 位偏移 |
|:---:|------|------|:---:|
| M800 | `M_RequestPickup` | 上料架请求取料信号 | 第1字 bit0 |
| D100 | `D_PlateLength` | 居中架测量板长数值（16位无符号，mm） | D 区字 |
| M811 | `M_Station1HasPlate` | 等待工位1有版信号 | 第1字 bit11 |
| M812 | `M_Station2HasPlate` | 等待工位2有版信号 | 第1字 bit12 |
| M813 | `M_Station3HasPlate` | 等待工位3有版信号 | 第1字 bit13 |
| M814 | `M_Station4HasPlate` | 等待工位4有版信号 | 第1字 bit14 |
| M815 | `M_Station5HasPlate` | 等待工位5有版信号 | 第1字 bit15 |
| M816 | `M_Station6HasPlate` | 等待工位6有版信号 | 第2字 bit0 |

> ⚠️ M816 在 bit16，需读 M800 起始 **2 字（32bit）**覆盖 M800~M831。
> D100 单独用 D 区（0xA8）读字。

### 输出信号（中控 → 上料架，读-改-写）

| M地址 | 常量名 | 信号名称 | 位偏移 |
|:---:|------|------|:---:|
| M801 | `M_PickupDone` | 天车取料完成信号 | bit1 |

> 写入方式：读 M800 起始 1 字 → 置 M801=1 → 写回 → 等 500ms → 写回原值复位。

### 地址定义文件

`Communication/DeviceAddresses/CenteringRackAddress.cs`

### 服务封装

`Communication/DeviceServices/CenteringRackService.cs`

```csharp
// 读取全部状态（M800~M816 位信号 + D100 板长）
var status = await rackSvc.ReadAllStatusAsync();
Console.WriteLine($"请求取料={status.RequestPickup} 板长={status.PlateLength}mm 有版工位=[{status.ActiveStations}]");

// 等待上料架请求取料
await rackSvc.WaitForRequestPickupAsync(timeoutMs: 60_000);

// 天车取料完成，通知上料架复位
await rackSvc.SetPickupDoneAsync();

// 单独读板长
int length = await rackSvc.ReadPlateLengthAsync();
```

---

## 总上料架 + 中转架 信号（192.168.2.63:9000）

> ⚠️ 三菱 MC 协议，端口 9000。ST007 总上料机架集成三个中转架状态。

### 输入信号（PLC → 上位机，只读）

| M/D地址 | 信号名称 | 说明 |
|:---:|------|------|
| M800 | 总上料架请求取料 | 机械手1来这里抓取放到货叉 |
| D100 | 板长数值 | 16位无符号，mm |
| M811 | 1号线中转架1 | 有版=1 / 没版=0 |
| M812 | 1号线中转架2 | 有版=1 / 没版=0 |
| M813 | 1号线中转架3 | 有版=1 / 没版=0 |
| M814 | 2号线中转架1 | 有版=1 / 没版=0 |
| M815 | 2号线中转架2 | 有版=1 / 没版=0 |
| M816 | 2号线中转架3 | 有版=1 / 没版=0 |

### 输出信号（上位机 → PLC，读写改）

| M地址 | 信号名称 | 写入方式 |
|:---:|------|------|
| M801 | 取料完成 | 写1→等500ms→写回原值复位 |

---

## 动平衡料架 + 研磨机上料架 信号（192.168.2.65:9000）

> ⚠️ 与上表同 PLC 型号（三菱 FX3G），但不同 IP。覆盖 M817~M824 及 D100。

### 输入信号（PLC → 上位机，只读）

| M/D地址 | 信号名称 | 说明 |
|:---:|------|------|
| D100 | 板长数值 | 16位无符号，mm |
| M817 | 1号线动平衡上料架1 | 1号线需动平衡的放这里 |
| M818 | 2号线动平衡上料架1 | 2号线需动平衡的放这里 |
| M819 | 动平衡上料架1 | 工人搬走后放到此位置 |
| M820 | 动平衡上料架2 | 机械手2从这里取料 |
| M821 | 2号线动平衡上料架2 | 2号线不需动平衡放这里 |
| M822 | 研磨机上料架1号位 | 1号线不需动平衡的放这里(坐标待定义) |
| M823 | 研磨机上料架6号位 | 研磨天车抓取起始位(890,250,1507) |

---

## 动平衡料架 1/2 独立 PLC 段（2.64 / 2.63）

| IP | M地址 | 信号名称 |
|:---:|:---:|------|
| 192.168.2.64 | M824 | 研磨机上料架(第一个) |
| 192.168.2.63 | *(同总上料架 M800~M816)* | ST009 动平衡料架2 复用了总上料架 IP
