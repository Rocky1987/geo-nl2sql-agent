using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GeoNl2Sql.Core;
using GeoNl2Sql.Eval.Common;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using GeoNl2Sql.Eval.Spike;

namespace GeoNl2Sql.Eval.Pipeline;

/// <summary>
/// M2 的 <c>pipeline</c> 子命令：30 題逐題呼叫 <see cref="Nl2SqlPipeline"/>（驗證器 + 唯讀執行 + 有界修正），
/// 以 <see cref="ResultComparer"/> 與標準答案比對，結果寫到 eval/GeoNl2Sql.Eval/Results/。
/// 標準 SQL 走 <c>Demo</c> 連線；生成的 SQL 只走 <c>Reader</c> 連線（geo_reader）。
/// 另外統計「驗證器誤擋」：被驗證器拒絕、但實際結果與標準答案相同的 SQL（必須為 0）。
/// </summary>
public static class PipelineCommand
{
    /// <summary>JSON 輸出用的命名規則（camelCase、縮排、中文不跳脫）。</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>固定回傳同一段文字的聊天用戶端（<c>--fake</c> 用，不呼叫模型）。</summary>
    private sealed class FixedChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>累計呼叫次數與 Token 用量的包裝用戶端（供開跑前確認費用）。</summary>
    private sealed class UsageClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public int Calls { get; private set; }
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            Calls++;
            InputTokens += response.Usage?.InputTokenCount ?? 0;
            OutputTokens += response.Usage?.OutputTokenCount ?? 0;
            return response;
        }
    }

    /// <summary>
    /// 執行 pipeline 量測。用法：<c>pipeline [--limit N] [--runs N] [--provider P] [--model M] [--fake 文字]</c>。
    /// </summary>
    /// <param name="config">已載入的設定；讀取 <c>Model</c> 區段與 <c>ConnectionStrings:Demo</c>、<c>ConnectionStrings:Reader</c>。</param>
    /// <param name="args">子命令之後的參數；意義同 <c>spike</c>。<c>--fake</c> 的 <c>{gold}</c> 會換成該題標準 SQL，用來不花費用驗證流程與驗證器誤擋。</param>
    /// <exception cref="InvalidOperationException">缺少連線字串或金鑰。</exception>
    public static async Task RunAsync(IConfiguration config, string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var fake = Opt("--fake");
        var runs = int.Parse(Opt("--runs") ?? "1");
        var demo = config.GetConnectionString("Demo") ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Demo。");
        var reader = config.GetConnectionString("Reader") ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Reader。");
        var options = ReadModelOptions(config, Opt);
        var executor = new ReadOnlySqlExecutor(reader, config.GetSection(QueryLimits.SectionName).Get<QueryLimits>() ?? new QueryLimits());
        var validator = new SqlValidator(DemoSchema.Tables);
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema-description.md"));

        var questionsBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Questions", "questions.json"));
        var questions = JsonSerializer.Deserialize<List<SpikeCommand.Gold>>(questionsBytes, Json)!
            .Take(int.Parse(Opt("--limit") ?? "30")).ToList();
        using var real = fake is null ? ChatClientFactory.Create(options) : null;
        var usage = real is null ? null : new UsageClient(real);
        var resultsDir = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Results"))).FullName;
        var modelLabel = fake is null ? $"{options.Provider}/{options.ModelId}" : "fake";
        Console.WriteLine($"Model={modelLabel} 題數={questions.Count} 輪數={runs}");

        for (var run = 1; run <= runs; run++)
        {
            var items = new List<object>();
            int correct = 0, correctFirstTry = 0, falseRejects = 0;
            var byCategory = questions.GroupBy(q => q.Category).ToDictionary(g => g.Key, _ => 0);
            foreach (var q in questions)
            {
                var gold = await SpikeCommand.QueryAsync(demo, q.GoldSql, asReader: false);
                IChatClient client = fake is null ? usage! : new FixedChatClient(Regex.Replace(fake.Replace("{gold}", q.GoldSql), @"^", "```sql\n") + "\n```");
                var pipeline = new Nl2SqlPipeline(client, validator, executor.ExecuteAsync, schema);
                Nl2SqlResult result;
                try { result = await pipeline.AskAsync(q.Question); }
                catch (HttpRequestException ex)
                {
                    // 本機 Ollama 偶爾對特定請求回 500：記為失敗，其餘題目繼續跑。
                    Console.WriteLine($"[{run}] {q.Id} model_error {ex.Message}");
                    items.Add(new { q.Id, q.Category, Correct = false, Error = "model_error", ex.Message });
                    continue;
                }

                var isCorrect = result.Success && ResultComparer.SameResult(gold, ResultComparer.Normalize(result.Data!.Rows), q.Ordered);
                var firstTry = isCorrect && result.Attempts.Count == 1;
                var rejectedButCorrect = 0;
                foreach (var a in result.Attempts.Where(a => a.Failure == "rejected"))
                    if (await IsCorrectViaReaderAsync(executor, a.Sql!, gold, q.Ordered)) rejectedButCorrect++;
                if (isCorrect) { correct++; byCategory[q.Category]++; }
                if (firstTry) correctFirstTry++;
                falseRejects += rejectedButCorrect;
                Console.WriteLine($"[{run}] {q.Id} {(isCorrect ? "ok" : result.Success ? "wrong_result" : "failed")} 嘗試 {result.Attempts.Count} 次" +
                                  (rejectedButCorrect > 0 ? $" ⚠誤擋 {rejectedButCorrect}" : ""));
                items.Add(new { q.Id, q.Category, Correct = isCorrect, result.Success, Attempts = result.Attempts, result.FailureReason, FalseRejects = rejectedButCorrect });
            }

            var summary = new { correct, correctFirstTry, falseRejects, total = questions.Count, byCategory,
                calls = usage?.Calls, inputTokens = usage?.InputTokens, outputTokens = usage?.OutputTokens };
            Console.WriteLine($"第 {run} 輪：最終 {correct}/{questions.Count}，一次就對 {correctFirstTry}/{questions.Count}，驗證器誤擋 {falseRejects}，" +
                              string.Join("、", byCategory.Select(kv => $"{kv.Key} {kv.Value}")));
            if (usage is not null) Console.WriteLine($"模型呼叫累計 {usage.Calls} 次，輸入 {usage.InputTokens}、輸出 {usage.OutputTokens} tokens");
            var file = Path.Combine(resultsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-pipeline-{Regex.Replace(modelLabel, @"[^\w.-]", "_")}-r{run}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                model = modelLabel, run,
                questionsSha256 = Convert.ToHexString(SHA256.HashData(questionsBytes)).ToLowerInvariant(),
                startedAt = DateTimeOffset.Now, summary, items,
            }, Json));
            Console.WriteLine($"已寫入 {file}");
        }
    }

    /// <summary>讀取模型設定並套用 <c>--provider</c>／<c>--model</c> 覆寫。</summary>
    /// <param name="config">設定。</param>
    /// <param name="opt">取得命令列選項值的函式。</param>
    internal static ModelOptions ReadModelOptions(IConfiguration config, Func<string, string?> opt)
    {
        var options = config.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
        if (opt("--provider") is { } p) options.Provider = Enum.Parse<ModelProvider>(p, true);
        if (opt("--model") is { } m) options.ModelId = m;
        return options;
    }

    /// <summary>
    /// 把被驗證器拒絕的 SQL 改以唯讀 login 執行，判斷它是否其實就是正確答案（誤擋）。
    /// 只用 geo_reader 執行（第二層防線），所以即使是危險語句也只會被資料庫拒絕；執行失敗一律視為「不是正確答案」。
    /// </summary>
    /// <param name="executor">唯讀執行器。</param>
    /// <param name="sql">被拒絕的 SQL。</param>
    /// <param name="gold">標準結果。</param>
    /// <param name="ordered">是否比對列順序。</param>
    private static async Task<bool> IsCorrectViaReaderAsync(ReadOnlySqlExecutor executor, string sql, List<object?[]> gold, bool ordered)
    {
        try
        {
            var r = await executor.ExecuteAsync(sql);
            return !r.Truncated && ResultComparer.SameResult(gold, ResultComparer.Normalize(r.Rows), ordered);
        }
        catch (SqlException) { return false; }
    }
}
