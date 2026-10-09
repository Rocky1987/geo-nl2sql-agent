using System.Text.Json;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace GeoNl2Sql.Web.Controllers;

/// <summary>
/// <c>POST /query/stream</c>：與 <c>POST /query</c> 相同的問題與驗證，但回應改成逐行輸出的 JSON（NDJSON，每行一個事件），
/// 讓前端能即時顯示進度與逐段的回答文字。事件種類：
/// <c>step</c>（開始呼叫某工具，欄位 <c>tool</c>）、<c>answer</c>（回答文字片段，欄位 <c>delta</c>）、
/// <c>result</c>（最後的完整 <see cref="QueryResponse"/>，欄位 <c>data</c>）、<c>error</c>（失敗，欄位 <c>message</c>，固定訊息）。
/// 開始串流之前就能判斷的錯誤（空問題、過長）仍回 400 與 <see cref="QueryResponse"/>。
/// </summary>
[ApiController]
[Route("query/stream")]
public sealed class QueryStreamController(AgentStreamRunner runner, ILogger<QueryStreamController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 回答一個問題並以串流輸出事件。
    /// </summary>
    /// <param name="request">含 <c>question</c> 的請求內容。</param>
    /// <param name="cancellationToken">取消權杖（瀏覽器中斷連線時觸發）。</param>
    /// <returns>問題不合法時回 400；否則直接寫入回應本體並回傳 null 結果（<see cref="EmptyResult"/>）。</returns>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] QueryRequest request, CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question))
            return BadRequest(Failure("請輸入問題。"));
        if (question.Length > QueryController.MaxQuestionLength)
            return BadRequest(Failure($"問題長度不可超過 {QueryController.MaxQuestionLength} 字元。"));

        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        try
        {
            await foreach (var e in runner(question, cancellationToken))
            {
                object line = e switch
                {
                    ToolStarted t => new { type = "step", tool = t.Name },
                    AnswerDelta d => new { type = "answer", delta = d.Text },
                    Completed c => new { type = "result", data = QueryController.ToResponse(c.Result) },
                    _ => throw new InvalidOperationException("未知的事件。"),
                };
                await WriteLineAsync(line, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 瀏覽器已中斷連線，沒有人在讀回應。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent 串流執行失敗");
            await WriteLineAsync(new { type = "error", message = QueryController.ModelErrorMessage }, CancellationToken.None);
        }

        return new EmptyResult();
    }

    /// <summary>寫出一行 JSON 並立即送出，前端才能即時收到。</summary>
    /// <param name="line">要序列化的事件。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    private async Task WriteLineAsync(object line, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(line, Json) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>失敗回應：沒有資料，只有固定的原因。</summary>
    /// <param name="message">固定的失敗原因。</param>
    private static QueryResponse Failure(string message) =>
        new(false, message, null, [], [], false, null, [], message);
}
