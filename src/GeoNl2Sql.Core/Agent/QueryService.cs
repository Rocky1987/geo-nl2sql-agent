using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GeoNl2Sql.Core.Audit;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace GeoNl2Sql.Core.Agent;

/// <summary>
/// 請求選擇的角色。這是使用者在頁面自選的，<b>不是驗證過的身分</b>，也不是存取控制（docs/m4-implementation-plan.md §4.1）；
/// 它只決定結果表要不要顯示原始個資。
/// </summary>
public enum UserRole
{
    /// <summary>預設。表格與回答都只有遮蔽後的值。</summary>
    Analyst,

    /// <summary>表格顯示原始個資（經 <c>geo_reader_pii</c> 旁路重跑）；模型看到的仍是遮蔽值。</summary>
    Admin,
}

/// <summary>
/// 在 <see cref="GeoAgent"/> 外面加上角色處理與稽核（docs/m4-implementation-plan.md §4.2、§5）。
/// 模型路徑永遠是 <see cref="GeoAgent"/> 內的 <c>geo_reader</c>，所以不論角色，送往模型的內容都不含原始個資；
/// admin 只在請求結束後，以 <c>geo_reader_pii</c> 重跑同一句已通過驗證的 SQL，用結果取代表格與地圖要素，重跑結果不回給模型。
/// 每個請求在<b>送出結果之前</b>寫一筆稽核；寫入失敗就丟 <see cref="AuditWriteException"/>，結果不會送出。
/// 用法：<c>var result = await new QueryService(agent, piiExecutor.ExecuteAsync, auditWriter, modelOptions).RunAsync("列出客戶", UserRole.Admin)</c>。
/// </summary>
public sealed class QueryService
{
    private readonly GeoAgent _agent;
    private readonly Func<string, CancellationToken, Task<SqlQueryResult>> _piiExecute;
    private readonly IAuditWriter _audit;
    private readonly ModelOptions _model;
    private readonly ILogger? _logger;

    /// <summary>
    /// 建立服務。
    /// </summary>
    /// <param name="agent">Agent（內部只用 <c>geo_reader</c>）。</param>
    /// <param name="piiExecute">以 <c>geo_reader_pii</c> 執行 SQL 的委派；只有 admin 請求會呼叫。</param>
    /// <param name="audit">稽核寫入器。要累計 token 用量，Agent 的 <see cref="Microsoft.Extensions.AI.IChatClient"/> 須是 <see cref="UsageTrackingChatClient"/>。</param>
    /// <param name="model">模型設定，只用來在稽核紀錄記下供應商與模型。</param>
    /// <param name="logger">旁路重跑失敗、稽核寫入失敗時記錄用；可省略。</param>
    public QueryService(GeoAgent agent, Func<string, CancellationToken, Task<SqlQueryResult>> piiExecute, IAuditWriter audit, ModelOptions model,
        ILogger? logger = null)
    {
        _agent = agent;
        _piiExecute = piiExecute;
        _audit = audit;
        _model = model;
        _logger = logger;
    }

