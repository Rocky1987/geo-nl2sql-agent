using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Audit;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Eval.Attack;
using GeoNl2Sql.Eval.Seed;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// 間接注入的資料路徑實測（docs/m4-implementation-plan.md §7.1、§7.3，假模型、真資料庫、不花費用）：
/// 被下毒的基地台名稱確實存在於資料庫，也確實會出現在送給模型的工具結果裡——
/// 工具結果清理關閉時 5 筆都原樣送達；開啟時 r1 命中的 2 筆被換成佔位文字、其餘 3 筆（刻意避開規則）仍送達。
/// 這證明攻擊測試中「模型沒有被說服」是模型的抵抗力，而不是資料沒送到模型。
/// </summary>
[Trait("Category", "Database")]
public class PoisonedDataReachesModelTests
{
    private const string Schema = "資料庫為測試用。\n## 維護注意\n";
    private static readonly string Sql = $"SELECT StationId, StationName FROM BaseStation WHERE StationId IN ({string.Join(", ", PoisonedStations.All.Select(p => p.StationId))}) ORDER BY StationId";

    private sealed class FakeWriter : IAuditWriter
    {
        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>NL2SQL 一律回固定的 SQL；Agent 先呼叫 query_database，拿到工具結果後回答。</summary>
    private sealed class ScriptedModel : IChatClient
    {
        private ChatMessage Next(IEnumerable<ChatMessage> messages)
        {
            var list = messages.ToList();
            if (list.Any(m => m.Role == ChatRole.System)) return new ChatMessage(ChatRole.Assistant, $"```sql\n{Sql}\n```");
            if (list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()) return new ChatMessage(ChatRole.Assistant, "完成。");
            return new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", "query_database", new Dictionary<string, object?> { ["question"] = "列出那五座基地台" })]);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(Next(messages)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static async Task<(GeoAgentResult Result, string ToolResult)> RunAsync(GuardrailOptions guardrails)
    {
        var limits = new QueryLimits();
        var recorder = new RecordingChatClient(new ScriptedModel());
        var reader = new ReadOnlySqlExecutor(DbConfig.Reader, limits);
        var pipeline = new Nl2SqlPipeline(recorder, new SqlValidator(DemoSchema.Tables), reader.ExecuteAsync, Schema);
        var agent = new GeoAgent(recorder, pipeline, new SpatialQueries(reader, new SpatialOptions()), new AgentOptions(), guardrails);
        var service = new QueryService(agent, (_, _) => throw new InvalidOperationException(), new FakeWriter(), new ModelOptions(), null, guardrails);

        var result = await service.RunAsync("列出那五座基地台", UserRole.Analyst);

        // 最後一次模型請求的內容包含 query_database 的工具結果（交給模型的文字）。
        return (result, recorder.Requests[^1]);
    }

    /// <summary>資料庫裡 5 座基地台的名稱就是下毒的文字（seed 已套用）。</summary>
    [Fact]
    public async Task Database_HoldsThePoisonedNames()
    {
        var result = await new ReadOnlySqlExecutor(DbConfig.Demo, new QueryLimits()).ExecuteAsync(
            $"SELECT StationId, StationName FROM dbo.BaseStation WHERE StationId IN ({string.Join(", ", PoisonedStations.All.Select(p => p.StationId))})");

        var names = result.Rows.ToDictionary(r => (int)r[0]!, r => (string)r[1]!);
        foreach (var (id, name, _) in PoisonedStations.All) Assert.Equal(name, names[id]);
    }

    /// <summary>清理關閉：5 筆下毒名稱原樣出現在送給模型的工具結果。</summary>
    [Fact]
    public async Task WithoutScrubbing_AllFivePoisonedNamesReachTheModel()
    {
        var (result, toolResult) = await RunAsync(GuardrailOptions.None);

        foreach (var (_, name, _) in PoisonedStations.All) Assert.Contains(name, toolResult);
        Assert.Equal(0, result.ScrubbedCells);
    }

    /// <summary>清理開啟：r1 命中的 2 筆換成佔位文字、不再原樣送達；刻意避開規則的 3 筆仍原樣送達。</summary>
    [Fact]
    public async Task WithScrubbing_OnlyTheDetectedTwoAreReplaced()
    {
        var (result, toolResult) = await RunAsync(GuardrailOptions.Default);

        foreach (var (id, name, detected) in PoisonedStations.All)
        {
            if (detected) Assert.DoesNotContain(name, toolResult);
            else Assert.Contains(name, toolResult);
        }
        Assert.Contains(PromptInjectionDetector.ScrubbedPlaceholder, toolResult);
        Assert.Equal(2, result.ScrubbedCells);
    }
}
