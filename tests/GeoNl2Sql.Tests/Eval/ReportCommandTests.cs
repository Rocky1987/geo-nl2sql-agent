using GeoNl2Sql.Eval.Benchmark;

namespace GeoNl2Sql.Tests.Eval;

/// <summary>
/// <see cref="ReportCommand"/> 的離線測試（docs/m5-implementation-plan.md §6）：以手工結果檔驗證主表的平均／最小／最大、
/// 修正救回的計數、各輪不一致的題目、百分位數，以及 Markdown 與 SVG 的內容。
/// </summary>
public class ReportCommandTests
{
    /// <summary>建立一題的紀錄（只填報告會用到的欄位）。</summary>
    private static BenchmarkItem Item(string id, string category, bool firstCorrect, string? firstFailure, bool correct, string? failure,
        long modelMs = 1000, long dbMs = 10) =>
        new(id, category, "easy", "original", firstCorrect, firstFailure, correct, failure, [], 0, 0, 1, 1500, 50, modelMs, dbMs, "v1");

    /// <summary>建立一個雲端的結果檔。</summary>
    private static BenchmarkRunFile Run(int run, params BenchmarkItem[] items) =>
        new("sha", "sys", "described", 2, "cloud", "Anthropic", "claude-haiku-4-5", 0, 1024, run, DateTimeOffset.UnixEpoch, "default", 0, items.Length, items);

    /// <summary>
    /// 兩輪、每輪 3 題：S01 都對；J01 第 1 輪由修正救回、第 2 輪修正失敗（各輪不一致）；G01 都錯。
    /// </summary>
    private static readonly BenchmarkRunFile[] TwoRuns =
    [
        Run(1, Item("S01", "single", true, null, true, null), Item("J01", "join", false, "exec_error", true, null),
            Item("G01", "spatial", false, "wrong_result", false, "wrong_result")),
        Run(2, Item("S01", "single", true, null, true, null), Item("J01", "join", false, "exec_error", false, "exec_error"),
            Item("G01", "spatial", false, "wrong_result", false, "wrong_result")),
    ];

    /// <summary>主表、分組、失敗類型、救回與不一致題目的計算。</summary>
    [Fact]
    public void Build_ComputesCellsRescuesAndUnstableQuestions()
    {
        var t = Assert.Single(ReportCommand.Build(TwoRuns).Tracks);

        Assert.Equal([1, 1], t.First.PerRun);
        Assert.Equal([2, 1], t.Final.PerRun);
        Assert.Equal((1.5, 1, 2), (t.Final.Mean, t.Final.Min, t.Final.Max));
        Assert.Equal([1, 0], t.ByCategory["join"].Final.PerRun);
        Assert.Equal(1, t.Rescued);
        Assert.Equal(1, t.RescuedFrom["exec_error"]);
        Assert.Equal(2, t.VisibleFirstFailures);
        Assert.Equal(2, t.FirstFailures["exec_error"]);
        Assert.Equal(1, t.FinalFailures["exec_error"]);
        Assert.Equal(["J01"], t.UnstableIds);
        Assert.Equal(6, t.TotalCalls);
        Assert.Equal(1010, t.PipelineMs.P50);
    }

    /// <summary>nearest-rank 百分位數。</summary>
    [Fact]
    public void NearestRank_UsesCeiling()
    {
        Assert.Equal(20, ReportCommand.NearestRank([10, 20, 30, 40], 0.50));
        Assert.Equal(40, ReportCommand.NearestRank([10, 20, 30, 40], 0.95));
        Assert.Equal(0, ReportCommand.NearestRank([], 0.50));
    }

    /// <summary>不同版本題集的結果檔不能混在一起彙總。</summary>
    [Fact]
    public void Build_RejectsMixedBenchmarkVersions()
    {
        var other = TwoRuns[1] with { BenchmarkSha256 = "other" };

        Assert.Throws<InvalidOperationException>(() => ReportCommand.Build([TwoRuns[0], other]));
    }

    /// <summary>Markdown 主表的一列與 SVG 的長條、鬚線、標籤。</summary>
    [Fact]
    public void MarkdownAndSvg_ShowMainTable()
    {
        var summary = ReportCommand.Build(TwoRuns);

        var md = ReportCommand.ToMarkdown(summary);
        var svg = ReportCommand.ToSvg(summary);

        Assert.Contains("| 雲端 claude-haiku-4-5 | 2 | 1／3（33.3%，1～1） | 1.5／3（50.0%，1～2） |", md);
        Assert.Contains("- 雲端 claude-haiku-4-5：J01", md);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("Cloud · claude-haiku-4-5", svg);
        Assert.Contains(">33.3%<", svg);
        Assert.Contains(">50.0%<", svg);
        Assert.Equal(2 + 2 + 1, svg.Split("<rect").Length - 1);   // 背景 1、長條 2、圖例 2
        Assert.EndsWith("</svg>\n", svg);
    }
}