    /// <summary>
    /// 回答一個問題。
    /// </summary>
    /// <param name="question">使用者的問題。</param>
    /// <param name="role">請求選擇的角色。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>與 <see cref="GeoAgent.RunAsync"/> 相同；admin 的表格與地圖要素換成原始值。</returns>
    /// <exception cref="AuditWriteException">稽核寫入失敗；結果不應送出。</exception>
    public async Task<GeoAgentResult> RunAsync(string question, UserRole role, CancellationToken cancellationToken = default)
    {
        var scope = UsageScope.Begin();
        var started = new Started();
        GeoAgentResult result;
        try
        {
            result = await _agent.RunAsync(question, cancellationToken);
            if (role == UserRole.Admin) result = await RevealAsync(result, _piiExecute, _logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 使用者已中斷連線，沒有回應可言，不記稽核。
        }
        catch (Exception)
        {
            await AuditModelErrorAsync(question, role, scope, started);
            throw;
        }

        await WriteAuditAsync(Build(question, role, result, scope, started));
        return result;
    }

    /// <summary>
    /// 串流版：事件原樣轉出，只有最後的 <see cref="Completed"/> 依角色換成處理後的結果，並在送出它之前寫入稽核。
    /// 這個方法本身不是迭代器：用量範圍要在呼叫端的同步段落建立，之後每次繼續列舉才看得到。
    /// </summary>
    /// <param name="question">使用者的問題。</param>
    /// <param name="role">請求選擇的角色。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <exception cref="AuditWriteException">稽核寫入失敗（列舉時丟出）；<see cref="Completed"/> 不會送出。</exception>
    public IAsyncEnumerable<GeoAgentEvent> RunStreamingAsync(string question, UserRole role, CancellationToken cancellationToken = default) =>
        Stream(question, role, UsageScope.Begin(), cancellationToken);

    /// <summary>串流的實際工作；try/catch 不能包住 yield，所以手動列舉並只把 MoveNext 放在 try 裡。</summary>
    private async IAsyncEnumerable<GeoAgentEvent> Stream(
        string question, UserRole role, UsageScope scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var started = new Started();
        await using var events = _agent.RunStreamingAsync(question, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            GeoAgentEvent current;
            try
            {
                if (!await events.MoveNextAsync()) break;
                current = events.Current;
                if (current is Completed completed && role == UserRole.Admin)
                    current = new Completed(await RevealAsync(completed.Result, _piiExecute, _logger, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await AuditModelErrorAsync(question, role, scope, started);
                throw;
            }

            if (current is Completed done) await WriteAuditAsync(Build(question, role, done.Result, scope, started));
            yield return current;
        }
    }

    /// <summary>
    /// 記錄一個沒有進到 Agent 的不合法請求（空問題、過長、角色不合法），Outcome 為 <see cref="AuditOutcome.Invalid"/>。
    /// </summary>
    /// <param name="question">請求的問題原文；可為 null。</param>
    /// <param name="role">請求的角色原文；null 視為預設的 analyst。</param>
    /// <exception cref="AuditWriteException">稽核寫入失敗。</exception>
    public Task RecordInvalidAsync(string? question, string? role) => WriteAuditAsync(new AuditRecord
    {
        OccurredAt = DateTime.UtcNow,
        Role = AuditRecord.Clip(role ?? "analyst", AuditRecord.MaxRoleLength)!,
        Question = AuditRecord.Clip(question ?? "", AuditRecord.MaxTextLength)!,
        Outcome = AuditOutcome.Invalid,
        Provider = _model.Provider.ToString(),
        ModelId = _model.ModelId,
        AgentPromptVersion = GeoAgent.PromptVersion,
        Nl2SqlPromptVersion = PromptBuilder.Described,
    });

    /// <summary>請求的起點：時間與計時器。</summary>
    private sealed class Started
    {
        public DateTime At { get; } = DateTime.UtcNow;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
    }

    /// <summary>由結果組出稽核紀錄；Outcome 依 <see cref="GeoAgentResult"/> 判斷（與回應的 error 欄位一致）。</summary>
    private AuditRecord Build(string question, UserRole role, GeoAgentResult result, UsageScope scope, Started started)
    {
        var outcome = result.HitLimit ? AuditOutcome.Limit
            : result.Query is { Success: false } ? AuditOutcome.Failed
            : AuditOutcome.Success;
        var toolCalls = result.ToolCalls.Count == 0 ? null
            : JsonSerializer.Serialize(result.ToolCalls.Select(c => new { name = c.Name, arguments = c.Arguments }));
        return Base(question, role, outcome, scope, started) with
        {
            ToolCalls = toolCalls,
            Sql = result.Query?.Sql,
            Attempts = result.Query?.Attempts.Count,
            RowCount = result.Query?.Data?.Rows.Count,
            Truncated = result.Query?.Data?.Truncated,
            AnswerExcerpt = AuditRecord.Clip(result.Answer, AuditRecord.MaxTextLength),
        };
    }

    /// <summary>組出共通欄位（時間、角色、問題、用量、重現用的版本）。</summary>
    private AuditRecord Base(string question, UserRole role, string outcome, UsageScope scope, Started started) => new()
    {
        OccurredAt = started.At,
        DurationMs = (int)started.Clock.ElapsedMilliseconds,
        Role = role.ToString().ToLowerInvariant(),
        Question = AuditRecord.Clip(question, AuditRecord.MaxTextLength)!,
        Outcome = outcome,
        ModelCalls = scope.ModelCalls,
        InputTokens = scope.InputTokens,
        OutputTokens = scope.OutputTokens,
        Provider = _model.Provider.ToString(),
        ModelId = _model.ModelId,
        AgentPromptVersion = GeoAgent.PromptVersion,
        Nl2SqlPromptVersion = PromptBuilder.Described,
    };

    /// <summary>請求本身已因例外失敗：盡力記一筆 <see cref="AuditOutcome.ModelError"/>；稽核也失敗時只記日誌，讓原本的例外繼續往外傳。</summary>
    private async Task AuditModelErrorAsync(string question, UserRole role, UsageScope scope, Started started)
    {
        try
        {
            await WriteAuditAsync(Base(question, role, AuditOutcome.ModelError, scope, started));
        }
        catch (AuditWriteException)
        {
            // WriteAuditAsync 已記錄日誌。
        }
    }

    /// <summary>寫入稽核。用 <see cref="CancellationToken.None"/>：使用者中途取消也不能讓已發生的請求沒有紀錄。</summary>
    private async Task WriteAuditAsync(AuditRecord record)
    {
        try
        {
            await _audit.WriteAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "稽核紀錄寫入失敗");
            throw new AuditWriteException(ex);
        }
    }

    /// <summary>
    /// 以 <c>geo_reader_pii</c> 重跑最後一次成功的 <c>query_database</c>，把結果表與該查詢的地圖要素換成原始值。
    /// 沒有成功的查詢就原樣回傳；重跑失敗（資料庫錯誤）時保留遮蔽後的結果並記錄，不讓請求失敗。
    /// </summary>
    /// <param name="result">以 <c>geo_reader</c> 取得的結果（遮蔽值）。</param>
    /// <param name="piiExecute">以 <c>geo_reader_pii</c> 執行 SQL 的委派。</param>
    /// <param name="logger">記錄用；可為 null。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>換成原始值的結果；回答文字與工具呼叫紀錄不變。</returns>
    public static async Task<GeoAgentResult> RevealAsync(GeoAgentResult result,
        Func<string, CancellationToken, Task<SqlQueryResult>> piiExecute, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (result.Query is not { Success: true, Sql: { } sql } query) return result;

        SqlQueryResult raw;
        try
        {
            raw = await piiExecute(sql, cancellationToken);
        }
        catch (SqlException ex)
        {
            logger?.LogWarning(ex, "admin 旁路重跑失敗，改顯示遮蔽後的結果");
            return result;
        }

        // 重建該查詢的地圖要素，放在原本的位置；別的工具加入的要素不動。
        var features = result.Map?.ToList() ?? [];
        var index = result.QueryFeatures is { Count: > 0 } old ? features.FindIndex(old.Contains) : -1;
        if (result.QueryFeatures is { } stale) features.RemoveAll(stale.Contains);
        if (GeoJsonBuilder.Build(raw) is { } rebuilt)
            features.InsertRange(index < 0 ? features.Count : index, rebuilt);

        var map = new NetTopologySuite.Features.FeatureCollection();
        foreach (var f in features) map.Add(f);
        return result with { Query = query with { Data = raw }, Map = map.Count == 0 ? null : map };
    }
}
