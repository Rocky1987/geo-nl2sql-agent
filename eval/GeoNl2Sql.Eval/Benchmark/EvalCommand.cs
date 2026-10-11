using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Eval.Common;
using GeoNl2Sql.Eval.Pipeline;
using GeoNl2Sql.Eval.Spike;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
// Microsoft.Extensions.AI 也有同名的抽象類別，這裡指的是評估用的快取。
using CachingChatClient = GeoNl2Sql.Eval.Common.CachingChatClient;

namespace GeoNl2Sql.Eval.Benchmark;

/// <summary>
/// M5 的 <c>eval</c> 子命令（docs/m5-implementation-plan.md §4.3）：60 題標準集 × 模型 × 輪次，每個（模型、輪次）寫一份結果檔，
/// 跑完自動執行 <see cref="ReportCommand"/>。模型回應經 <see cref="CachingChatClient"/> 快取；計分以 geo_reader_pii 重跑最終 SQL（§2.3）。
/// 輸出位置：預設 <c>eval/results/m5/</c>（快取在其下的 <c>cache/</c>）；<c>--replay</c> 只讀 <c>m5/cache/</c>，結果寫到 <c>eval/results/m5-replay/</c>
/// 並與 <c>m5/</c> 的正式結果逐題比對；<c>--fresh</c> 忽略既有快取，結果與新快取寫到 <c>eval/results/m5-fresh-時間/</c>。
/// </summary>
public static class EvalCommand
{
    /// <summary>標準集的一題（<c>benchmark.json</c> 的一個物件）。</summary>
    /// <param name="Id">題號。</param>
    /// <param name="Category">類別。</param>
    /// <param name="Question">題目文字。</param>
    /// <param name="GoldSql">標準 SQL。</param>
    /// <param name="Ordered">是否比對列順序。</param>
    /// <param name="Difficulty">難度。</param>
    private sealed record BenchmarkQuestion(string Id, string Category, string Question, string GoldSql, bool Ordered, string Difficulty);

    /// <summary>Web 的兩個模型軌道。</summary>
    private static readonly (string Track, ModelProvider Provider, string ModelId)[] Tracks =
        [("cloud", ModelProvider.Anthropic, "claude-haiku-4-5"), ("local", ModelProvider.Ollama, "qwen2.5:3b")];

    private static readonly JsonSerializerOptions Json = BenchmarkJson.Options;

