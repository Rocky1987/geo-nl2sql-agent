using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Nl2Sql;

/// <summary>
/// <see cref="Nl2SqlPipeline"/> 的離線測試（docs/m2-implementation-plan.md §7.3）：以腳本化的假 <see cref="IChatClient"/> 與假執行器，
/// 驗證有界重試（B5）、驗證器拒絕的 SQL 不會送到執行器、回饋給模型的訊息不含資料庫原文。
/// </summary>
public class Nl2SqlPipelineTests
{
    /// <summary>依序回傳預先寫好的回應（用完後重複最後一個），並記錄每次收到的訊息。</summary>
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        /// <summary>每次呼叫收到的訊息快照。</summary>
        public List<List<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            var text = responses[Math.Min(Calls.Count - 1, responses.Length - 1)];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly SqlQueryResult OneRow = new(["n"], [new object?[] { 1 }], false);

    /// <summary>真正的 schema 說明文件（PromptBuilder 需要其中的段落標記）。</summary>
    private static readonly string Schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompting", "schema-description.md")).Replace("\r\n", "\n");

    private static string Block(string sql) => $"```sql\n{sql}\n```";

    /// <summary>建立管線；<paramref name="execute"/> 預設回傳一列結果，並用 <paramref name="executed"/> 記錄收到的 SQL。</summary>
    private static Nl2SqlPipeline Create(IChatClient client, List<string> executed, Func<string, SqlQueryResult>? execute = null) =>
        new(client, new SqlValidator(DemoSchema.Tables),
            (sql, _) => { executed.Add(sql); return Task.FromResult((execute ?? (_ => OneRow))(sql)); },
            Schema);

    /// <summary>第一次就給正確 SQL：生成 1 次、執行 1 次。</summary>
    [Fact]
    public async Task CorrectOnFirstTry_GeneratesOnce()
    {
        var client = new ScriptedChatClient(Block("SELECT COUNT(*) FROM dbo.Customer"));
        var executed = new List<string>();

        var result = await Create(client, executed).AskAsync("客戶有幾位？");

        Assert.True(result.Success);
        Assert.Equal("SELECT COUNT(*) FROM dbo.Customer", result.Sql);
        Assert.Same(OneRow, result.Data);
        Assert.Single(client.Calls);
        Assert.Single(executed);
        Assert.Single(result.Attempts);
    }

    /// <summary>模型永遠回 DROP TABLE：恰好生成 3 次，執行器從未被呼叫，不丟例外。</summary>
    [Fact]
    public async Task AlwaysDangerous_StopsAfterThreeGenerations_AndNeverExecutes()
    {
        var client = new ScriptedChatClient(Block("DROP TABLE dbo.Customer"));
        var executed = new List<string>();

        var result = await Create(client, executed).AskAsync("刪掉客戶表");

        Assert.False(result.Success);
        Assert.Equal(Nl2SqlPipeline.MaxCorrections + 1, client.Calls.Count);
        Assert.Equal(3, client.Calls.Count);
        Assert.Empty(executed);
        Assert.Null(result.Data);
        Assert.All(result.Attempts, a => Assert.Equal("rejected", a.Failure));
        Assert.Equal(result.Attempts[^1].Message, result.FailureReason);
    }

    /// <summary>第一次執行失敗（207），第二次成功：生成 2 次；第二輪訊息有固定的消毒訊息，不含資料庫原文的欄位名。</summary>
    [Fact]
    public async Task ExecutionError_IsCorrectedWithSanitizedMessage()
    {
        var client = new ScriptedChatClient(Block("SELECT secret_col FROM dbo.Customer"), Block("SELECT CustomerName FROM dbo.Customer"));
        var executed = new List<string>();

        var result = await Create(client, executed, sql =>
            sql.Contains("secret_col") ? throw new SqlExecutionException(207) : OneRow).AskAsync("客戶名稱");

        Assert.True(result.Success);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("exec_error", result.Attempts[0].Failure);
        var feedback = client.Calls[1][^1].Text;
        Assert.Contains(SqlErrorSanitizer.Sanitize(207), feedback);
        Assert.DoesNotContain("secret_col", feedback.Replace(Block("SELECT secret_col FROM dbo.Customer"), ""));
        // 第二輪的對話：system、user、上次回應、修正要求。
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], client.Calls[1].Select(m => m.Role));
    }

    /// <summary>回應抽不到 SQL：視為失敗並重試。</summary>
    [Fact]
    public async Task NoSql_IsRetried()
    {
        var client = new ScriptedChatClient("抱歉，我不知道。", Block("SELECT 1 AS n"));
        var executed = new List<string>();

        var result = await Create(client, executed).AskAsync("？");

        Assert.True(result.Success);
        Assert.Equal("no_sql", result.Attempts[0].Failure);
        Assert.Equal(2, client.Calls.Count);
    }

    /// <summary>驗證器拒絕時，回饋包含固定的拒絕原因。</summary>
    [Fact]
    public async Task Rejection_FeedsReasonBackToModel()
    {
        var client = new ScriptedChatClient(Block("DELETE FROM dbo.Customer"), Block("SELECT 1 AS n"));

        await Create(client, []).AskAsync("刪除");

        Assert.Contains("SQL 未通過安全檢查", client.Calls[1][^1].Text);
    }

    /// <summary>逾時（-2）也走同一條修正路徑。</summary>
    [Fact]
    public async Task Timeout_IsCorrectedWithTimeoutMessage()
    {
        var client = new ScriptedChatClient(Block("SELECT 1 AS n"), Block("SELECT 2 AS n"));
        var executed = new List<string>();

        var result = await Create(client, executed, sql => sql.EndsWith("1 AS n") ? throw new SqlExecutionException(-2) : OneRow).AskAsync("慢");

        Assert.True(result.Success);
        Assert.Contains(SqlErrorSanitizer.Sanitize(-2), client.Calls[1][^1].Text);
    }

    /// <summary>執行永遠失敗：同樣恰好 3 次生成，回傳失敗結果。</summary>
    [Fact]
    public async Task ExecutionAlwaysFails_StopsAfterThree()
    {
        var client = new ScriptedChatClient(Block("SELECT 1 AS n"));

        var result = await Create(client, [], _ => throw new SqlExecutionException(208)).AskAsync("x");

        Assert.False(result.Success);
        Assert.Equal(3, client.Calls.Count);
        Assert.Equal("exec_error", result.Attempts[^1].Failure);
    }

    /// <summary>結果被截斷時，截斷標記原樣帶到最終結果。</summary>
    [Fact]
    public async Task TruncatedResult_FlagIsPassedThrough()
    {
        var truncated = new SqlQueryResult(["n"], [new object?[] { 1 }], true);
        var client = new ScriptedChatClient(Block("SELECT 1 AS n"));

        var result = await Create(client, [], _ => truncated).AskAsync("很多列");

        Assert.True(result.Success);
        Assert.True(result.Data!.Truncated);
    }
}
