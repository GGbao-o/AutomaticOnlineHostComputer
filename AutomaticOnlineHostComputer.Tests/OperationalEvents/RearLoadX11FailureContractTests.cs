namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

/// <remarks>
/// 两线后天车统一使用共用取板组件；失败必须有最后异常上报和本次归属清理，不能重做旧充磁循环。
/// 历史X11最终报告接口仍存在，但现行三位置探测失败走主严重catch，不能据旧恢复字段推断回队。
/// </remarks>
public sealed class RearLoadX11FailureContractTests
{
    [Theory]
    [InlineData("Line1RearFlowEngine.cs", "1号线")]
    [InlineData("Line2RearFlowEngine.cs", "2号线")]
    public void Three_zero_x11_load_failure_recovers_releases_locks_then_pauses_line(
        string fileName,
        string lineName)
    {
        string method = ExtractMethod(ReadFlowSource(fileName), "private async Task DoLoad");
        Assert.Contains("PickupProbeHelper.PickAsync", method);
        int fault = method.IndexOf("\n        catch (Exception ex)");
        string severe = method[fault..];
        Assert.True(severe.IndexOf("_paused = true;") < severe.IndexOf("ClearSkewOnAbortAsync"));
        Assert.Contains("DiscardReservedWorkpiece(rs, actionId, rackArrivalSeq, wp)", severe);
        Assert.Contains("operation.HoldForManualSafetyRecovery(ex.Message);", severe);
        Assert.Contains("action.Discard(", severe);
        Assert.Contains("OnRearCraneSafetyAlarm?.Invoke", severe);
        Assert.Contains("operation.TryRelease(\"RearCrane\", _craneRearLock)", severe);
        Assert.Contains(lineName, severe);

    }

    [Theory]
    [InlineData("Line1RearFlowEngine.cs")]
    [InlineData("Line2RearFlowEngine.cs")]
    public void Rear_load_cleanup_never_de_magnetizes_once_pickup_may_have_happened(string fileName)
    {
        string method = ExtractMethod(ReadFlowSource(fileName), "private async Task DoLoad");
        int fault = method.IndexOf("\n        catch (Exception ex)");
        int final = method.IndexOf("\n        finally", fault);
        string severe = method[fault..final];
        Assert.Contains("operation.HoldForManualSafetyRecovery(ex.Message);", severe);
        Assert.Contains("requiresManualConfirmation = true;", severe);
        Assert.DoesNotContain("MagnetOffAsync", severe);
        Assert.DoesNotContain("MoveAbsoluteAsync", severe);
        string helper = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Service", "FlowEngine", "PickupProbeHelper.cs"));
        Assert.DoesNotContain("MagnetOffAsync", helper);

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
