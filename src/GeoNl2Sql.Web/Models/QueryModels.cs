using System.Text.Json;
using GeoNl2Sql.Core.Agent;

namespace GeoNl2Sql.Web.Models;

/// <summary>
/// 執行一次 Agent 請求的委派。正式環境由 <c>Program.cs</c> 以 <see cref="GeoAgent"/> 實作；
/// 測試時換成假的實作，就不需要模型與資料庫。
/// </summary>
/// <param name="question">使用者的問題。</param>
/// <param name="role">請求選擇的角色（自選，不是驗證過的身分）。</param>
/// <param name="cancellationToken">取消權杖（請求中斷時觸發）。</param>
public delegate Task<GeoAgentResult> AgentRunner(string question, UserRole role, CancellationToken cancellationToken);

/// <summary>
/// 記錄一個不合法請求（空問題、過長、角色不合法）的稽核委派。正式環境由 <see cref="QueryService.RecordInvalidAsync"/> 實作；
/// 寫入失敗時丟 <see cref="GeoNl2Sql.Core.Audit.AuditWriteException"/>。
/// </summary>
/// <param name="question">請求的問題原文；可為 null。</param>
/// <param name="role">請求的角色原文；可為 null。</param>
public delegate Task InvalidRequestRecorder(string? question, string? role);

/// <summary>
/// 串流版的 <see cref="AgentRunner"/>：依發生順序回傳工具呼叫、回答文字片段與最後的完整結果。
/// </summary>
/// <param name="question">使用者的問題。</param>
/// <param name="role">請求選擇的角色（自選，不是驗證過的身分）。</param>
/// <param name="cancellationToken">取消權杖（請求中斷時觸發）。</param>
public delegate IAsyncEnumerable<GeoAgentEvent> AgentStreamRunner(string question, UserRole role, CancellationToken cancellationToken);

/// <summary><c>POST /query</c> 的請求內容。</summary>
/// <param name="Question">自然語言問題；不可為空，長度上限見 <see cref="Controllers.QueryController.MaxQuestionLength"/>。</param>
/// <param name="Role">角色，<c>analyst</c> 或 <c>admin</c>（不分大小寫）；省略為 analyst，其他值回 400。只讀請求本文，不讀查詢字串或標頭。</param>
public sealed record QueryRequest(string? Question, string? Role = null);

/// <summary>
/// <c>POST /query</c> 的回應。欄位名稱是契約（評估程式也會用），不隨意更動（docs/m3-implementation-plan.md §7.1）。
/// </summary>
/// <param name="Success">是否成功回答；失敗時 <paramref name="Error"/> 有固定的原因。</param>
/// <param name="Answer">給使用者看的回答文字。</param>
/// <param name="Sql">最近一次 <c>query_database</c> 生成並通過驗證的 SQL；沒查過資料庫為 null。</param>
/// <param name="Columns">結果表的欄位名稱。</param>
/// <param name="Rows">結果表的資料列；空間欄位以固定文字代替，不含位元組。</param>
/// <param name="Truncated">結果是否因超過列數上限而被截斷。</param>
/// <param name="GeoJson">地圖用的 GeoJSON FeatureCollection；沒有空間資料為 null。</param>
/// <param name="ToolsCalled">模型依序呼叫過的工具名稱。</param>
/// <param name="Error">失敗原因（固定訊息，不含例外細節或資料庫錯誤原文）；成功為 null。</param>
public sealed record QueryResponse(
    bool Success,
    string Answer,
    string? Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    JsonElement? GeoJson,
    IReadOnlyList<string> ToolsCalled,
    string? Error);
