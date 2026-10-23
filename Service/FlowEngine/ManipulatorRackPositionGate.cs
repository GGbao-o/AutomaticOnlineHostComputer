namespace AutomaticOnlineHostComputer.Service;

/// <summary>后天车分流区的单机械手位置准入；只比较当次实际Y和D5007，不执行设备动作。</summary>
internal static class ManipulatorRackPositionGate
{
    /// <summary>
    /// 沿用原配置的严格Y小于阈值，同时要求Y回原点完成Word恰好为1。
    /// 读取失败/未知不能当作Y=0或已经回原点；D5007为2、-1等非0值也不放行。
    /// 调用方必须对机械手2、3分别判断并同时通过，未通过时继续原持锁等待。
    /// </summary>
    internal static bool IsClear(int? actualY, short? yHomeCompleted, int belowY)
        => actualY is int y && y < belowY && yHomeCompleted == 1;
}
