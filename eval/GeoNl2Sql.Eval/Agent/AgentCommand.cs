using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Eval.Pipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace GeoNl2Sql.Eval.Agent;

/// <summary>
/// M3 的 <c>agent</c> 子命令：用 <c>Questions/tools.json</c> 的 12 題量測模型的工具選擇（docs/m3-implementation-plan.md §6.3）。
/// 每題記錄模型呼叫了哪些工具、參數是否正確、模型呼叫次數與 token 用量，結果寫到 eval/GeoNl2Sql.Eval/Results/。
/// 題組很小，只作為工具迴圈能運作的證據，不是準確率宣稱。
/// </summary>
public static class AgentCommand
{
    /// <summary>JSON 輸出用的命名規則（camelCase、縮排、中文不跳脫）。</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>工具選擇題的一題；參數欄位只在對應的工具才有值。</summary>
    private sealed record ToolQuestion(string Id, string Question, string ExpectedTool, string? DistrictName,
        double? Latitude, double? Longitude, double? RadiusMeters);

    /// <summary>累計模型呼叫次數與 token 用量的包裝用戶端；Agent 與 NL2SQL 管線共用，所以兩者的用量都會算進來。</summary>
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
    /// 執行量測。用法：<c>agent [--limit N] [--runs N] [--provider P] [--model M]</c>。
    /// </summary>
    /// <param name="config">已載入的設定；讀取 <c>Model</c>、<c>Agent</c>、<c>Spatial</c>、<c>Query</c> 區段與 <c>ConnectionStrings:Reader</c>。</param>
    /// <param name="args">子命令之後的參數。</param>
    /// <exception cref="InvalidOperationException">缺少連線字串或金鑰。</exception>
    public static async Task RunAsync(IConfiguration config, string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var runs = int.Parse(Opt("--runs") ?? "1");
        var reader = config.GetConnectionString("Reader") ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Reader。");
        var modelOptions = PipelineCommand.ReadModelOptions(config, Opt);
        var agentOptions = config.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        var spatialOptions = config.GetSection(SpatialOptions.SectionName).Get<SpatialOptions>() ?? new SpatialOptions();
        var executor = new ReadOnlySqlExecutor(reader, config.GetSection(QueryLimits.SectionName).Get<QueryLimits>() ?? new QueryLimits());
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema-description.md"));
        var questionsBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Questions", "tools.json"));
        var questions = JsonSerializer.Deserialize<List<ToolQuestion>>(questionsBytes, Json)!
            .Take(int.Parse(Opt("--limit") ?? "12")).ToList();

        using var real = ChatClientFactory.Create(modelOptions);
        var usage = new UsageClient(real);
        var pipeline = new Nl2SqlPipeline(usage, new SqlValidator(DemoSchema.Tables), executor.ExecuteAsync, schema);
        var agent = new GeoAgent(usage, pipeline, new SpatialQueries(executor, spatialOptions), agentOptions);
        var resultsDir = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Results"))).FullName;
        var modelLabel = $"{modelOptions.Provider}/{modelOptions.ModelId}";
        Console.WriteLine($"Model={modelLabel} 題數={questions.Count} 輪數={runs} 提示詞版本={GeoAgent.PromptVersion} 輪數上限={agentOptions.MaxToolRounds}");

        for (var run = 1; run <= runs; run++)
        {
            var items = new List<object>();
            int toolCorrect = 0, fullyCorrect = 0, limitHits = 0, errors = 0;
            foreach (var q in questions)
            {
                var (calls0, in0, out0) = (usage.Calls, usage.InputTokens, usage.OutputTokens);
                GeoAgentResult result;
                try { result = await agent.RunAsync(q.Question); }
                catch (HttpRequestException ex)
                {
                    // 本機 Ollama 偶爾對特定請求回 500：記為失敗，其餘題目繼續跑。
                    errors++;
                    Console.WriteLine($"[{run}] {q.Id} model_error {ex.Message}");
                    items.Add(new { q.Id, q.ExpectedTool, Error = "model_error", ex.Message });
                    continue;
                }

                var first = result.ToolCalls.FirstOrDefault();
                var toolOk = first?.Name == q.ExpectedTool;
                var paramsOk = toolOk && ParametersMatch(q, first!);
                if (toolOk) toolCorrect++;
                if (paramsOk) fullyCorrect++;
                if (result.HitLimit) limitHits++;
                Console.WriteLine($"[{run}] {q.Id} {(paramsOk ? "ok" : toolOk ? "wrong_params" : "wrong_tool")} " +
                                  $"呼叫：{string.Join(" → ", result.ToolCalls.Select(c => c.Name))}{(result.HitLimit ? "（超過上限）" : "")}");
                items.Add(new
                {
                    q.Id, q.ExpectedTool, ToolCorrect = toolOk, ParamsCorrect = paramsOk, result.HitLimit,
                    ToolCalls = result.ToolCalls, Sql = result.Query?.Sql, MapFeatures = result.Map?.Count ?? 0, result.Answer,
                    ModelCalls = usage.Calls - calls0, InputTokens = usage.InputTokens - in0, OutputTokens = usage.OutputTokens - out0,
                });
            }

            var summary = new { toolCorrect, fullyCorrect, limitHits, errors, total = questions.Count,
                calls = usage.Calls, inputTokens = usage.InputTokens, outputTokens = usage.OutputTokens };
            Console.WriteLine($"第 {run} 輪：選對工具 {toolCorrect}/{questions.Count}，工具與參數都對 {fullyCorrect}/{questions.Count}，超過上限 {limitHits}，模型錯誤 {errors}");
            Console.WriteLine($"模型呼叫累計 {usage.Calls} 次，輸入 {usage.InputTokens}、輸出 {usage.OutputTokens} tokens");
            var file = Path.Combine(resultsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-agent-{Regex.Replace(modelLabel, @"[^\w.-]", "_")}-r{run}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                model = modelLabel, run, promptVersion = GeoAgent.PromptVersion, maxToolRounds = agentOptions.MaxToolRounds,
                questionsSha256 = Convert.ToHexString(SHA256.HashData(questionsBytes)).ToLowerInvariant(),
                startedAt = DateTimeOffset.Now, summary, items,
            }, Json));
            Console.WriteLine($"已寫入 {file}");
        }
    }

    /// <summary>
    /// 判斷第一個工具呼叫的參數是否正確：質心要行政區名稱完全相符；緩衝區緯經度誤差 ≤ 0.001 度、半徑相差 ≤ 0.5 公尺；一般查詢不比對參數。
    /// </summary>
    /// <param name="q">題目（含預期參數）。</param>
    /// <param name="call">模型的第一個工具呼叫。</param>
    private static bool ParametersMatch(ToolQuestion q, ToolCall call)
    {
        double Num(string key) => Convert.ToDouble(call.Arguments[key], System.Globalization.CultureInfo.InvariantCulture);
        return q.ExpectedTool switch
        {
            "get_district_centroid" => call.Arguments.TryGetValue("districtName", out var name) && name as string == q.DistrictName,
            "buffer_around_point" => call.Arguments.ContainsKey("latitude")
                && Math.Abs(Num("latitude") - q.Latitude!.Value) <= 0.001
                && Math.Abs(Num("longitude") - q.Longitude!.Value) <= 0.001
                && Math.Abs(Num("radiusMeters") - q.RadiusMeters!.Value) <= 0.5,
            _ => true,
        };
    }
}