    /// <summary>
    /// 執行評估。用法：<c>eval [--models cloud,local] [--runs 3] [--limit N] [--ids S09,J09] [--replay | --fresh]</c>。
    /// </summary>
    /// <param name="config">已載入的設定；讀取 <c>Model</c> 區段（金鑰、Ollama 位址）與 <c>ConnectionStrings:Demo</c>、<c>Reader</c>、<c>ReaderPii</c>。</param>
    /// <param name="args">子命令之後的參數。</param>
    /// <returns>結束代碼：0 成功；1 參數錯誤、<c>--replay</c> 缺快取，或重播結果與正式結果不同。</returns>
    /// <exception cref="InvalidOperationException">缺少連線字串；雲端模型呼叫失敗或本機模型連不上（已完成的呼叫都在快取裡，重跑即可接續）。</exception>
    public static async Task<int> RunAsync(IConfiguration config, string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var replay = args.Contains("--replay");
        var fresh = args.Contains("--fresh");
        var trackNames = (Opt("--models") ?? "cloud,local").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tracks = Tracks.Where(t => trackNames.Contains(t.Track)).ToList();
        if (replay && fresh || tracks.Count != trackNames.Length)
        {
            Console.WriteLine("用法：eval [--models cloud,local] [--runs 3] [--limit N] [--ids S09,J09] [--replay | --fresh]");
            return 1;
        }
        var runs = int.Parse(Opt("--runs") ?? "3");
        string Conn(string name) => config.GetConnectionString(name) ?? throw new InvalidOperationException($"找不到設定 ConnectionStrings:{name}。");
        var limits = config.GetSection(QueryLimits.SectionName).Get<QueryLimits>() ?? new QueryLimits();
        var reader = new ReadOnlySqlExecutor(Conn("Reader"), limits);
        var pii = new ReadOnlySqlExecutor(Conn("ReaderPii"), limits);
        var demo = Conn("Demo");
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema-description.md"));
        var runner = new BenchmarkRunner(new SqlValidator(DemoSchema.Tables), schema, reader.ExecuteAsync, pii.ExecuteAsync);

        var benchmarkBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Questions", "benchmark.json"));
        var questions = JsonSerializer.Deserialize<List<BenchmarkQuestion>>(benchmarkBytes, Json)!
            .Where(q => Opt("--ids") is not { } ids || ids.Split(',').Contains(q.Id, StringComparer.OrdinalIgnoreCase))
            .Take(Opt("--limit") is { } limit ? int.Parse(limit) : int.MaxValue).ToList();
        var originalIds = JsonSerializer.Deserialize<List<BenchmarkQuestion>>(
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Questions", "questions.json")), Json)!.Select(q => q.Id).ToHashSet();
        var benchmarkSha = Sha256(benchmarkBytes);
        var systemSha = Sha256(Encoding.UTF8.GetBytes(PromptBuilder.BuildSystemPrompt(schema)));

        var official = Path.Combine(ReportCommand.ResultsRoot, "m5");
        var outDir = replay ? Path.Combine(ReportCommand.ResultsRoot, "m5-replay")
            : fresh ? Path.Combine(ReportCommand.ResultsRoot, $"m5-fresh-{DateTime.Now:yyyyMMdd-HHmmss}")
            : official;
        var cacheDir = Path.Combine(replay ? official : outDir, "cache");
        var cacheMode = replay ? "replay" : fresh ? "fresh" : "default";
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"題數={questions.Count} 模型={string.Join(",", tracks.Select(t => t.ModelId))} 輪數={runs} 快取={cacheMode} 輸出={outDir}");

        var gold = new Dictionary<string, List<object?[]>>();
        foreach (var q in questions) gold[q.Id] = await SpikeCommand.QueryAsync(demo, q.GoldSql, asReader: false);

        var mismatched = false;
        foreach (var (track, provider, modelId) in tracks)
        {
            var options = PipelineCommand.ReadModelOptions(config, _ => null);
            options.Provider = provider;
            options.ModelId = modelId;
            IChatClient? real = null;
            try
            {
                for (var run = 1; run <= runs; run++)
                {
                    var slug = Regex.Replace($"{provider}-{modelId}", @"[^\w.-]", "_");
                    // 只有本機 Ollama 的 HTTP 錯誤（例如對特定請求回 500）是量測的一部分，記進快取；雲端錯誤一律停止，不寫快取。
                    var cache = new CachingChatClient(Path.Combine(cacheDir, $"{slug}-r{run}.jsonl"), provider.ToString(), modelId, run,
                        replay ? null : () => real ??= ChatClientFactory.Create(options), recordServerErrors: provider == ModelProvider.Ollama);
                    var startedAt = DateTimeOffset.Now;
                    var items = new List<BenchmarkItem>();
                    var missing = new List<string>();
                    foreach (var q in questions)
                    {
                        cache.Tag = q.Id;
                        BenchmarkOutcome o;
                        try { o = await runner.RunAsync(cache, q.Question, gold[q.Id], q.Ordered); }
                        catch (CacheMissException) { missing.Add(q.Id); continue; }
                        if (o.ModelError is not null && (provider != ModelProvider.Ollama || o.ModelErrorStatus is null))
                            throw new InvalidOperationException(
                                $"{q.Id} 模型呼叫失敗（{o.ModelError}），已停止。已完成的呼叫都在快取裡，排除問題後重跑同一命令即可接續。");

                        items.Add(new BenchmarkItem(q.Id, q.Category, q.Difficulty, originalIds.Contains(q.Id) ? "original" : "new",
                            o.FirstCorrect, o.FirstFailure, o.Correct, o.Failure,
                            o.Result?.Attempts.Select(a => new BenchmarkAttempt(a.Sql, a.Failure, a.Message)).ToList() ?? [],
                            o.FalseRejects, o.PermissionDenied, o.ModelCalls, o.InputTokens, o.OutputTokens, o.ModelMs, o.DbMs, o.ModelVersion));
                        Console.WriteLine($"[{track} r{run}] {q.Id} {o.Failure ?? "ok"}（第 1 次 {o.FirstFailure ?? "ok"}）嘗試 {o.Result?.Attempts.Count ?? 0} 次" +
                                          (o.FalseRejects > 0 ? $" ⚠誤擋 {o.FalseRejects}" : "") +
                                          (o.PermissionDenied > 0 ? $" 權限拒絕 {o.PermissionDenied}" : ""));
                    }
                    if (missing.Count > 0)
                    {
                        Console.WriteLine($"快取缺少 {missing.Count} 題的回應：{string.Join(",", missing)}。--replay 不呼叫模型，未寫入結果。");
                        return 1;
                    }

                    var file = Path.Combine(outDir, $"{slug}-r{run}.json");
                    // 溫度與輸出上限是 Nl2SqlPipeline 寫死的值（不對外開放），這裡照抄進檔頭。
                    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new BenchmarkRunFile(
                        benchmarkSha, systemSha, PromptBuilder.Described, Nl2SqlPipeline.MaxCorrections, track, provider.ToString(), modelId,
                        Temperature: 0, MaxOutputTokens: 1024, run, startedAt, cacheMode, cache.Hits, cache.Misses, items), Json));
                    Console.WriteLine($"{track} 第 {run} 輪：有修正 {items.Count(i => i.Correct)}/{items.Count}，無修正 {items.Count(i => i.FirstCorrect)}/{items.Count}；" +
                                      $"模型呼叫 {items.Sum(i => i.ModelCalls)} 次（實際呼叫 {cache.Misses}、快取命中 {cache.Hits}），" +
                                      $"輸入 {items.Sum(i => i.InputTokens)}、輸出 {items.Sum(i => i.OutputTokens)} tokens；已寫入 {file}");
                    if (replay) mismatched |= !SameAsOfficial(Path.Combine(official, $"{slug}-r{run}.json"), items);
                }
            }
            finally
            {
                real?.Dispose();
            }
        }

        await ReportCommand.WriteAsync(outDir);
        return mismatched ? 1 : 0;
    }

    /// <summary>重播結果與正式結果逐題比對（第 1 次與最終的正確性、失敗類型）。正式結果檔不存在時只提示、不算不同。</summary>
    /// <param name="officialFile">正式結果檔。</param>
    /// <param name="items">重播的每題紀錄。</param>
    /// <returns>相同（或沒有正式結果可比）時為 true。</returns>
    private static bool SameAsOfficial(string officialFile, List<BenchmarkItem> items)
    {
        if (!File.Exists(officialFile))
        {
            Console.WriteLine($"沒有正式結果檔可比對：{officialFile}");
            return true;
        }
        var official = JsonSerializer.Deserialize<BenchmarkRunFile>(File.ReadAllText(officialFile), Json)!.Items.ToDictionary(i => i.Id);
        var diffs = items.Where(i => !official.TryGetValue(i.Id, out var o)
                                     || (o.FirstCorrect, o.FirstFailure, o.Correct, o.Failure) != (i.FirstCorrect, i.FirstFailure, i.Correct, i.Failure))
            .Select(i => i.Id).ToList();
        Console.WriteLine(diffs.Count == 0 ? $"與正式結果逐題相同（{items.Count} 題）" : $"與正式結果不同：{string.Join(",", diffs)}");
        return diffs.Count == 0;
    }

    /// <summary>小寫十六進位的 SHA-256。</summary>
    /// <param name="bytes">內容。</param>
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
