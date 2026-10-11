using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.FormattableString;

namespace GeoNl2Sql.Eval.Benchmark;

/// <summary>某個條件下各輪的正確題數。</summary>
/// <param name="PerRun">依輪次排列的正確題數。</param>
public sealed record Cell(int[] PerRun)
{
    /// <summary>各輪平均。</summary>
    public double Mean => PerRun.Average();

    /// <summary>各輪最小值。</summary>
    public int Min => PerRun.Min();

    /// <summary>各輪最大值。</summary>
    public int Max => PerRun.Max();
}

/// <summary>一個分組（類別、難度或原／新題）的題數與正確題數。</summary>
/// <param name="Count">分組的題數。</param>
/// <param name="First">無修正（第 1 次嘗試）的正確題數。</param>
/// <param name="Final">有修正（最終）的正確題數。</param>
public sealed record GroupStats(int Count, Cell First, Cell Final);

/// <summary>耗時的百分位數（毫秒，nearest-rank）。</summary>
/// <param name="P50">中位數。</param>
/// <param name="P95">第 95 百分位數。</param>
public sealed record Percentiles(long P50, long P95);

/// <summary>一個模型所有輪次的彙總（docs/m5-implementation-plan.md §6.1）。次數類欄位都是「各輪合計」。</summary>
/// <param name="Track"><c>cloud</c> 或 <c>local</c>。</param>
/// <param name="Provider">供應商。</param>
/// <param name="ModelId">模型 ID。</param>
/// <param name="ModelVersions">供應商回報的模型版本（去重複）。</param>
/// <param name="Runs">輪數。</param>
/// <param name="Total">每輪題數。</param>
/// <param name="First">無修正的正確題數。</param>
/// <param name="Final">有修正的正確題數。</param>
/// <param name="ByCategory">依類別。</param>
/// <param name="ByDifficulty">依難度。</param>
/// <param name="ByOrigin">原 30 題與新 30 題。</param>
/// <param name="FirstFailures">第 1 次嘗試的失敗類型分佈。</param>
/// <param name="FinalFailures">最終的失敗類型分佈。</param>
/// <param name="Rescued">第 1 次錯、修正後對的題次。</param>
/// <param name="RescuedFrom">被救回的題次原本的失敗類型。</param>
/// <param name="VisibleFirstFailures">第 1 次嘗試中管線看得出的失敗（no_sql、rejected、exec_error）題次，是修正可救回的上限。</param>
/// <param name="FalseRejects">驗證器誤擋次數。</param>
/// <param name="PermissionDenied">被權限拒絕（錯誤 230）的嘗試數。</param>
/// <param name="ModelErrors">最終為 model_error 的題次。</param>
/// <param name="PipelineMs">每題管線耗時（模型＋資料庫）。</param>
/// <param name="ModelMs">每題模型呼叫耗時。</param>
/// <param name="DbMs">每題資料庫執行耗時。</param>
/// <param name="CallsPerQuestion">每題平均模型呼叫次數。</param>
/// <param name="InputTokensPerQuestion">每題平均輸入 Token。</param>
/// <param name="OutputTokensPerQuestion">每題平均輸出 Token。</param>
/// <param name="TotalCalls">模型呼叫總數。</param>
/// <param name="TotalInputTokens">輸入 Token 總數。</param>
/// <param name="TotalOutputTokens">輸出 Token 總數。</param>
/// <param name="UnstableIds">各輪結果（無修正或有修正）不一致的題號。</param>
public sealed record TrackSummary(string Track, string Provider, string ModelId, IReadOnlyList<string> ModelVersions, int Runs, int Total,
    Cell First, Cell Final, IReadOnlyDictionary<string, GroupStats> ByCategory, IReadOnlyDictionary<string, GroupStats> ByDifficulty,
    IReadOnlyDictionary<string, GroupStats> ByOrigin, IReadOnlyDictionary<string, int> FirstFailures, IReadOnlyDictionary<string, int> FinalFailures,
    int Rescued, IReadOnlyDictionary<string, int> RescuedFrom, int VisibleFirstFailures, int FalseRejects, int PermissionDenied, int ModelErrors,
    Percentiles PipelineMs, Percentiles ModelMs, Percentiles DbMs, double CallsPerQuestion, double InputTokensPerQuestion,
    double OutputTokensPerQuestion, int TotalCalls, long TotalInputTokens, long TotalOutputTokens, IReadOnlyList<string> UnstableIds);

