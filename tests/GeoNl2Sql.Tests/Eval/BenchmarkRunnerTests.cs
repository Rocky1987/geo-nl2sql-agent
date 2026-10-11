using System.Net;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Eval.Common;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Eval;

/// <summary>
/// <see cref="BenchmarkRunner"/> 的離線測試（docs/m5-implementation-plan.md §2.2、§2.3、§4.5），以假模型與假執行器驗證：
/// 第 1 次失敗、修正後成功時「無修正」記錯而「有修正」記對；最終 SQL 以第二個執行器（pii）重跑後才比對；權限拒絕的次數計算正確。
/// </summary>
public class BenchmarkRunnerTests
{
    /// <summary>依序回傳預先寫好的回應（用完後重複最後一個）；可設定改為丟出例外。</summary>
    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private int _calls;
        public Exception? Throw { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Throw is not null) throw Throw;
            var text = responses[Math.Min(_calls++, responses.Length - 1)];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"```sql\n{text}\n```")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>真正的 schema 說明文件（PromptBuilder 需要其中的段落標記）。</summary>
    private static readonly string Schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompting", "schema-description.md")).Replace("\r\n", "\n");

    private static readonly List<object?[]> Gold = [["王小明"]];

    private static SqlQueryResult Rows(string value) => new(["FullName"], [new object?[] { value }], false);

    private static Func<string, CancellationToken, Task<SqlQueryResult>> Executor(Func<string, SqlQueryResult> run) =>
        (sql, _) => Task.FromResult(run(sql));

    private static BenchmarkRunner Runner(Func<string, SqlQueryResult> execute, Func<string, SqlQueryResult>? rescore = null) =>
        new(new SqlValidator(DemoSchema.Tables), Schema, Executor(execute), rescore is null ? null : Executor(rescore));

    /// <summary>第 1 次執行錯誤、修正後正確：無修正記錯（exec_error），有修正記對；模型呼叫 2 次。</summary>
    [Fact]
    public async Task FirstAttemptFails_CorrectionSucceeds_CountsOnlyAsCorrectWithCorrection()
    {
        var client = new ScriptedChatClient("SELECT bad_col FROM dbo.Customer", "SELECT FullName FROM dbo.Customer");
        var runner = Runner(sql => sql.Contains("bad_col") ? throw new SqlExecutionException(207) : Rows("王小明"));

        var o = await runner.RunAsync(client, "客戶姓名", Gold, ordered: false);

        Assert.True(o.Correct);
        Assert.False(o.FirstCorrect);
        Assert.Equal("exec_error", o.FirstFailure);
        Assert.Null(o.Failure);
        Assert.Equal(2, o.ModelCalls);
        Assert.Equal(2, o.Result!.Attempts.Count);
    }

    /// <summary>
    /// 管線以 geo_reader 拿到遮蔽值、與標準答案不同；以第二個執行器（geo_reader_pii）重跑最終 SQL 後才比對，所以記對。
    /// 不提供重跑執行器時（pipeline 子命令的行為）同一題記錯。
    /// </summary>
    [Fact]
    public async Task FinalSql_IsRescoredWithSecondExecutor()
    {
        var rescored = new List<string>();
        var withRescore = Runner(_ => Rows("王**"), sql => { rescored.Add(sql); return Rows("王小明"); });
        var withoutRescore = Runner(_ => Rows("王**"));

        var o = await withRescore.RunAsync(new ScriptedChatClient("SELECT FullName FROM Customer"), "客戶姓名", Gold, ordered: false);
        var plain = await withoutRescore.RunAsync(new ScriptedChatClient("SELECT FullName FROM Customer"), "客戶姓名", Gold, ordered: false);

        Assert.True(o.Correct);
        Assert.True(o.FirstCorrect);
        Assert.Equal(["SELECT FullName FROM Customer"], rescored);
        Assert.False(plain.Correct);
        Assert.Equal("wrong_result", plain.Failure);
        Assert.Equal("wrong_result", plain.FirstFailure);
    }

    /// <summary>第 1 次寫 dbo.Customer 被權限拒絕（230），修正後改用 Customer：權限拒絕計 1 次，第 1 次的失敗類型是 exec_error。</summary>
    [Fact]
    public async Task PermissionDenied_IsCounted()
    {
        var client = new ScriptedChatClient("SELECT FullName FROM dbo.Customer", "SELECT FullName FROM Customer");
        var runner = Runner(sql => sql.Contains("dbo.Customer") ? throw new SqlExecutionException(BenchmarkRunner.PermissionDeniedError) : Rows("王**"),
            _ => Rows("王小明"));

        var o = await runner.RunAsync(client, "客戶姓名", Gold, ordered: false);

        Assert.Equal(1, o.PermissionDenied);
        Assert.Equal("exec_error", o.FirstFailure);
        Assert.True(o.Correct);
        Assert.False(o.FirstCorrect);
    }

    /// <summary>模型服務回 HTTP 錯誤：記為 model_error（第 1 次與最終都是），不往外丟，並帶回狀態碼。</summary>
    [Fact]
    public async Task ModelHttpError_IsRecordedAsModelError()
    {
        var client = new ScriptedChatClient("unused") { Throw = new HttpRequestException("500", null, HttpStatusCode.InternalServerError) };

        var o = await Runner(_ => Rows("王小明")).RunAsync(client, "客戶姓名", Gold, ordered: false);

        Assert.Null(o.Result);
        Assert.Equal(("model_error", "model_error"), (o.FirstFailure, o.Failure));
        Assert.Equal(500, o.ModelErrorStatus);
        Assert.False(o.Correct);
    }
}
