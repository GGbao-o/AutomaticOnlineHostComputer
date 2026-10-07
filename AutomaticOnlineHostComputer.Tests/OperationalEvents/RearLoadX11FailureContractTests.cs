namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class RearLoadX11FailureContractTests
{
    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "1号线")]
    [InlineData("Line2RearFlowEngine.cs", "2号线")]
    public void Three_zero_x11_load_failure_recovers_releases_locks_then_pauses_line(
        string fileName,
        string lineName)
    {
        string source = ReadFlowSource(fileName);
        string method = ExtractMethod(source, "private async Task DoLoad");

        Assert.Contains("public bool X11PickupFailed;", source, StringComparison.Ordinal);
        Assert.Contains("public string X11RecoverySummary", source, StringComparison.Ordinal);
        Assert.Contains("OnRearCraneX11FinalSafetyAlarm", source, StringComparison.Ordinal);

        int markFailed = method.IndexOf("loadState.X11PickupFailed = true;", StringComparison.Ordinal);
        int throwFailed = method.IndexOf("取料失败: 3次充磁后X11仍=0", StringComparison.Ordinal);
        Assert.True(markFailed >= 0, $"{fileName} 未在第三次X11=0时标记失败状态");
        Assert.True(throwFailed > markFailed, $"{fileName} 必须先标记X11失败再抛异常");

        int finalReport = method.IndexOf("TryReportRearLoadX11FinalFailure", StringComparison.Ordinal);
        Assert.True(finalReport > throwFailed, $"{fileName} 最终结构化事件必须在原异常进入收尾后才上报");
        Assert.Equal(1, CountOccurrences(method, "TryReportRearLoadX11FinalFailure"));
        Assert.DoesNotContain(
            "TryReportPhysical(_exceptionReporter, \"CRANE_X11_NOT_CONFIRMED\"",
            method[..throwFailed],
            StringComparison.Ordinal);

        Assert.Contains(
            "await cr.MoveAbsoluteAsync(-1, -1, 0, ct: CancellationToken.None);",
            method,
            StringComparison.Ordinal);
        Assert.Contains("退磁成功", method, StringComparison.Ordinal);
        Assert.Contains("退磁失败", method, StringComparison.Ordinal);
        Assert.Contains("Z已回原点", method, StringComparison.Ordinal);
        Assert.Contains("Z回原点失败", method, StringComparison.Ordinal);

        int rearLockRelease = method.IndexOf(
            "operation.TryRelease(\"RearCrane\", _craneRearLock)",
            StringComparison.Ordinal);
        int alarmText = method.IndexOf("string x11Alarm", StringComparison.Ordinal);
        int pauseCondition = method.LastIndexOf(
            "if (loadState.X11PickupFailed)",
            alarmText,
            StringComparison.Ordinal);
        int pauseAssignment = method.IndexOf("_paused = true;", pauseCondition, StringComparison.Ordinal);
        int alarmCallback = method.IndexOf("safetyAlarmCallback?.Invoke", StringComparison.Ordinal);

        Assert.True(rearLockRelease >= 0, $"{fileName} 未找到后天车锁释放");
        Assert.True(alarmText > rearLockRelease, $"{fileName} X11最终告警必须在后天车锁释放后生成");
        Assert.True(pauseCondition > rearLockRelease, $"{fileName} 必须释放后天车锁后再生成X11暂停告警");
        Assert.True(pauseAssignment > pauseCondition, $"{fileName} X11失败条件必须设置引擎暂停");
        Assert.True(alarmCallback > pauseAssignment, $"{fileName} 必须在本引擎暂停后再暂停对应整线");
        Assert.True(finalReport > alarmCallback, $"{fileName} 必须在安全告警回调完成后记录最终结构化事件");

        string pauseBlock = method[pauseCondition..alarmCallback];
        Assert.Contains($"{lineName}后天车从中转架", pauseBlock, StringComparison.Ordinal);
        Assert.Contains("X11连续三次为0", pauseBlock, StringComparison.Ordinal);
        Assert.Contains("X11RecoverySummary", pauseBlock, StringComparison.Ordinal);
        Assert.Contains("pendingSafetyAlarm == null", pauseBlock, StringComparison.Ordinal);
        Assert.Contains("X11Finalization", method, StringComparison.Ordinal);
        Assert.Contains("ZoneTS", method[rearLockRelease..finalReport], StringComparison.Ordinal);
        Assert.Contains("ZoneMT", method[rearLockRelease..finalReport], StringComparison.Ordinal);
        Assert.Contains("RearCrane", method[rearLockRelease..finalReport], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Rear_load_cleanup_never_de_magnetizes_once_pickup_may_have_happened(string fileName)
    {
        string source = ReadFlowSource(fileName);
        string method = ExtractMethod(source, "private async Task DoLoad");

        // “物理命令已开始且响应未知”分支必须置 requiresManualConfirmation，阻止后续退磁收尾。
        Assert.Contains("physicalCommandStarted && !loadState.X11PickupFailed", method, StringComparison.Ordinal);

        // 退磁收尾（if (mag && cr != null)）前必须是带 !requiresManualConfirmation 守卫的 else，
        // 不能是裸 else，否则“持件等待/响应未知”会被当成“未取到板”而放磁。
        int magOff = method.IndexOf("if (mag && cr != null)", StringComparison.Ordinal);
        Assert.True(magOff > 0, $"{fileName} 未找到退磁收尾分支");
        string guard = method[Math.Max(0, magOff - 96)..magOff];
        Assert.Contains("else if (!requiresManualConfirmation)", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("else\n            {\n                if (mag && cr != null)",
            method.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Rear_load_rechecks_source_reservation_and_target_request_data_after_locks(string fileName)
    {
        string source = ReadFlowSource(fileName);
        string method = ExtractMethod(source, "private async Task DoLoad");

        int rackCheck = method.IndexOf("RackHasPhysicalPlateSnapshot(rs)", StringComparison.Ordinal);
        int sourceUnchanged = method.IndexOf("bool sourceUnchanged", StringComparison.Ordinal);
        int requestData = method.IndexOf("IsRequestDataAsync(ct)", StringComparison.Ordinal);
        Assert.True(rackCheck >= 0, $"{fileName} 未找到锁后来源有板检查");
        Assert.True(sourceUnchanged > rackCheck, $"{fileName} 锁后来源有板检查之后必须复核来源预约/缓存身份");
        Assert.True(requestData > sourceUnchanged, $"{fileName} 锁后必须复核目标斜床请求数据");

        Assert.Contains("_rackLedger.IsReserved(rs, out string reservedOp)", method, StringComparison.Ordinal);
        Assert.Contains("_rackLedger.ReleaseReservation(rs, actionId);", method, StringComparison.Ordinal);
        Assert.Contains("锁后复核未通过", method, StringComparison.Ordinal);
    }

    [Fact]
    public void X11_final_callback_pauses_and_shows_alarm_without_duplicate_legacy_event()
    {
        string home = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Presentation", "ViewModels", "Home", "HomeViewModel.cs"));

        Assert.Contains(
            "OnRearCraneX11FinalSafetyAlarm = message => HandleLineSafetyAlarm(1, \"后天车X11取料失败\", message, recordLegacyEvent: false)",
            home,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnRearCraneX11FinalSafetyAlarm = message => HandleLineSafetyAlarm(2, \"后天车X11取料失败\", message, recordLegacyEvent: false)",
            home,
            StringComparison.Ordinal);
        Assert.Contains("if (recordLegacyEvent)", home, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string ReadFlowSource(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", fileName));

    private static string ExtractMethod(string source, string signature)
    {
        int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureStart >= 0, $"未找到方法：{signature}");
        int bodyStart = source.IndexOf('{', signatureStart);
        Assert.True(bodyStart >= 0, $"方法缺少左大括号：{signature}");
        int bodyEnd = FindMatchingBrace(source, bodyStart);
        return source[signatureStart..(bodyEnd + 1)];
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        int depth = 0;
        bool inString = false;
        bool inCharacter = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int index = openBrace; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (!inString && !inCharacter && current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (!inString && !inCharacter && current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (!inCharacter && current == '"' && !IsEscaped(source, index))
            {
                inString = !inString;
                continue;
            }
            if (!inString && current == '\'' && !IsEscaped(source, index))
            {
                inCharacter = !inCharacter;
                continue;
            }
            if (inString || inCharacter) continue;

            if (current == '{') depth++;
            else if (current == '}' && --depth == 0) return index;
        }

        throw new InvalidDataException($"未找到位置 {openBrace} 对应的右大括号。");
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int current = index - 1; current >= 0 && source[current] == '\\'; current--)
            slashCount++;
        return slashCount % 2 != 0;
    }
}