/// <summary><c>summary.json</c> 的內容。</summary>
/// <param name="BenchmarkSha256">結果檔共同的 <c>benchmark.json</c> SHA-256。</param>
/// <param name="Tracks">每個模型的彙總（雲端在前）。</param>
public sealed record BenchmarkSummary(string BenchmarkSha256, IReadOnlyList<TrackSummary> Tracks);

/// <summary>
/// M5 的 <c>report</c> 子命令（docs/m5-implementation-plan.md §4.4、§6）：只讀同一目錄下的結果檔（<c>{供應商}-{模型}-r{輪次}.json</c>），
/// 不呼叫模型、不連資料庫，產生 <c>summary.json</c>、<c>summary.md</c>（表格）與 <c>accuracy.svg</c>（主表的長條圖，直接寫 SVG 文字）。
/// 用法：<c>report [--dir 目錄]</c>，預設 <c>eval/results/m5/</c>；<c>eval</c> 跑完會自動對它的輸出目錄執行一次。
/// </summary>
public static class ReportCommand
{
    private static readonly Regex RunFile = new(@"^[\w.-]+-r\d+\.json$");
    private static readonly string[] Categories = ["single", "join", "aggregate", "spatial"];
    private static readonly string[] Difficulties = ["easy", "medium", "hard"];
    private static readonly string[] Origins = ["original", "new"];
    private static readonly string[] FailureTypes = ["no_sql", "rejected", "exec_error", "wrong_result", "model_error"];
    private static readonly string[] VisibleFailures = ["no_sql", "rejected", "exec_error"];

    /// <summary>Markdown 分組表的顯示名稱（類別沿用文件中的英文代號）。</summary>
    private static readonly Dictionary<string, string> GroupLabels = new()
    {
        ["easy"] = "易", ["medium"] = "中", ["hard"] = "難", ["original"] = "原題（M1 凍結）", ["new"] = "新題（M5 新增）",
    };

