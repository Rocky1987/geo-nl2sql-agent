using GeoNl2Sql.Core.Guardrails;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Core.Nl2Sql;

/// <summary>
/// 執行器回報的失敗，只帶錯誤號碼。<see cref="Nl2SqlPipeline"/> 同時接受 <see cref="SqlException"/> 與這個型別；
/// 後者讓不經 SqlClient 的執行器與離線測試也能表達「資料庫回了某個錯誤號碼」（<c>SqlException</c> 無法自行建構）。
/// </summary>
/// <param name="errorNumber">對應 <c>SqlException.Number</c>；逾時為 -2。</param>
public sealed class SqlExecutionException(int errorNumber) : Exception($"SQL 執行失敗（錯誤碼 {errorNumber}）")
{
    /// <summary>錯誤號碼。</summary>
    public int ErrorNumber { get; } = errorNumber;
}

/// <summary>管線中一次嘗試的紀錄。</summary>
/// <param name="Response">模型的原始回應文字。</param>
/// <param name="Sql">抽出的 SQL；抽不到為 null。</param>
/// <param name="Failure">null 表示這次成功；否則為 <c>no_sql</c>、<c>rejected</c>（驗證器拒絕）、<c>exec_error</c> 之一。</param>
/// <param name="Message">失敗時回饋給模型的訊息（驗證器的固定原因，或消毒後的訊息）；成功時為 null。永不含資料庫原文。</param>
public sealed record Nl2SqlAttempt(string Response, string? Sql, string? Failure, string? Message);

/// <summary>管線的最終結果。</summary>
/// <param name="Success">是否取得查詢結果。</param>
/// <param name="Sql">最後一次嘗試的 SQL；成功時就是產生結果的那一句，抽不到時為 null。</param>
/// <param name="Data">查詢結果（含是否被截斷）；失敗時為 null。</param>
/// <param name="FailureReason">失敗時最後一次的原因；成功時為 null。</param>
/// <param name="Attempts">每次嘗試的紀錄，依序排列。</param>
public sealed record Nl2SqlResult(bool Success, string? Sql, SqlQueryResult? Data, string? FailureReason, IReadOnlyList<Nl2SqlAttempt> Attempts);

/// <summary>
/// 自然語言轉 SQL 的完整流程：模型生成 → <see cref="SqlExtractor"/> 抽取 → <see cref="SqlValidator"/> 驗證 → 唯讀執行。
/// 任何一步失敗，就把上一次的回應與「消毒後」的原因附加到對話再生成，最多修正 <see cref="MaxCorrections"/> 次。
/// 被驗證器拒絕的 SQL 絕不會送到執行器。M2 的回答就是查詢結果本身，不另外請模型摘要。
/// 用法：<c>await new Nl2SqlPipeline(client, validator, execute, schemaMarkdown).AskAsync("中央區有哪些基地台？")</c>。
/// </summary>
public sealed class Nl2SqlPipeline
{
    /// <summary>修正次數上限（初次生成之外最多再生成 2 次，共 3 次）。固定常數，不開放設定。</summary>
    public const int MaxCorrections = 2;

    private readonly IChatClient _client;
    private readonly SqlValidator _validator;
    private readonly Func<string, CancellationToken, Task<SqlQueryResult>> _execute;
    private readonly string _systemPrompt;

    /// <summary>建立管線。</summary>
    /// <param name="client">聊天用戶端。</param>
    /// <param name="validator">SQL 驗證器（第一道防線）。</param>
    /// <param name="execute">執行 SQL 的委派，正式使用時傳入 <c>ReadOnlySqlExecutor.ExecuteAsync</c>；失敗時拋 <see cref="SqlException"/> 或 <see cref="SqlExecutionException"/>。</param>
    /// <param name="schemaMarkdown">schema 說明文件（db/schema-description.md）的全文。</param>
    /// <param name="schemaVersion"><see cref="PromptBuilder"/> 的版本，預設 described。</param>
    public Nl2SqlPipeline(IChatClient client, SqlValidator validator, Func<string, CancellationToken, Task<SqlQueryResult>> execute,
        string schemaMarkdown, string schemaVersion = PromptBuilder.Described)
    {
        _client = client;
        _validator = validator;
        _execute = execute;
        _systemPrompt = PromptBuilder.BuildSystemPrompt(schemaMarkdown, schemaVersion);
    }

    /// <summary>回答一個問題。</summary>
    /// <param name="question">使用者的自然語言問題。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>結果；SQL 層面的失敗（抽不到、被拒絕、執行錯誤、逾時）不丟例外，以 <see cref="Nl2SqlResult.Success"/> 為 false 回報。模型呼叫本身的例外會往外傳。</returns>
    public async Task<Nl2SqlResult> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages = [new(ChatRole.System, _systemPrompt), new(ChatRole.User, question)];
        var attempts = new List<Nl2SqlAttempt>();
        var options = new ChatOptions { Temperature = 0, MaxOutputTokens = 1024 };

        for (var i = 0; i <= MaxCorrections; i++)
        {
            var response = await _client.GetResponseAsync(messages, options, cancellationToken);
            var (attempt, data) = await TryOnceAsync(response.Text, cancellationToken);
            attempts.Add(attempt);
            if (attempt.Failure is null) return new Nl2SqlResult(true, attempt.Sql, data, null, attempts);

            messages.Add(new(ChatRole.Assistant, response.Text));
            messages.Add(new(ChatRole.User, $"{attempt.Message}\n請修正，同樣只輸出一個 ```sql 區塊。"));
        }

        var last = attempts[^1];
        return new Nl2SqlResult(false, last.Sql, null, last.Message, attempts);
    }

    /// <summary>對一次模型回應跑完抽取、驗證、執行。</summary>
    /// <param name="text">模型回應文字。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    private async Task<(Nl2SqlAttempt, SqlQueryResult?)> TryOnceAsync(string text, CancellationToken cancellationToken)
    {
        var sql = SqlExtractor.Extract(text);
        if (sql is null) return (new(text, null, "no_sql", "回應中找不到 SQL。"), null);

        var verdict = _validator.Validate(sql);
        if (!verdict.IsValid) return (new(text, sql, "rejected", $"SQL 未通過安全檢查：{verdict.Reason}。"), null);

        try
        {
            return (new(text, sql, null, null), await _execute(sql, cancellationToken));
        }
        catch (Exception ex) when (ex is SqlException or SqlExecutionException)
        {
            var number = ex is SqlException s ? s.Number : ((SqlExecutionException)ex).ErrorNumber;
            return (new(text, sql, "exec_error", $"這個 SQL 執行失敗：{SqlErrorSanitizer.Sanitize(number)}"), null);
        }
    }
}
