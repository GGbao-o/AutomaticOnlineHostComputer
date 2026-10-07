# FANUC FOCAS 单线程会话实施计划

1. 修正 `FanucSdk.Connect` 的超时单位，并补充 `EW_HANDLE(-8)` 错误文本。
2. 将 `FanucSkewBedService` 的所有 SDK 调用统一投递到每台设备独立 Worker。
3. 保留原 public API、三次重试、连接重建和参数回读校验行为。
4. 为连接、重试和 Worker 生命周期增加设备、线程、句柄诊断信息及维护注释。
5. 更新 FANUC 协议文档，说明超时、线程归属、错误码和业务影响。
6. 检查完整差异并编译 `AutomaticOnlineHostComputer.csproj`。