    /// <summary>評估結果的根目錄 <c>eval/results/</c>（由輸出目錄 bin/&lt;組態&gt;/&lt;TFM&gt;/ 往上推算，不依賴工作目錄）。</summary>
    internal static string ResultsRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "results"));

    /// <summary>執行 report。用法：<c>report [--dir 目錄]</c>。</summary>
    /// <param name="args">子命令之後的參數。</param>
    /// <returns>結束代碼：0 成功；1 目錄不存在。</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        var dir = args.SkipWhile(a => a != "--dir").Skip(1).FirstOrDefault() ?? Path.Combine(ResultsRoot, "m5");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"找不到目錄：{dir}");
            return 1;
        }
        await WriteAsync(dir);
        return 0;
    }

    /// <summary>讀取目錄中的結果檔，寫出 <c>summary.json</c>、<c>summary.md</c>、<c>accuracy.svg</c>。沒有結果檔時只提示。</summary>
    /// <param name="dir">結果檔所在目錄（輸出也寫在這裡）。</param>
    public static async Task WriteAsync(string dir)
    {
        var files = Directory.GetFiles(dir, "*.json").Where(f => RunFile.IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<BenchmarkRunFile>(File.ReadAllText(f), BenchmarkJson.Options)!).ToList();
        if (files.Count == 0)
        {
            Console.WriteLine($"{dir} 沒有結果檔。");
            return;
        }
        var summary = Build(files);
        await File.WriteAllTextAsync(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(summary, BenchmarkJson.Options));
        await File.WriteAllTextAsync(Path.Combine(dir, "summary.md"), ToMarkdown(summary));
        await File.WriteAllTextAsync(Path.Combine(dir, "accuracy.svg"), ToSvg(summary));
        Console.WriteLine($"已寫入 {dir} 的 summary.json、summary.md、accuracy.svg");
    }

    /// <summary>彙總結果檔。</summary>
    /// <param name="files">結果檔（可含多個模型）。</param>
    /// <returns>彙總。</returns>
    /// <exception cref="InvalidOperationException">結果檔來自不同版本的題集，或同一模型的各輪題目不同。</exception>
    public static BenchmarkSummary Build(IReadOnlyList<BenchmarkRunFile> files)
    {
        if (files.Select(f => f.BenchmarkSha256).Distinct().Count() > 1)
            throw new InvalidOperationException("結果檔來自不同版本的 benchmark.json，不能放在一起彙總。");
        var tracks = files.GroupBy(f => (f.Track, f.Provider, f.ModelId))
            .OrderBy(g => g.Key.Track == "cloud" ? 0 : 1).ThenBy(g => g.Key.ModelId, StringComparer.Ordinal)
            .Select(g => Summarize(g.OrderBy(f => f.Run).ToList())).ToList();
        return new BenchmarkSummary(files[0].BenchmarkSha256, tracks);
    }

    /// <summary>彙總一個模型的各輪。</summary>
    /// <param name="runs">依輪次排序的結果檔。</param>
    private static TrackSummary Summarize(List<BenchmarkRunFile> runs)
    {
        var ids = runs[0].Items.Select(i => i.Id).ToList();
        if (runs.Any(r => !r.Items.Select(i => i.Id).SequenceEqual(ids)))
            throw new InvalidOperationException($"{runs[0].ModelId} 各輪的題目不同（可能是 --limit 試跑留下的結果檔），請重跑完整輪次。");
        var items = runs.SelectMany(r => r.Items).ToList();

        Cell Count(Func<BenchmarkItem, bool> correct, Func<BenchmarkItem, bool> filter) =>
            new(runs.Select(r => r.Items.Where(filter).Count(correct)).ToArray());
        Dictionary<string, GroupStats> Groups(Func<BenchmarkItem, string> key, string[] order) => order
            .Where(k => runs[0].Items.Any(i => key(i) == k))
            .ToDictionary(k => k, k => new GroupStats(runs[0].Items.Count(i => key(i) == k),
                Count(i => i.FirstCorrect, i => key(i) == k), Count(i => i.Correct, i => key(i) == k)));
        Dictionary<string, int> Failures(Func<BenchmarkItem, string?> failure, IEnumerable<BenchmarkItem> source) =>
            FailureTypes.ToDictionary(t => t, t => source.Count(i => failure(i) == t));
        Percentiles Percentile(Func<BenchmarkItem, long> value)
        {
            var sorted = items.Select(value).Order().ToList();
            return new Percentiles(NearestRank(sorted, 0.50), NearestRank(sorted, 0.95));
        }

        var rescued = items.Where(i => !i.FirstCorrect && i.Correct).ToList();
        var n = (double)items.Count;
        return new TrackSummary(runs[0].Track, runs[0].Provider, runs[0].ModelId,
            items.Select(i => i.ModelVersion).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList(), runs.Count, ids.Count,
            Count(i => i.FirstCorrect, _ => true), Count(i => i.Correct, _ => true),
            Groups(i => i.Category, Categories), Groups(i => i.Difficulty, Difficulties), Groups(i => i.Origin, Origins),
            Failures(i => i.FirstFailure, items), Failures(i => i.Failure, items),
            rescued.Count, Failures(i => i.FirstFailure, rescued).Where(kv => kv.Value > 0).ToDictionary(),
            items.Count(i => i.FirstFailure is { } f && VisibleFailures.Contains(f)),
            items.Sum(i => i.FalseRejects), items.Sum(i => i.PermissionDenied), items.Count(i => i.Failure == "model_error"),
            Percentile(i => i.ModelMs + i.DbMs), Percentile(i => i.ModelMs), Percentile(i => i.DbMs),
            items.Sum(i => i.ModelCalls) / n, items.Sum(i => i.InputTokens) / n, items.Sum(i => i.OutputTokens) / n,
            items.Sum(i => i.ModelCalls), items.Sum(i => i.InputTokens), items.Sum(i => i.OutputTokens),
            ids.Where(id => runs.Select(r => r.Items.First(i => i.Id == id)).Select(i => (i.FirstCorrect, i.Correct)).Distinct().Count() > 1).ToList());
    }

    /// <summary>nearest-rank 百分位數：排序後取第 ⌈p·n⌉ 個。</summary>
    /// <param name="sorted">已排序的值。</param>
    /// <param name="p">0～1。</param>
    internal static long NearestRank(List<long> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];

    /// <summary>產生 <c>summary.md</c>。</summary>
    /// <param name="s">彙總。</param>
    public static string ToMarkdown(BenchmarkSummary s)
    {
        var md = new StringBuilder();
        var t = s.Tracks;
        string Name(TrackSummary x) => $"{(x.Track == "cloud" ? "雲端" : "本機")} {x.ModelId}";
        string Header(string first, Func<TrackSummary, IEnumerable<string>> columns) =>
            $"| {first} | {string.Join(" | ", t.SelectMany(columns))} |\n|---|{string.Concat(t.SelectMany(columns).Select(_ => "---|"))}\n";

        md.Append("# M5 評估結果\n\n");
        md.Append($"由 `report` 從同目錄的結果檔產生，請勿手動編輯。題集 `benchmark.json` 的 SHA-256：`{s.BenchmarkSha256}`。\n\n");
        md.Append("- 「無修正」取同一次執行的第 1 次嘗試，不另外跑；「有修正」是管線最多修正 2 次後的最終結果。\n");
        md.Append("- 計分：最終 SQL 以 `geo_reader_pii` 重跑後與標準答案比對（執行結果比對，不比 SQL 文字）。\n");
        md.Append("- 每格是「各輪平均題數／題數」，括號內為各輪最小～最大。60 題時 1 題＝1.67 個百分點，差 1～2 題不下結論。\n\n");

        md.Append("## 1. 主表\n\n");
        md.Append("| 模型 | 輪數 | 無修正 | 有修正 |\n|---|---|---|---|\n");
        foreach (var x in t)
            md.Append($"| {Name(x)} | {x.Runs} | {Full(x.First, x.Total)} | {Full(x.Final, x.Total)} |\n");

        foreach (var (title, pick) in new (string, Func<TrackSummary, IReadOnlyDictionary<string, GroupStats>>)[]
                 { ("2. 依類別", x => x.ByCategory), ("3. 依難度", x => x.ByDifficulty), ("4. 原 30 題與新 30 題", x => x.ByOrigin) })
        {
            md.Append($"\n## {title}\n\n");
            md.Append(Header("分組", x => [$"{Name(x)} 無修正", $"{Name(x)} 有修正"]));
            foreach (var key in pick(t[0]).Keys)
                md.Append($"| {GroupLabels.GetValueOrDefault(key, key)}（{pick(t[0])[key].Count} 題） | {string.Join(" | ", t.SelectMany(x => pick(x).TryGetValue(key, out var g)
                    ? new[] { Short(g.First, g.Count), Short(g.Final, g.Count) } : ["—", "—"]))} |\n");
        }

        md.Append("\n## 5. 失敗類型（各輪合計題次）\n\n");
        md.Append(Header("失敗類型", x => [$"{Name(x)} 第 1 次", $"{Name(x)} 最終"]));
        foreach (var type in FailureTypes)
            md.Append($"| {type} | {string.Join(" | ", t.SelectMany(x => new[] { x.FirstFailures[type], x.FinalFailures[type] }))} |\n");
        md.Append("\n`no_sql` 抽不到 SQL；`rejected` 被驗證器拒絕；`exec_error` 執行錯誤（含權限拒絕與逾時）；`wrong_result` 能執行但結果不對（管線不知道，不會修正）；`model_error` 模型服務回錯誤。\n\n");
        foreach (var x in t)
            md.Append($"- {Name(x)}：修正救回 {x.Rescued} 題次（原本的失敗類型：{(x.RescuedFrom.Count == 0 ? "—" : string.Join("、", x.RescuedFrom.Select(kv => $"{kv.Key} {kv.Value}")))}）；" +
                      $"第 1 次嘗試中看得出的失敗共 {x.VisibleFirstFailures} 題次，是修正可救回的上限。\n");

        md.Append("\n## 6. 驗證器誤擋、權限拒絕與模型錯誤（各輪合計）\n\n");
        md.Append(Header("項目", x => [Name(x)]));
        md.Append($"| 驗證器誤擋 | {string.Join(" | ", t.Select(x => x.FalseRejects))} |\n");
        md.Append($"| 被權限拒絕（錯誤 230）的嘗試 | {string.Join(" | ", t.Select(x => x.PermissionDenied))} |\n");
        md.Append($"| model_error 題次 | {string.Join(" | ", t.Select(x => x.ModelErrors))} |\n");

        md.Append("\n## 7. 延遲與用量\n\n");
        md.Append(Header("項目", x => [Name(x)]));
        md.Append($"| 每題管線耗時 p50／p95（ms） | {string.Join(" | ", t.Select(x => Invariant($"{x.PipelineMs.P50:N0}／{x.PipelineMs.P95:N0}")))} |\n");
        md.Append($"| 其中模型呼叫 p50／p95（ms） | {string.Join(" | ", t.Select(x => Invariant($"{x.ModelMs.P50:N0}／{x.ModelMs.P95:N0}")))} |\n");
        md.Append($"| 其中資料庫執行 p50／p95（ms） | {string.Join(" | ", t.Select(x => Invariant($"{x.DbMs.P50:N0}／{x.DbMs.P95:N0}")))} |\n");
        md.Append($"| 每題平均模型呼叫次數 | {string.Join(" | ", t.Select(x => x.CallsPerQuestion.ToString("0.00", CultureInfo.InvariantCulture)))} |\n");
        md.Append($"| 每題平均輸入／輸出 token | {string.Join(" | ", t.Select(x => Invariant($"{x.InputTokensPerQuestion:N0}／{x.OutputTokensPerQuestion:N0}")))} |\n");
        md.Append($"| 模型呼叫總數（各輪合計） | {string.Join(" | ", t.Select(x => Invariant($"{x.TotalCalls:N0}")))} |\n");
        md.Append($"| 輸入／輸出 token 總數 | {string.Join(" | ", t.Select(x => Invariant($"{x.TotalInputTokens:N0}／{x.TotalOutputTokens:N0}")))} |\n");
        md.Append($"| 供應商回報的模型版本 | {string.Join(" | ", t.Select(x => x.ModelVersions.Count == 0 ? "—" : string.Join("、", x.ModelVersions)))} |\n");
        md.Append("\n管線耗時＝模型呼叫耗時＋資料庫執行耗時，只算 NL2SQL 管線；網頁請求另有 Agent 的 1～2 次模型呼叫，實際耗時比這個數字長。" +
                  "`--replay` 時模型耗時沿用快取中的實測值，資料庫耗時重新量。\n");

        md.Append("\n## 8. 各輪結果不一致的題目\n\n");
        foreach (var x in t)
            md.Append($"- {Name(x)}：{(x.UnstableIds.Count == 0 ? "無" : string.Join("、", x.UnstableIds))}\n");
        return md.ToString();
    }

    /// <summary>產生 <c>accuracy.svg</c>：每個模型兩根長條（無修正、有修正），高度為各輪平均準確率，鬚線為最小～最大。文字用英文，避免中文字型缺漏。</summary>
    /// <param name="s">彙總。</param>
    public static string ToSvg(BenchmarkSummary s)
    {
        const int width = 640, height = 380, left = 56, right = 24, top = 56, plotHeight = 240, barWidth = 64, gap = 14;
        const int bottom = top + plotHeight;
        var svg = new StringBuilder();
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        double Y(double pct) => bottom - pct / 100 * plotHeight;

        var first = s.Tracks.FirstOrDefault();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Arial, Helvetica, sans-serif\" font-size=\"12\" fill=\"#111827\">\n");
        svg.Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"#ffffff\"/>\n");
        svg.Append($"<text x=\"{width / 2}\" y=\"26\" text-anchor=\"middle\" font-size=\"15\" font-weight=\"bold\">NL2SQL execution accuracy ({first?.Total ?? 0} questions, mean of {first?.Runs ?? 0} runs)</text>\n");
        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            svg.Append($"<line x1=\"{left}\" y1=\"{F(Y(pct))}\" x2=\"{width - right}\" y2=\"{F(Y(pct))}\" stroke=\"#e5e7eb\"/>\n");
            svg.Append($"<text x=\"{left - 8}\" y=\"{F(Y(pct) + 4)}\" text-anchor=\"end\" fill=\"#6b7280\">{pct}%</text>\n");
        }

        var groupWidth = (double)(width - left - right) / Math.Max(1, s.Tracks.Count);
        for (var i = 0; i < s.Tracks.Count; i++)
        {
            var x = s.Tracks[i];
            var center = left + groupWidth * (i + 0.5);
            foreach (var (cell, offset, color) in new[] { (x.First, -barWidth - gap / 2.0, "#9ca3af"), (x.Final, gap / 2.0, "#2563eb") })
            {
                var pct = cell.Mean / x.Total * 100;
                var barX = center + offset;
                var mid = barX + barWidth / 2.0;
                svg.Append($"<rect x=\"{F(barX)}\" y=\"{F(Y(pct))}\" width=\"{barWidth}\" height=\"{F(bottom - Y(pct))}\" fill=\"{color}\"/>\n");
                if (cell.Min != cell.Max)
                {
                    var (yMin, yMax) = (Y(cell.Min * 100.0 / x.Total), Y(cell.Max * 100.0 / x.Total));
                    svg.Append($"<line x1=\"{F(mid)}\" y1=\"{F(yMin)}\" x2=\"{F(mid)}\" y2=\"{F(yMax)}\" stroke=\"#111827\"/>\n");
                    foreach (var y in new[] { yMin, yMax })
                        svg.Append($"<line x1=\"{F(mid - 8)}\" y1=\"{F(y)}\" x2=\"{F(mid + 8)}\" y2=\"{F(y)}\" stroke=\"#111827\"/>\n");
                }
                var labelY = Y(cell.Max * 100.0 / x.Total) - 8;
                svg.Append($"<text x=\"{F(mid)}\" y=\"{F(labelY)}\" text-anchor=\"middle\" font-weight=\"bold\">{pct.ToString("0.0", CultureInfo.InvariantCulture)}%</text>\n");
            }
            svg.Append($"<text x=\"{F(center)}\" y=\"{bottom + 22}\" text-anchor=\"middle\">{(x.Track == "cloud" ? "Cloud" : "Local")} · {SecurityElement.Escape(x.ModelId)}</text>\n");
        }

        var legendY = height - 28;
        svg.Append($"<rect x=\"{left}\" y=\"{legendY - 10}\" width=\"12\" height=\"12\" fill=\"#9ca3af\"/>\n");
        svg.Append($"<text x=\"{left + 18}\" y=\"{legendY}\">Without self-correction (1st attempt)</text>\n");
        svg.Append($"<rect x=\"{left + 270}\" y=\"{legendY - 10}\" width=\"12\" height=\"12\" fill=\"#2563eb\"/>\n");
        svg.Append($"<text x=\"{left + 288}\" y=\"{legendY}\">With self-correction (up to 2 retries)</text>\n");
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>主表的一格：「平均／題數（百分比，最小～最大）」。</summary>
    private static string Full(Cell c, int total) =>
        $"{Mean(c.Mean)}／{total}（{(c.Mean / total * 100).ToString("0.0", CultureInfo.InvariantCulture)}%，{c.Min}～{c.Max}）";

    /// <summary>分組表的一格：「平均／題數」，各輪不同時加「（最小～最大）」。</summary>
    private static string Short(Cell c, int count) => $"{Mean(c.Mean)}／{count}" + (c.Min == c.Max ? "" : $"（{c.Min}～{c.Max}）");

    /// <summary>平均值的顯示：整數不帶小數，否則 1 位小數。</summary>
    private static string Mean(double v) => v % 1 == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
}
