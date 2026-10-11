using System.Diagnostics;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Eval.Common;

/// <summary>一題的執行與計分結果。</summary>
/// <param name="Result">管線結果；模型呼叫丟出 HTTP 錯誤（<c>model_error</c>）時為 null。</param>
/// <param name="Correct">最終結果是否正確（「有修正」）。</param>
/// <param name="FirstCorrect">第 1 次嘗試是否就正確（「無修正」，docs/m5-implementation-plan.md §2.2）。</param>
/// <param name="FirstFailure">第 1 次嘗試的失敗類型；正確時為 null。</param>
/// <param name="Failure">最終的失敗類型：<c>no_sql</c>、<c>rejected</c>、<c>exec_error</c>、<c>wrong_result</c>、<c>model_error</c>；正確時為 null。</param>
/// <param name="FalseRejects">被驗證器拒絕、但實際結果與標準答案相同的嘗試數（驗證器誤擋）。</param>
/// <param name="PermissionDenied">執行時被資料庫以權限拒絕（錯誤 230）的嘗試數。</param>
/// <param name="ModelCalls">模型呼叫次數。</param>
/// <param name="InputTokens">輸入 Token 合計。</param>
/// <param name="OutputTokens">輸出 Token 合計。</param>
/// <param name="ModelMs">模型呼叫耗時合計（毫秒；經過快取時為快取中的實測值）。</param>
/// <param name="DbMs">管線內資料庫執行耗時合計（毫秒；不含計分用的重跑）。</param>
/// <param name="ModelVersion">供應商回報的模型版本（最後一次呼叫）。</param>
/// <param name="ModelError">模型錯誤訊息；沒有錯誤時為 null。</param>
/// <param name="ModelErrorStatus">模型錯誤的 HTTP 狀態碼；連線失敗等沒有狀態碼時為 null。</param>
public sealed record BenchmarkOutcome(Nl2SqlResult? Result, bool Correct, bool FirstCorrect, string? FirstFailure, string? Failure,
    int FalseRejects, int PermissionDenied, int ModelCalls, long InputTokens, long OutputTokens, long ModelMs, long DbMs,
    string? ModelVersion, string? ModelError = null, int? ModelErrorStatus = null);

