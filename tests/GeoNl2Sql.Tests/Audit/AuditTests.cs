using System.Text.Json;
using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Audit;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Audit;

/// <summary>
/// 稽核寫入的離線測試（docs/m4-implementation-plan.md §5.1、§5.3、§5.4，假的模型、執行器與寫入器）：
/// 每種 Outcome 各一筆、欄位齊全、稽核在結果送出之前寫入、寫入失敗時請求失敗（非串流與串流）。
/// </summary>
public class AuditTests
{
    private const string Schema = "資料庫為測試用。\n## 維護注意\n";
    private const string Sql = "SELECT StationName FROM dbo.BaseStation";

    private static readonly ModelOptions Model = new() { Provider = ModelProvider.Anthropic, ModelId = "test-model" };

    /// <summary>記錄寫入內容的假寫入器；<c>fail</c> 為 true 時丟例外。</summary>
    private sealed class FakeWriter(bool fail = false) : IAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];

        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            if (fail) throw new InvalidOperationException("Server=secret;Password=hunter2");
            lock (Records) Records.Add(record);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 腳本化的假模型：NL2SQL 管線的呼叫（有 system 訊息）回 SQL 區塊；Agent 的呼叫在沒有工具結果時要求 query_database，
    /// 有工具結果後回答。每次呼叫都回報 10 個輸入、5 個輸出 token（串流以最後一個更新回報）。<c>always</c> 為 true 時永遠要求呼叫工具。
    /// </summary>
    private sealed class FakeModel(bool always = false, Exception? throws = null) : IChatClient
    {
        private ChatMessage Next(IEnumerable<ChatMessage> messages)
        {
            if (throws is not null) throw throws;
            var list = messages.ToList();
            if (list.Any(m => m.Role == ChatRole.System)) return new ChatMessage(ChatRole.Assistant, $"```sql\n{Sql}\n```");
            if (!always && list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                return new ChatMessage(ChatRole.Assistant, "找到 2 座基地台。");
            return new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString("N"), "query_database", new Dictionary<string, object?> { ["question"] = "列出基地台" })]);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(Next(messages)) { Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 } });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var message = Next(messages);
            foreach (var content in message.Contents)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
            yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })] };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly SqlQueryResult TwoRows = new(["StationName"], [["BS-1"], ["BS-2"]], false, ["nvarchar"]);

    /// <summary>組出 QueryService：模型與執行器都是假的；執行 SQL 失敗時改丟 <see cref="SqlExecutionException"/>。</summary>
    private static QueryService Service(FakeWriter writer, FakeModel? model = null, bool sqlFails = false, int maxRounds = 6)
    {
        var client = new UsageTrackingChatClient(model ?? new FakeModel());
        var pipeline = new Nl2SqlPipeline(client, new SqlValidator(DemoSchema.Tables),
            (_, _) => sqlFails ? throw new SqlExecutionException(208) : Task.FromResult(TwoRows), Schema);
        var spatial = new SpatialQueries(new ReadOnlySqlExecutor("Server=tcp:127.0.0.1,1;Connect Timeout=1", new QueryLimits()), new SpatialOptions());
        var agent = new GeoAgent(client, pipeline, spatial, new AgentOptions { MaxToolRounds = maxRounds });
        return new QueryService(agent, (_, _) => throw new InvalidOperationException("analyst 不應呼叫 pii"), writer, Model);
    }

    /// <summary>成功的請求：一筆紀錄，欄位齊全（角色、問題、SQL、列數、模型呼叫與 token 合計、版本、回答摘錄、工具呼叫）。</summary>
    [Fact]
    public async Task Success_WritesOneCompleteRecord()
    {
        var writer = new FakeWriter();

        var result = await Service(writer).RunAsync("列出基地台", UserRole.Analyst);

        Assert.Equal("找到 2 座基地台。", result.Answer);
        var r = Assert.Single(writer.Records);
        Assert.Equal(AuditOutcome.Success, r.Outcome);
        Assert.Equal("analyst", r.Role);
        Assert.Equal("列出基地台", r.Question);
        Assert.Equal(Sql, r.Sql);
        Assert.Equal(1, r.Attempts);
        Assert.Equal(2, r.RowCount);
        Assert.False(r.Truncated);
        // Agent 呼叫 2 次（要求工具、回答）＋ NL2SQL 管線 1 次。
        Assert.Equal(3, r.ModelCalls);
        Assert.Equal(30, r.InputTokens);
        Assert.Equal(15, r.OutputTokens);
        Assert.Equal("Anthropic", r.Provider);
        Assert.Equal("test-model", r.ModelId);
        Assert.Equal(GeoAgent.PromptVersion, r.AgentPromptVersion);
        Assert.Equal("described", r.Nl2SqlPromptVersion);
        Assert.Equal("找到 2 座基地台。", r.AnswerExcerpt);
        Assert.Null(r.GuardRule);
        Assert.Equal(0, r.ScrubbedCells);
        Assert.InRange(r.OccurredAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1));
        var calls = JsonDocument.Parse(r.ToolCalls!).RootElement;
        Assert.Equal("query_database", calls[0].GetProperty("name").GetString());
        Assert.Equal("列出基地台", calls[0].GetProperty("arguments").GetProperty("question").GetString());
    }

    /// <summary>串流版：稽核在 <see cref="Completed"/> 送出之前就寫好，內容與非串流相同（含串流回報的 token）。</summary>
    [Fact]
    public async Task Streaming_WritesRecordBeforeCompleted()
    {
        var writer = new FakeWriter();
        var recordsWhenCompleted = -1;

        await foreach (var e in Service(writer).RunStreamingAsync("列出基地台", UserRole.Analyst))
            if (e is Completed) recordsWhenCompleted = writer.Records.Count;

        Assert.Equal(1, recordsWhenCompleted);
        var r = Assert.Single(writer.Records);
        Assert.Equal(AuditOutcome.Success, r.Outcome);
        Assert.Equal(3, r.ModelCalls);
        Assert.Equal(30, r.InputTokens);
    }

    /// <summary>SQL 一直執行失敗：Outcome 是 failed，仍記下最後的 SQL 與 3 次嘗試。</summary>
    [Fact]
    public async Task QueryFailure_IsRecordedAsFailed()
    {
        var writer = new FakeWriter();

        await Service(writer, sqlFails: true).RunAsync("列出基地台", UserRole.Analyst);

        var r = Assert.Single(writer.Records);
        Assert.Equal(AuditOutcome.Failed, r.Outcome);
        Assert.Equal(Sql, r.Sql);
        Assert.Equal(Nl2SqlPipeline.MaxCorrections + 1, r.Attempts);
        Assert.Null(r.RowCount);
    }

    /// <summary>模型永遠要求呼叫工具：超過輪數上限，Outcome 是 limit。</summary>
    [Fact]
    public async Task ToolRoundLimit_IsRecordedAsLimit()
    {
        var writer = new FakeWriter();

        var result = await Service(writer, new FakeModel(always: true), maxRounds: 1).RunAsync("列出基地台", UserRole.Analyst);

        Assert.True(result.HitLimit);
        Assert.Equal(AuditOutcome.Limit, Assert.Single(writer.Records).Outcome);
    }

    /// <summary>模型服務丟例外：記一筆 model_error，原本的例外照常往外傳（控制器據此回 502）。</summary>
    [Fact]
    public async Task ModelException_IsRecordedAsModelError_AndRethrown()
    {
        var writer = new FakeWriter();
        var service = Service(writer, new FakeModel(throws: new HttpRequestException("連不上")));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RunAsync("列出基地台", UserRole.Admin));

        var r = Assert.Single(writer.Records);
        Assert.Equal(AuditOutcome.ModelError, r.Outcome);
        Assert.Equal("admin", r.Role);
        Assert.Equal(1, r.ModelCalls);
    }

    /// <summary>不合法請求（含不合法的角色原文與過長的問題）記為 invalid；過長的文字截斷到欄位上限。</summary>
    [Fact]
    public async Task InvalidRequest_IsRecordedAsInvalid_WithClippedText()
    {
        var writer = new FakeWriter();
        var service = Service(writer);

        await service.RecordInvalidAsync(new string('問', 800), "superuser");
        await service.RecordInvalidAsync(null, null);

        Assert.Equal(2, writer.Records.Count);
        Assert.All(writer.Records, r => Assert.Equal(AuditOutcome.Invalid, r.Outcome));
        Assert.Equal(AuditRecord.MaxTextLength, writer.Records[0].Question.Length);
        Assert.Equal("superuser", writer.Records[0].Role);
        Assert.Equal("", writer.Records[1].Question);
        Assert.Equal("analyst", writer.Records[1].Role);
        Assert.All(writer.Records, r => Assert.Equal(0, r.ModelCalls));
    }

    /// <summary>寫入失敗（H4）：非串流丟 <see cref="AuditWriteException"/>，例外訊息不含底層細節。</summary>
    [Fact]
    public async Task WriterFailure_FailsTheRequest()
    {
        var ex = await Assert.ThrowsAsync<AuditWriteException>(() => Service(new FakeWriter(fail: true)).RunAsync("列出基地台", UserRole.Analyst));

        Assert.DoesNotContain("hunter2", ex.Message);
    }

    /// <summary>寫入失敗（H4）：串流在送出 <see cref="Completed"/> 之前就丟例外，結果不會送出。</summary>
    [Fact]
    public async Task WriterFailure_StreamingNeverYieldsCompleted()
    {
        var completed = false;

        await Assert.ThrowsAsync<AuditWriteException>(async () =>
        {
            await foreach (var e in Service(new FakeWriter(fail: true)).RunStreamingAsync("列出基地台", UserRole.Analyst))
                if (e is Completed) completed = true;
        });

        Assert.False(completed);
    }

    /// <summary>不合法請求的稽核寫入失敗，同樣丟 <see cref="AuditWriteException"/>。</summary>
    [Fact]
    public async Task WriterFailure_OnInvalidRequest_Throws()
    {
        await Assert.ThrowsAsync<AuditWriteException>(() => Service(new FakeWriter(fail: true)).RecordInvalidAsync("", null));
    }

    /// <summary>請求被使用者中斷（取消）：不是失敗也沒有回應，不寫稽核。</summary>
    [Fact]
    public async Task Cancelled_WritesNothing()
    {
        var writer = new FakeWriter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(writer, new FakeModel(throws: new OperationCanceledException(cts.Token))).RunAsync("列出基地台", UserRole.Analyst, cts.Token));

        Assert.Empty(writer.Records);
    }
}
