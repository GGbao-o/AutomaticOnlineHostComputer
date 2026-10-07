namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class St010PlacementM731SuppressionContractTests
{
    [Fact]
    public void Placement_flags_are_two_independent_atomic_memory_markers()
    {
        string source = Read("Service", "FlowEngine", "St010PlacementFlags.cs");

        Assert.Contains("class St010PlacementFlags", source, StringComparison.Ordinal);
        Assert.Contains("BeginM3Placement", source, StringComparison.Ordinal);
        Assert.Contains("EndM3Placement", source, StringComparison.Ordinal);
        Assert.Contains("BeginCrane2Placement", source, StringComparison.Ordinal);
        Assert.Contains("EndCrane2Placement", source, StringComparison.Ordinal);
        Assert.Contains("Snapshot", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SemaphoreSlim", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMBit", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_creates_one_shared_flag_instance_for_the_three_relevant_engines()
    {
        string source = Read("Presentation", "ViewModels", "Home", "HomeViewModel.cs");

        Assert.Contains("var st010PlacementFlags = new St010PlacementFlags();", source, StringComparison.Ordinal);
        Assert.Equal(5, Count(source, "st010PlacementFlags"));
    }

    [Fact]
    public void M3_marks_before_final_st010_y_move_clears_after_safe_z_and_finally_clears()
    {
        string method = ExtractMethod(Read("Service", "FlowEngine", "BalancingFlowEngine.cs"), "private async Task M3Flow");

        int m720Confirmed = Index(method, "await ConfirmM720EmptyBeforePlaceAsync(ct);");
        int begin = Index(method, "BeginM3Placement()");
        int move = Index(method, "await _m3.MoveAbsoluteAsync(-1, destY, -1, ct: ct);");
        int safeZ = Index(method, "await _m3.MoveAbsoluteAsync(-1, -1, safeZ, ct: ct);");
        int normalClear = Index(method, "ST010放板标志=0（放料后Z安全到位）");
        int m721 = Index(method, "M721=1 通知PLC放料完成");

        Assert.True(m720Confirmed < begin && begin < move,
            "M3必须在最终M720确认成功后、Y去ST010前置位。 ");
        Assert.True(safeZ < normalClear && normalClear < m721,
            "M3在ST010退磁后Z安全到位即清标志，不等待M721或研磨FIFO。");
        Assert.True(LastIndex(method, "finally", "EndM3Placement()"),
            "M3的finally必须兜底清除ST010放板标志。");
    }

    [Fact]
    public void Crane2_marks_only_short_st010_branch_before_xy_and_finally_clears()
    {
        string method = ExtractMethod(Read("Service", "FlowEngine", "Line1RearFlowEngine.cs"), "private async Task DoUnload");

        int m720Confirmed = Index(method, "await WaitForM720CanPlaceBeforePlaceAsync(wp, ct);");
        int begin = Index(method, "BeginCrane2Placement()");
        int move = Index(method, "await cr.MoveAbsoluteAsync(gx + _ox, gy + _oy, -1, ct: ct);");

        Assert.True(m720Confirmed < begin && begin < move,
            "2号天车必须在短板M720确认成功后、XY去ST010前置位。");
        Assert.Equal(1, Count(method, "BeginCrane2Placement()"));
        Assert.True(LastIndex(method, "finally", "EndCrane2Placement()"),
            "2号天车的finally必须兜底清除ST010放板标志。");
    }

    [Fact]
    public void Grinding_crane_skips_m731_only_when_a_st010_placement_flag_is_active()
    {
        string method = ExtractMethod(Read("Service", "FlowEngine", "GrindingFlowEngine.cs"), "private async Task ProcessWorkpieceAsync");
        int delay = Index(method, "await Task.Delay(_cfg.Grinding.M731NotifyDelayMs, ct);");
        int snapshot = Index(method, "var st010Placement = _st010PlacementFlags.Snapshot();");
        int write = Index(method, "Task m731Task = _mc65.WriteMBitInWordAsync(720, 11, true, ct);");
        string skipBranch = ExtractIfBody(method, "if (st010Placement.AnyPlacing)");

        Assert.True(delay < snapshot && snapshot < write,
            "M731延时结束后必须先读取ST010放板标志，再决定是否写M731。");
        Assert.Contains("await zSafeTask;", skipBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMBitInWordAsync", skipBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Placement_flag_logic_does_not_use_m720_locks_or_coordinates()
    {
        string flags = Read("Service", "FlowEngine", "St010PlacementFlags.cs");
        string grinding = ExtractMethod(Read("Service", "FlowEngine", "GrindingFlowEngine.cs"), "private async Task ProcessWorkpieceAsync");

        Assert.DoesNotContain("CurrentCount", flags, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitAsync", flags, StringComparison.Ordinal);
        Assert.DoesNotContain("Release", flags, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCount", grinding, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));

    private static int Index(string source, string text)
    {
        int index = source.IndexOf(text, StringComparison.Ordinal);
        Assert.True(index >= 0, $"未找到预期文本：{text}");
        return index;
    }

    private static int Count(string source, string text)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(text, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += text.Length;
        }

        return count;
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = Index(source, signature);
        int openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"未找到 {signature} 的方法体。");

        return ExtractBalancedBody(source, openingBrace, start);
    }

    private static bool LastIndex(string source, string earlier, string later)
    {
        int laterIndex = source.LastIndexOf(later, StringComparison.Ordinal);
        if (laterIndex < 0) return false;
        int earlierIndex = source.LastIndexOf(earlier, laterIndex, StringComparison.Ordinal);
        return earlierIndex >= 0 && earlierIndex < laterIndex;
    }

    private static string ExtractIfBody(string source, string condition)
    {
        int conditionIndex = Index(source, condition);
        int openingBrace = source.IndexOf('{', conditionIndex);
        return ExtractBalancedBody(source, openingBrace, conditionIndex);
    }

    private static string ExtractBalancedBody(string source, int openingBrace, int start)
    {
        Assert.True(openingBrace >= 0, "未找到代码块起始大括号。");
        int depth = 0;
        for (int i = openingBrace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException("代码块大括号未闭合。");
    }
}
