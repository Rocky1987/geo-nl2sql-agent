using System.Globalization;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Extensions.AI;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace GeoNl2Sql.Tests.Agent;

/// <summary>
/// Agent 與工具迴圈的離線測試（docs/m3-implementation-plan.md §6.4）：輪數上限（G7）、請求間的地圖資料隔離、
/// 工具的參數拒絕與固定錯誤訊息（G6）。模型與資料庫都用假的，不花費用、不需要連線。
/// </summary>
public class GeoAgentTests
{
    /// <summary>依腳本回應的假聊天用戶端；記錄被呼叫的次數。</summary>
    private sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, int, ChatMessage> script) : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            return Task.FromResult(new ChatResponse(script(list, Interlocked.Increment(ref _calls))));
        }

        /// <summary>把腳本的回應拆成更新：文字內容每兩個字元一段，其餘內容（工具呼叫）各一段。</summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var message = script(messages.ToList(), Interlocked.Increment(ref _calls));
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                {
                    for (var i = 0; i < text.Text.Length; i += 2)
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text.Text.Substring(i, Math.Min(2, text.Text.Length - i)));
                }
                else
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
                }
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>PromptBuilder 需要的最小 schema 文件（含起訖標記）。</summary>
    private const string Schema = "資料庫為測試用。\n## 維護注意\n";

    /// <summary>永遠不會被連上的唯讀執行器：若參數檢查失效而真的連線，測試會以 SqlException 失敗。</summary>
    private static SpatialQueries UnreachableSpatial() =>
        new(new ReadOnlySqlExecutor("Server=tcp:127.0.0.1,1;Connect Timeout=1", new QueryLimits()), new SpatialOptions());

    /// <summary>
    /// 建立 Nl2SqlPipeline：模型固定回一個 SQL 區塊，執行委派由呼叫端決定。
    /// </summary>
    /// <param name="execute">假的執行委派。</param>
    private static Nl2SqlPipeline Pipeline(Func<string, CancellationToken, Task<SqlQueryResult>> execute)
    {
        var sqlClient = new ScriptedChatClient((_, _) => new ChatMessage(ChatRole.Assistant, "```sql\nSELECT StationName, Location FROM dbo.BaseStation\n```"));
        return new Nl2SqlPipeline(sqlClient, new SqlValidator(DemoSchema.Tables), execute, Schema);
    }

    /// <summary>一列帶點位的查詢結果（geography 位元組，座標為指定的經緯度）。</summary>
    private static SqlQueryResult PointResult(double lon, double lat)
    {
        var point = new GeometryFactory(new PrecisionModel(), 4326).CreatePoint(new Coordinate(lon, lat));
        var bytes = new SqlServerBytesWriter { IsGeography = true }.Write(point);
        return new SqlQueryResult(["StationName", "Location"], [new object?[] { "BS-1", bytes }], false, ["nvarchar", "geography"]);
    }

    /// <summary>
    /// G7：模型永遠要求呼叫工具時，恰好執行上限輪數的工具呼叫後停止（模型請求為上限＋1 次：每輪之後再問一次），不丟例外，回固定訊息。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task ToolLoop_StopsExactlyAtLimit_WithoutThrowing(int limit)
    {
        var client = new ScriptedChatClient((_, n) => new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"c{n}", "buffer_around_point",
                new Dictionary<string, object?> { ["latitude"] = 999.0, ["longitude"] = 121.0, ["radiusMeters"] = 100.0 })]));
        var agent = new GeoAgent(client, Pipeline((_, _) => throw new InvalidOperationException()), UnreachableSpatial(),
            new AgentOptions { MaxToolRounds = limit });

        var result = await agent.RunAsync("永遠呼叫工具");

        Assert.Equal(limit + 1, client.Calls);
        Assert.True(result.HitLimit);
        Assert.Equal(GeoAgent.LimitMessage, result.Answer);
        Assert.Null(result.Map);
    }

    /// <summary>正常流程：模型先呼叫 query_database，再用工具結果回答；地圖資料走旁路，模型收到的工具結果不含座標。</summary>
    [Fact]
    public async Task QueryDatabase_ThenAnswer_FillsMapSideChannel()
    {
        string? toolResultSeenByModel = null;
        var client = new ScriptedChatClient((messages, n) =>
        {
            if (n == 1)
                return new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("c1", "query_database", new Dictionary<string, object?> { ["question"] = "列出基地台" })]);
            toolResultSeenByModel = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString();
            return new ChatMessage(ChatRole.Assistant, "找到 1 座基地台。");
        });
        var agent = new GeoAgent(client, Pipeline((_, _) => Task.FromResult(PointResult(121.5, 25.05))), UnreachableSpatial(), new AgentOptions());

        var result = await agent.RunAsync("列出基地台");

        Assert.False(result.HitLimit);
        Assert.Equal("找到 1 座基地台。", result.Answer);
        Assert.Equal(["query_database"], result.ToolCalls.Select(c => c.Name));
        Assert.NotNull(result.Query);
        Assert.Contains("SELECT StationName", result.Query!.Sql);
        var feature = Assert.Single(result.Map!);
        Assert.Equal(121.5, ((Point)feature.Geometry).X);
        Assert.Contains("BS-1", toolResultSeenByModel);
        Assert.DoesNotContain("121.5", toolResultSeenByModel);
    }

    /// <summary>並行的兩個請求，各自的地圖資料互不混入。</summary>
    [Fact]
    public async Task ConcurrentRequests_DoNotShareMapData()
    {
        var client = new ScriptedChatClient((messages, _) =>
        {
            var hasResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (hasResult) return new ChatMessage(ChatRole.Assistant, "完成");
            var question = messages.Last(m => m.Role == ChatRole.User).Text;
            return new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString(), "query_database", new Dictionary<string, object?> { ["question"] = question })]);
        });
        // 執行委派依問題中的數字決定點位，並刻意延遲讓兩個請求交錯。
        var pipelineClient = new ScriptedChatClient((messages, _) =>
        {
            var q = messages.Last(m => m.Role == ChatRole.User).Text;
            return new ChatMessage(ChatRole.Assistant, $"```sql\nSELECT StationName, Location FROM dbo.BaseStation -- {q}\n```");
        });
        var pipeline = new Nl2SqlPipeline(pipelineClient, new SqlValidator(DemoSchema.Tables), async (sql, _) =>
        {
            await Task.Delay(30);
            var lon = double.Parse(sql[(sql.LastIndexOf("-- ", StringComparison.Ordinal) + 3)..], CultureInfo.InvariantCulture);
            return PointResult(lon, 25);
        }, Schema);
        var agent = new GeoAgent(client, pipeline, UnreachableSpatial(), new AgentOptions());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => agent.RunAsync((120 + i).ToString(CultureInfo.InvariantCulture))));

        for (var i = 0; i < results.Length; i++)
        {
            var feature = Assert.Single(results[i].Map!);
            Assert.Equal(120 + i, ((Point)feature.Geometry).X);
        }
    }

    /// <summary>G6：不合法的緩衝區參數被擋下，回固定訊息，沒有送到資料庫（執行器不可連線，若送出會丟 SqlException）。</summary>
    [Theory]
    [InlineData(95, 121, 500)]
    [InlineData(25, 200, 500)]
    [InlineData(25, 121, 0)]
    [InlineData(25, 121, 1_000_000)]
    public async Task BufferTool_RejectsBadArguments_WithFixedMessage(double lat, double lon, double meters)
    {
        var tools = new GeoTools(Pipeline((_, _) => throw new InvalidOperationException()), UnreachableSpatial(), new MapResult());

        var text = await tools.BufferAroundPointAsync(lat, lon, meters);

        Assert.StartsWith("參數不合法：", text);
        Assert.Equal(0, tools.Map.Count);
    }

    /// <summary>query_database 失敗時，模型只收到消毒後的固定原因，不含資料庫錯誤原文。</summary>
    [Fact]
    public async Task QueryTool_OnDatabaseError_ReturnsSanitizedReason()
    {
        var tools = new GeoTools(Pipeline((_, _) => throw new SqlExecutionException(208)), UnreachableSpatial(), new MapResult());

        var text = await tools.QueryDatabaseAsync("列出基地台");

        Assert.StartsWith("查詢失敗：", text);
        Assert.Contains(SqlErrorSanitizer.Sanitize(208), text);
        Assert.DoesNotContain("SqlExecutionException", text);
        Assert.Equal(0, tools.Map.Count);
    }

    /// <summary>串流：先送工具開始事件，再逐段送回答文字，最後送與非串流相同的完整結果；地圖資料同樣走旁路。</summary>
    [Fact]
    public async Task Streaming_EmitsToolStep_ThenAnswerDeltas_ThenCompleted()
    {
        var client = new ScriptedChatClient((messages, n) => n == 1
            ? new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", "query_database", new Dictionary<string, object?> { ["question"] = "列出基地台" })])
            : new ChatMessage(ChatRole.Assistant, "找到 1 座基地台。"));
        var agent = new GeoAgent(client, Pipeline((_, _) => Task.FromResult(PointResult(121.5, 25.05))), UnreachableSpatial(), new AgentOptions());

        var events = new List<GeoAgentEvent>();
        await foreach (var e in agent.RunStreamingAsync("列出基地台")) events.Add(e);

        Assert.Equal("query_database", Assert.IsType<ToolStarted>(events[0]).Name);
        var deltas = events.OfType<AnswerDelta>().Select(d => d.Text).ToList();
        Assert.True(deltas.Count > 1);
        Assert.Equal("找到 1 座基地台。", string.Concat(deltas));
        var completed = Assert.IsType<Completed>(events[^1]);
        Assert.Equal("找到 1 座基地台。", completed.Result.Answer);
        Assert.False(completed.Result.HitLimit);
        Assert.Single(completed.Result.Map!);
    }

    /// <summary>串流版同樣遵守輪數上限（G7）：停止時 Completed 為固定訊息與 HitLimit。</summary>
    [Fact]
    public async Task Streaming_HitsToolRoundLimit()
    {
        var client = new ScriptedChatClient((_, n) => new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"c{n}", "buffer_around_point",
                new Dictionary<string, object?> { ["latitude"] = 999.0, ["longitude"] = 121.0, ["radiusMeters"] = 100.0 })]));
        var agent = new GeoAgent(client, Pipeline((_, _) => throw new InvalidOperationException()), UnreachableSpatial(),
            new AgentOptions { MaxToolRounds = 2 });

        GeoAgentResult? result = null;
        await foreach (var e in agent.RunStreamingAsync("永遠呼叫工具"))
            if (e is Completed c) result = c.Result;

        Assert.True(result!.HitLimit);
        Assert.Equal(GeoAgent.LimitMessage, result.Answer);
        Assert.Equal(3, client.Calls);
    }

    /// <summary>三個工具都有名稱與非空描述，且緩衝區工具的描述寫明「緯度在前」與「公尺」（模型靠這些選工具與填參數）。</summary>
    [Fact]
    public void Tools_HaveNamesAndDescriptions()
    {
        var tools = new GeoTools(Pipeline((_, _) => throw new InvalidOperationException()), UnreachableSpatial(), new MapResult())
            .CreateFunctions().Cast<AIFunction>().ToList();

        Assert.Equal(["query_database", "get_district_centroid", "buffer_around_point"], tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        var properties = tools.Single(t => t.Name == "buffer_around_point").JsonSchema.GetProperty("properties");
        Assert.Contains("緯度在前", properties.GetProperty("latitude").GetProperty("description").GetString());
        Assert.Contains("公尺", properties.GetProperty("radiusMeters").GetProperty("description").GetString());
    }
}
