using System.Runtime.CompilerServices;
using GeoNl2Sql.Core.Guardrails;
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
/// 在 <see cref="GeoAgent"/> 外面加上角色處理（docs/m4-implementation-plan.md §4.2）。
/// 模型路徑永遠是 <see cref="GeoAgent"/> 內的 <c>geo_reader</c>，所以不論角色，送往模型的內容都不含原始個資；
/// admin 只在請求結束後，以 <c>geo_reader_pii</c> 重跑同一句已通過驗證的 SQL，用結果取代表格與地圖要素，重跑結果不回給模型。
/// 用法：<c>var result = await new QueryService(agent, piiExecutor.ExecuteAsync).RunAsync("列出客戶", UserRole.Admin)</c>。
/// </summary>
public sealed class QueryService
{
    private readonly GeoAgent _agent;
    private readonly Func<string, CancellationToken, Task<SqlQueryResult>> _piiExecute;
    private readonly ILogger? _logger;

    /// <summary>
    /// 建立服務。
    /// </summary>
    /// <param name="agent">Agent（內部只用 <c>geo_reader</c>）。</param>
    /// <param name="piiExecute">以 <c>geo_reader_pii</c> 執行 SQL 的委派；只有 admin 請求會呼叫。</param>
    /// <param name="logger">旁路重跑失敗時記錄用；可省略。</param>
    public QueryService(GeoAgent agent, Func<string, CancellationToken, Task<SqlQueryResult>> piiExecute, ILogger? logger = null)
    {
        _agent = agent;
        _piiExecute = piiExecute;
        _logger = logger;
    }

    /// <summary>
    /// 回答一個問題。
    /// </summary>
    /// <param name="question">使用者的問題。</param>
    /// <param name="role">請求選擇的角色。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>與 <see cref="GeoAgent.RunAsync"/> 相同；admin 的表格與地圖要素換成原始值。</returns>
    public async Task<GeoAgentResult> RunAsync(string question, UserRole role, CancellationToken cancellationToken = default)
    {
        var result = await _agent.RunAsync(question, cancellationToken);
        return role == UserRole.Admin ? await RevealAsync(result, _piiExecute, _logger, cancellationToken) : result;
    }

    /// <summary>
    /// 串流版：事件原樣轉出，只有最後的 <see cref="Completed"/> 依角色換成處理後的結果。
    /// </summary>
    /// <param name="question">使用者的問題。</param>
    /// <param name="role">請求選擇的角色。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    public async IAsyncEnumerable<GeoAgentEvent> RunStreamingAsync(
        string question, UserRole role, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var e in _agent.RunStreamingAsync(question, cancellationToken))
        {
            if (e is Completed c && role == UserRole.Admin)
                yield return new Completed(await RevealAsync(c.Result, _piiExecute, _logger, cancellationToken));
            else
                yield return e;
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