/// <summary>
/// 對一題跑完 <see cref="Nl2SqlPipeline"/> 並計分（docs/m5-implementation-plan.md §2.2～§2.5），<c>pipeline</c> 與 <c>eval</c> 共用。
/// 管線照產品條件以 <c>execute</c>（geo_reader）執行；提供 <c>rescore</c>（geo_reader_pii）時，最終那句 SQL 改以它重跑後才與標準答案比對，
/// 遮蔽不會被算成 SQL 寫錯。另外量測模型與資料庫耗時、統計驗證器誤擋與權限拒絕（錯誤 230）。<c>Core</c> 不需要任何修改。
/// 用法：<c>await new BenchmarkRunner(validator, schema, reader.ExecuteAsync, pii.ExecuteAsync).RunAsync(client, question, goldRows, ordered)</c>。
/// </summary>
/// <param name="validator">SQL 驗證器。</param>
/// <param name="schemaMarkdown">schema 說明文件全文。</param>
/// <param name="execute">管線使用的執行器。</param>
/// <param name="rescore">計分用的重跑執行器；null 時直接以管線的結果比對（<c>pipeline</c> 子命令的行為）。驗證器誤擋也以它判斷（null 時用 <paramref name="execute"/>）。</param>
public sealed class BenchmarkRunner(SqlValidator validator, string schemaMarkdown,
    Func<string, CancellationToken, Task<SqlQueryResult>> execute,
    Func<string, CancellationToken, Task<SqlQueryResult>>? rescore = null)
{
    /// <summary>SQL Server「拒絕了 SELECT 權限」的錯誤號碼（M4 起 geo_reader 讀 dbo.Customer 的個資欄位時收到）。</summary>
    public const int PermissionDeniedError = 230;

    /// <summary>跑一題。</summary>
    /// <param name="client">聊天用戶端（評估時是 <see cref="CachingChatClient"/>）。</param>
    /// <param name="question">題目文字。</param>
    /// <param name="gold">標準 SQL 的結果（<see cref="ResultComparer.Normalize"/> 的表示法）。</param>
    /// <param name="ordered">是否比對列順序。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>執行與計分結果。模型的 HTTP 錯誤記為 <c>model_error</c>，不往外丟；其餘模型例外（含 <see cref="CacheMissException"/>）照常往外傳。</returns>
    public async Task<BenchmarkOutcome> RunAsync(IChatClient client, string question, List<object?[]> gold, bool ordered,
        CancellationToken cancellationToken = default)
    {
        var meter = new MeteredChatClient(client);
        long dbMs = 0;
        var denied = 0;

        async Task<SqlQueryResult> MeasuredExecute(string sql, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            try { return await execute(sql, token); }
            catch (Exception ex) when (ErrorNumber(ex) == PermissionDeniedError) { denied++; throw; }
            finally { dbMs += sw.ElapsedMilliseconds; }
        }

        Nl2SqlResult result;
        try
        {
            result = await new Nl2SqlPipeline(meter, validator, MeasuredExecute, schemaMarkdown).AskAsync(question, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new BenchmarkOutcome(null, false, false, "model_error", "model_error", 0, denied, meter.Calls, meter.InputTokens,
                meter.OutputTokens, meter.ElapsedMs, dbMs, meter.ModelVersion, ex.Message, (int?)ex.StatusCode);
        }

        var correct = result.Success && (rescore is null
            ? ResultComparer.SameResult(gold, ResultComparer.Normalize(result.Data!.Rows), ordered)
            : await IsCorrectAsync(rescore, result.Sql!, gold, ordered, cancellationToken));
        var falseRejects = 0;
        foreach (var attempt in result.Attempts.Where(a => a.Failure == "rejected"))
            if (await IsCorrectAsync(rescore ?? execute, attempt.Sql!, gold, ordered, cancellationToken)) falseRejects++;

        // 第 1 次嘗試成功時管線就停了，所以「第 1 次正確」等於「最終正確且只嘗試 1 次」。
        return new BenchmarkOutcome(result, correct, correct && result.Attempts.Count == 1,
            result.Attempts[0].Failure ?? (correct ? null : "wrong_result"),
            correct ? null : result.Success ? "wrong_result" : result.Attempts[^1].Failure,
            falseRejects, denied, meter.Calls, meter.InputTokens, meter.OutputTokens, meter.ElapsedMs, dbMs, meter.ModelVersion);
    }

    /// <summary>以指定執行器跑一句 SQL，判斷結果是否與標準答案相同；執行失敗或被截斷一律視為不同。</summary>
    /// <param name="executor">執行器（唯讀 login，危險語句只會被資料庫拒絕）。</param>
    /// <param name="sql">要判斷的 SQL。</param>
    /// <param name="gold">標準結果。</param>
    /// <param name="ordered">是否比對列順序。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    private static async Task<bool> IsCorrectAsync(Func<string, CancellationToken, Task<SqlQueryResult>> executor, string sql,
        List<object?[]> gold, bool ordered, CancellationToken cancellationToken)
    {
        try
        {
            var r = await executor(sql, cancellationToken);
            return !r.Truncated && ResultComparer.SameResult(gold, ResultComparer.Normalize(r.Rows), ordered);
        }
        catch (Exception ex) when (ex is SqlException or SqlExecutionException) { return false; }
    }

    /// <summary>取出資料庫錯誤號碼；不是資料庫錯誤時為 null。</summary>
    /// <param name="ex">例外。</param>
    private static int? ErrorNumber(Exception ex) => ex switch
    {
        SqlException s => s.Number,
        SqlExecutionException e => e.ErrorNumber,
        _ => null,
    };

    /// <summary>累計一題內的模型呼叫次數、Token 與耗時。耗時優先取 <see cref="CachingChatClient"/> 放在回應裡的實測值，沒有時自己計時。</summary>
    /// <param name="inner">實際的用戶端。不會被釋放（本類別不呼叫 Dispose）。</param>
    private sealed class MeteredChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public int Calls { get; private set; }
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long ElapsedMs { get; private set; }
        public string? ModelVersion { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            Calls++;
            InputTokens += response.Usage?.InputTokenCount ?? 0;
            OutputTokens += response.Usage?.OutputTokenCount ?? 0;
            ElapsedMs += response.AdditionalProperties?.TryGetValue(CachingChatClient.ElapsedMsKey, out var ms) == true && ms is long cached
                ? cached
                : sw.ElapsedMilliseconds;
            ModelVersion = response.ModelId ?? ModelVersion;
            return response;
        }
    }
}
