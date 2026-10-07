namespace AutomaticOnlineHostComputer.Tests.FlowEngine;

public sealed class GrindingUnloadReturnSt709OverlapContractTests
{
    [Fact]
    public void Z_home_completes_before_unload_done_signal_and_xy_return_start_together_before_idle()
    {
        string method = ExtractMethod(
            Read("Service", "FlowEngine", "GrindingFlowEngine.cs"),
            "private async Task UnloadFromGrinderAsync");

        int st709Coords = Index(method, "TryGetStationCoords(\"ST709\", out int st709X, out int st709Y, out int st709Z)");
        int zHome = Index(method, "await crane.HomeZAsync(ct);");
        int unloadDone = Index(method, "Task unloadDoneTask = grinder.Svc.SetUnloadDoneAsync(ct);");
        int xyReturn = Index(method, "Task st709ReturnXyTask = crane.MoveAbsoluteAsync(ApplyOffsetX(st709X), ApplyOffsetY(st709Y), -1, ct: ct);");
        int joined = Index(method, "await Task.WhenAll(unloadDoneTask, st709ReturnXyTask);");
        int idle = Index(method, "grinder.State = GrinderState.Idle;");

        Assert.True(st709Coords < zHome && zHome < unloadDone && unloadDone < joined && xyReturn < joined && joined < idle,
            "Z回原点必须成功返回后，才可并发发送下料完成信号和空载XY回ST709；两项成功后才可置Idle。");
        Assert.DoesNotContain("Task zHomeTask", method, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), Path.Combine(parts)));

    private static int Index(string source, string text)
    {
        int index = source.IndexOf(text, StringComparison.Ordinal);
        Assert.True(index >= 0, $"未找到预期文本：{text}");
        return index;
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = Index(source, signature);
        int openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"未找到 {signature} 的方法体。");

        int depth = 0;
        for (int i = openingBrace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"{signature} 的方法体未闭合。");
    }
}
