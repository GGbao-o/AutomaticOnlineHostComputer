namespace AutomaticOnlineHostComputer.Tests.Presentation;

public sealed class GrindingCacheTooltipContractTests
{
    [Fact]
    public void Grinding_cache_tooltip_formats_all_items_with_optional_identity_and_length()
    {
        string source = ReadHomeSource();
        string formatter = ExtractMethod(source, "FormatGrindingCacheTooltip");

        Assert.Contains("public string GrindingCacheTooltip", source, StringComparison.Ordinal);
        Assert.Contains("_grindingEngine?.GetCachedWorkpiecesSnapshot()", source, StringComparison.Ordinal);
        Assert.Contains("wp.PlateNo", formatter, StringComparison.Ordinal);
        Assert.Contains("wp.Sequence", formatter, StringComparison.Ordinal);
        Assert.Contains("D={wp.Diameter}mm", formatter, StringComparison.Ordinal);
        Assert.Contains("wp.Length > 0", formatter, StringComparison.Ordinal);
        Assert.Contains("L={wp.Length}mm", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("Take(2)", formatter, StringComparison.Ordinal);
    }

    [Fact]
    public void Grinding_cache_kpi_card_binds_the_full_tooltip_on_its_outer_border()
    {
        string xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "Views", "FlowStatus", "FlowStatusView.xaml"));
        int cardStart = xaml.IndexOf("Text=\"研磨缓存\"", StringComparison.Ordinal);

        Assert.True(cardStart >= 0, "未找到研磨缓存KPI卡片。");
        string card = xaml.Substring(Math.Max(0, cardStart - 500), 1200);
        Assert.Contains("ToolTip=\"{Binding GrindingCacheTooltip}\"", card, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.InitialShowDelay=\"1000\"", card, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GrindingCachedCount", card, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding GrindingCacheDetail", card, StringComparison.Ordinal);
    }

    private static string ReadHomeSource() =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "Presentation",
            "ViewModels",
            "Home",
            "HomeViewModel.cs"));

    private static string ExtractMethod(string source, string methodName)
    {
        int start = source.IndexOf($"private static string {methodName}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法 {methodName}。");

        int openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"未找到方法 {methodName} 的方法体。");

        int depth = 0;
        for (int i = openingBrace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(start, i - start + 1);
        }

        throw new InvalidOperationException($"方法 {methodName} 的大括号未闭合。");
    }
}
