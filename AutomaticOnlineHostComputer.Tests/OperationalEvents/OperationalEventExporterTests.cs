using System.Text;
using System.Text.Json;
using AutomaticOnlineHostComputer.Infrastructure.Export;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class OperationalEventExporterTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
    [InlineData(" \t-2+3", "' \t-2+3")]
    [InlineData("\r\n@cmd", "'\r\n@cmd")]
    [InlineData("正常文本", "正常文本")]
    public void Csv_cells_are_formula_safe(string input, string expected)
    {
        Assert.Equal(expected, OperationalEventExporter.MakeCsvCellSafe(input));
    }

    [Fact]
    public void Csv_has_one_bom_fixed_columns_rfc4180_and_structured_first_latest_evidence()
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();

        byte[] bytes = OperationalEventExporter.BuildCsv([item]);

        byte[] bom = Encoding.UTF8.GetPreamble();
        Assert.True(bytes.AsSpan(0, bom.Length).SequenceEqual(bom));
        Assert.False(bytes.AsSpan(bom.Length, bom.Length).SequenceEqual(bom));
        string csv = Encoding.UTF8.GetString(bytes[bom.Length..]);
        string[] records = ParseCsvRecords(csv).ToArray();
        Assert.Equal(2, records.Length);
        string[] headers = ParseCsvFields(records[0]).ToArray();
        string[] values = ParseCsvFields(records[1]).ToArray();
        Assert.Equal(headers.Length, values.Length);
        Assert.True(headers.Length > 150, $"实际列数: {headers.Length}");
        Assert.Contains("First.Position.DisplayX.Availability", headers);
        Assert.Contains("Latest.Position.FeedbackRereadCount.Value", headers);
        Assert.Contains("First.Exception.InnerExceptionChain", headers);
        Assert.Contains("Latest.BusinessState.PhysicalCommitments", headers);
        Assert.Contains("EffectiveGuidance.RequiredActions", headers);
        Assert.Contains("核对现场工件", values[Array.IndexOf(headers, "EffectiveGuidance.RequiredActions")], StringComparison.Ordinal);
        Assert.Contains("记录事件编号", values[Array.IndexOf(headers, "EffectiveGuidance.RequiredActions")], StringComparison.Ordinal);
        Assert.Equal("-5", values[Array.IndexOf(headers, "First.Position.DeltaX.Value")]);
        Assert.Equal("'=P-001", values[Array.IndexOf(headers, "First.Workpiece.PlateNo.Value")]);
        Assert.Equal("ST713,入口", values[Array.IndexOf(headers, "First.Workpiece.Source.Value")]);
        Assert.Equal("双头\"镗\"\r\nA", values[Array.IndexOf(headers, "First.Workpiece.Target.Value")]);
        Assert.Equal("首次详情,含逗号\r单独CR", values[Array.IndexOf(headers, "First.DetailMessage")]);
        Assert.Equal("最近详情\"含引号\"\n单独LF", values[Array.IndexOf(headers, "Latest.DetailMessage")]);
        Assert.Equal("首次结果\r\n跨行", values[Array.IndexOf(headers, "First.Result")]);
        Assert.EndsWith("\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_exports_are_explicit_and_keep_csv_schema()
    {
        byte[] bytes = OperationalEventExporter.BuildCsv([]);
        string csv = Encoding.UTF8.GetString(bytes[Encoding.UTF8.GetPreamble().Length..]);
        string text = OperationalEventExporter.BuildText([], new OperationalEventFormatter());

        Assert.Single(ParseCsvRecords(csv));
        Assert.True(ParseCsvFields(ParseCsvRecords(csv).Single()).Count() > 150);
        Assert.Equal("无可导出事件", text);
    }

    [Fact]
    public async Task Write_entry_snapshots_array_before_file_io_and_text_file_has_one_bom()
    {
        OperationalEvent original = OperationalEventFormattingTestFactory.CreateCompleteEvent();
        OperationalEvent replacement = original with { EventId = "EVT-REPLACED" };
        OperationalEvent[] source = [original];
        string directory = Path.Combine(Path.GetTempPath(), "OperationalEventExporterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string csvPath = Path.Combine(directory, "events.csv");
        string txtPath = Path.Combine(directory, "events.txt");

        try
        {
            Task csvWrite = OperationalEventExporter.WriteCsvAsync(csvPath, source);
            Task textWrite = OperationalEventExporter.WriteTextAsync(txtPath, source, new OperationalEventFormatter());
            source[0] = replacement;
            await Task.WhenAll(csvWrite, textWrite);

            string csv = Encoding.UTF8.GetString((await File.ReadAllBytesAsync(csvPath))[Encoding.UTF8.GetPreamble().Length..]);
            byte[] txtBytes = await File.ReadAllBytesAsync(txtPath);
            byte[] bom = Encoding.UTF8.GetPreamble();
            Assert.Contains(original.EventId, csv, StringComparison.Ordinal);
            Assert.DoesNotContain(replacement.EventId, csv, StringComparison.Ordinal);
            Assert.True(txtBytes.AsSpan(0, bom.Length).SequenceEqual(bom));
            Assert.False(txtBytes.AsSpan(bom.Length, bom.Length).SequenceEqual(bom));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Text_export_has_stable_separator_identity_times_count_and_full_details()
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();

        string text = OperationalEventExporter.BuildText([item, item with { EventId = "EVT-2" }], new OperationalEventFormatter());

        Assert.Contains("事件 1/2", text, StringComparison.Ordinal);
        Assert.Contains("事件 2/2", text, StringComparison.Ordinal);
        Assert.Contains(item.EventId, text, StringComparison.Ordinal);
        Assert.Contains("首次时间", text, StringComparison.Ordinal);
        Assert.Contains("最近时间", text, StringComparison.Ordinal);
        Assert.Contains("累计次数", text, StringComparison.Ordinal);
        Assert.Contains("事件总览", text, StringComparison.Ordinal);
        Assert.Contains("================================================================================", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Dynamic_evidence_collections_round_trip_delimiters_newlines_quotes_and_empty_strings()
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();
        const string adversarial = "|;=\r\n\"quoted\"";
        BusinessStateEvidence sourceState = item.LatestEvidence.BusinessState;
        BusinessStateEvidence state = new(
            sourceState.Availability,
            sourceState.Reason,
            sourceState.LastSuccessfulCheckpoint,
            sourceState.StateBefore,
            sourceState.StateAfter,
            sourceState.CacheBefore,
            sourceState.CacheAfter,
            sourceState.OwnerBefore,
            sourceState.OwnerAfter,
            sourceState.HoldingWorkpiece,
            sourceState.Placed,
            sourceState.CacheNotified,
            [new PhysicalCommitmentEvidence(string.Empty, EvidenceValue<bool>.Unknown(adversarial), string.Empty)]);
        LockEvidence locks = new(
            EvidenceAvailability.Confirmed,
            "locks",
            [new LockItemEvidence(string.Empty, EvidenceValue<bool>.Confirmed(false, adversarial), EvidenceValue<bool>.Unknown(string.Empty), adversarial)]);
        RecoveryEvidence recovery = new(
            EvidenceAvailability.Confirmed,
            "recovery",
            EvidenceValue<bool>.Confirmed(true, "attempted"),
            EvidenceValue<bool>.Confirmed(false, "completed"),
            [new RecoveryStepEvidence(string.Empty, RecoveryStepState.Failed, adversarial)],
            [string.Empty, adversarial],
            string.Empty);
        OperationalEvidence latest = item.LatestEvidence with
        {
            BusinessState = state,
            Locks = locks,
            Recovery = recovery
        };
        item = item with { LatestEvidence = latest };

        byte[] bytes = OperationalEventExporter.BuildCsv([item]);
        string csv = Encoding.UTF8.GetString(bytes[Encoding.UTF8.GetPreamble().Length..]);
        string[] records = ParseCsvRecords(csv).ToArray();
        string[] headers = ParseCsvFields(records[0]).ToArray();
        string[] values = ParseCsvFields(records[1]).ToArray();

        using JsonDocument commitments = ParseJsonColumn(headers, values, "Latest.BusinessState.PhysicalCommitments");
        JsonElement commitment = commitments.RootElement[0];
        Assert.Equal(string.Empty, commitment.GetProperty("Name").GetString());
        Assert.Equal(adversarial, commitment.GetProperty("State").GetProperty("Reason").GetString());
        Assert.Equal(string.Empty, commitment.GetProperty("Detail").GetString());

        using JsonDocument lockItems = ParseJsonColumn(headers, values, "Latest.Locks.Items");
        JsonElement lockItem = lockItems.RootElement[0];
        Assert.Equal(string.Empty, lockItem.GetProperty("Name").GetString());
        Assert.Equal(adversarial, lockItem.GetProperty("HeldAtFailure").GetProperty("Reason").GetString());
        Assert.Equal(string.Empty, lockItem.GetProperty("ReleasedAfterward").GetProperty("Reason").GetString());
        Assert.Equal(adversarial, lockItem.GetProperty("ManualConfirmation").GetString());

        using JsonDocument steps = ParseJsonColumn(headers, values, "Latest.Recovery.Steps");
        JsonElement step = steps.RootElement[0];
        Assert.Equal(string.Empty, step.GetProperty("Step").GetString());
        Assert.Equal(adversarial, step.GetProperty("Detail").GetString());

        using JsonDocument verification = ParseJsonColumn(headers, values, "Latest.Recovery.PostRecoveryVerification");
        Assert.Equal(string.Empty, verification.RootElement[0].GetString());
        Assert.Equal(adversarial, verification.RootElement[1].GetString());
    }

    [Fact]
    public void Commands_and_physical_conclusions_export_complete_evidence_quadruples()
    {
        OperationalEvent item = OperationalEventFormattingTestFactory.CreateCompleteEvent();
        PositionEvidence firstPosition = item.FirstEvidence.Position with
        {
            YFineTuneCommand = new DeviceCommandEvidence(
                DeviceCommandState.Unknown,
                EvidenceAvailability.Unknown,
                "调用点没有命令状态")
        };
        item = item with
        {
            FirstEvidence = item.FirstEvidence with { Position = firstPosition }
        };
        byte[] bytes = OperationalEventExporter.BuildCsv([item]);
        string csv = Encoding.UTF8.GetString(bytes[Encoding.UTF8.GetPreamble().Length..]);
        string[] records = ParseCsvRecords(csv).ToArray();
        string[] headers = ParseCsvFields(records[0]).ToArray();
        string[] values = ParseCsvFields(records[1]).ToArray();

        string commandPrefix = "Latest.MotionAndMagnet.MagnetOnCommand";
        Assert.Equal("Unknown", Value(headers, values, commandPrefix + ".Availability"));
        Assert.Equal("true", Value(headers, values, commandPrefix + ".HasValue"));
        Assert.Equal("SentUnconfirmed", Value(headers, values, commandPrefix + ".Value"));
        Assert.Equal("充磁调用响应超时", Value(headers, values, commandPrefix + ".Reason"));
        Assert.Equal("SentUnconfirmed", Value(headers, values, commandPrefix + ".State"));

        string conclusionPrefix = "Latest.PhysicalConclusion";
        Assert.Equal("Unknown", Value(headers, values, conclusionPrefix + ".Availability"));
        Assert.Equal("true", Value(headers, values, conclusionPrefix + ".HasValue"));
        Assert.Equal("CommandResultUnknown", Value(headers, values, conclusionPrefix + ".Value"));
        using JsonDocument reason = JsonDocument.Parse(Value(headers, values, conclusionPrefix + ".Reason"));
        Assert.Equal("充磁可能已执行", reason.RootElement.GetProperty("Summary").GetString());
        Assert.Equal("命令响应超时", reason.RootElement.GetProperty("Basis").GetString());
        Assert.Equal("CommandResultUnknown", Value(headers, values, conclusionPrefix + ".Code"));

        string unavailableCommand = "First.Position.YFineTuneCommand";
        Assert.Equal("false", Value(headers, values, unavailableCommand + ".HasValue"));
        Assert.Equal("Unknown", Value(headers, values, unavailableCommand + ".Value"));
        Assert.Equal("调用点没有命令状态", Value(headers, values, unavailableCommand + ".Reason"));
    }

    private static JsonDocument ParseJsonColumn(string[] headers, string[] values, string name) =>
        JsonDocument.Parse(Value(headers, values, name));

    private static string Value(string[] headers, string[] values, string name)
    {
        int index = Array.IndexOf(headers, name);
        Assert.True(index >= 0, $"缺少CSV列: {name}");
        return values[index];
    }

    private static IEnumerable<string> ParseCsvRecords(string csv)
    {
        var record = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < csv.Length; index++)
        {
            char character = csv[index];
            if (character == '"')
            {
                record.Append(character);
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    record.Append(csv[++index]);
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (!quoted && character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
            {
                index++;
                yield return record.ToString();
                record.Clear();
            }
            else
            {
                record.Append(character);
            }
        }

        if (record.Length > 0)
        {
            yield return record.ToString();
        }
    }

    private static IEnumerable<string> ParseCsvFields(string record)
    {
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < record.Length; index++)
        {
            char character = record[index];
            if (character == '"')
            {
                if (quoted && index + 1 < record.Length && record[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (!quoted && character == ',')
            {
                yield return field.ToString();
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        yield return field.ToString();
    }
}
