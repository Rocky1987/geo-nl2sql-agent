using System.Text.Json;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace GeoNl2Sql.Web.Controllers;

/// <summary>
/// <c>POST /query</c>：收一個自然語言問題，交給 Agent，回傳回答、SQL、結果表與地圖用的 GeoJSON。
/// 失敗時只回固定訊息，不回堆疊追蹤或資料庫錯誤原文（例外細節只寫入伺服器日誌）。
/// M3 沒有使用者驗證、角色與稽核，僅供本機展示（docs/m3-implementation-plan.md §7.3）。
/// </summary>
[ApiController]
[Route("query")]
public sealed class QueryController(AgentRunner runner, ILogger<QueryController> logger) : ControllerBase
{
    /// <summary>問題長度上限（字元）；超過回 400。</summary>
    public const int MaxQuestionLength = 500;

    /// <summary>模型服務出錯時回給前端的固定訊息。</summary>
    public const string ModelErrorMessage = "模型服務暫時無法使用，請稍後再試。";

    /// <summary>資料庫查詢失敗時回給前端的固定訊息。</summary>
    public const string QueryFailedMessage = "查詢沒有成功，請換個說法再試。";

    /// <summary>空間欄位在結果表中顯示的固定文字（座標資料走 <c>geoJson</c>）。</summary>
    public const string SpatialPlaceholder = "<空間資料，已顯示在地圖上>";

    /// <summary>角色不合法時回給前端的固定訊息。</summary>
    public const string InvalidRoleMessage = "角色只能是 analyst 或 admin。";

    /// <summary>
    /// 解析請求的角色：省略為 <see cref="UserRole.Analyst"/>；只接受 <c>analyst</c>／<c>admin</c>（不分大小寫），
    /// 其他值（含數字）一律不合法，不依賴列舉的字串轉換。
    /// </summary>
    /// <param name="text">請求本文的 <c>role</c>。</param>
    /// <param name="role">解析結果。</param>
    /// <returns>合法為 true。</returns>
    public static bool TryParseRole(string? text, out UserRole role)
    {
        role = UserRole.Analyst;
        if (text is null) return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "analyst": return true;
            case "admin": role = UserRole.Admin; return true;
            default: return false;
        }
    }

    /// <summary>
    /// 回答一個問題。
    /// </summary>
    /// <param name="request">含 <c>question</c>、選用 <c>role</c> 的請求內容。</param>
    /// <param name="cancellationToken">取消權杖（瀏覽器中斷連線時觸發）。</param>
    /// <returns>200 與 <see cref="QueryResponse"/>；問題為空、過長或角色不合法回 400；模型服務出錯回 502（內容同樣是 <see cref="QueryResponse"/>）。</returns>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question))
            return BadRequest(Failure("請輸入問題。"));
        if (question.Length > MaxQuestionLength)
            return BadRequest(Failure($"問題長度不可超過 {MaxQuestionLength} 字元。"));

        if (!TryParseRole(request.Role, out var role))
            return BadRequest(Failure(InvalidRoleMessage));

        GeoAgentResult result;
        try
        {
            result = await runner(question, role, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent 執行失敗");
            return StatusCode(StatusCodes.Status502BadGateway, Failure(ModelErrorMessage));
        }

        return Ok(ToResponse(result));
    }

    /// <summary>
    /// 把 Agent 的結果整理成回應：空間欄位不序列化位元組，地圖資料轉成 JSON 物件。
    /// </summary>
    /// <param name="result">Agent 的結果。</param>
    /// <returns>回應內容。</returns>
    public static QueryResponse ToResponse(GeoAgentResult result)
    {
        var data = result.Query?.Data;
        var columns = data?.Columns ?? [];
        var rows = data is null ? [] : data.Rows
            .Select(row => (IReadOnlyList<object?>)row.Select((value, i) => ToCell(value, data.ColumnTypes?[i])).ToList()).ToList();
        JsonElement? geoJson = result.Map is { Count: > 0 } map ? JsonDocument.Parse(GeoJsonBuilder.Serialize(map)).RootElement.Clone() : null;

        var error = result.HitLimit ? GeoAgent.LimitMessage
            : result.Query is { Success: false } ? QueryFailedMessage
            : null;
        return new QueryResponse(error is null, result.Answer, result.Query?.Sql, columns, rows,
            data?.Truncated ?? false, geoJson, result.ToolCalls.Select(c => c.Name).ToList(), error);
    }

    /// <summary>
    /// 把一個儲存格轉成可序列化的值：空間欄位改成固定文字，其他位元組欄位寫成 <c>"&lt;N bytes&gt;"</c>，其餘照原值。
    /// </summary>
    /// <param name="value">儲存格的值。</param>
    /// <param name="typeName">欄位型別（小寫）；未知為 null。</param>
    private static object? ToCell(object? value, string? typeName) => value switch
    {
        byte[] when typeName is not null && GeographyReader.IsSpatialType(typeName) => SpatialPlaceholder,
        byte[] bytes => $"<{bytes.Length} bytes>",
        _ => value,
    };

    /// <summary>失敗回應（驗證不通過或例外）：沒有資料，只有固定的原因。</summary>
    /// <param name="message">固定的失敗原因。</param>
    private static QueryResponse Failure(string message) =>
        new(false, message, null, [], [], false, null, [], message);
}
