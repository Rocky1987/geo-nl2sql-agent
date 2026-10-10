using System.Text;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Audit;
using GeoNl2Sql.Web.Controllers;
using GeoNl2Sql.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeoNl2Sql.Tests.Web;

/// <summary>
/// 兩個端點與稽核的整合（docs/m4-implementation-plan.md §5.3）：不合法的請求也要先記稽核；
/// 稽核寫不進去時，<c>POST /query</c> 回 500、<c>POST /query/stream</c> 只送 <c>error</c> 事件，兩者都不送出結果，也不洩漏底層細節。
/// </summary>
public class AuditControllerTests
{
    private static readonly Task<GeoAgentResult> Ok = Task.FromResult(new GeoAgentResult("好的", null, [], null, false));

    private static QueryController Query(AgentRunner run, InvalidRequestRecorder record) =>
        new(run, record, NullLogger<QueryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static QueryStreamController Stream(AgentStreamRunner run, InvalidRequestRecorder record, MemoryStream body)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = body;
        return new QueryStreamController(run, record, NullLogger<QueryStreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static async IAsyncEnumerable<GeoAgentEvent> Throwing()
    {
        yield return new AnswerDelta("已經顯示的文字");
        await Task.CompletedTask;
        throw new AuditWriteException(new InvalidOperationException("Server=secret;Password=hunter2"));
    }

    /// <summary>空問題、過長的問題與不合法的角色：都先記稽核（帶原文）再回 400。</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", "analyst")]
    [InlineData("問", "superuser")]
    public async Task InvalidRequests_AreAuditedBeforeReturning400(string? question, string? role)
    {
        var seen = new List<(string?, string?)>();
        InvalidRequestRecorder record = (q, r) => { seen.Add((q, r)); return Task.CompletedTask; };
        var request = new QueryRequest(question, role);

        Assert.IsType<BadRequestObjectResult>(await Query((_, _, _) => Ok, record).Post(request, default));
        Assert.IsType<BadRequestObjectResult>(await Stream((_, _, _) => Throwing(), record, new MemoryStream()).Post(request, default));

        Assert.Equal([(question, role), (question, role)], seen);
    }

    /// <summary>不合法請求的稽核寫入失敗：回 500 與固定訊息，不回 400（沒有紀錄就不回應）。</summary>
    [Fact]
    public async Task InvalidRequest_WhenAuditFails_Returns500()
    {
        InvalidRequestRecorder record = (_, _) => throw new AuditWriteException(new InvalidOperationException("Password=hunter2"));

        var a = Assert.IsType<ObjectResult>(await Query((_, _, _) => Ok, record).Post(new QueryRequest(""), default));
        var b = Assert.IsType<ObjectResult>(await Stream((_, _, _) => Throwing(), record, new MemoryStream()).Post(new QueryRequest(""), default));

        Assert.Equal(500, a.StatusCode);
        Assert.Equal(500, b.StatusCode);
        Assert.Equal(QueryController.AuditFailedMessage, Assert.IsType<QueryResponse>(a.Value).Error);
        Assert.Equal(QueryController.AuditFailedMessage, Assert.IsType<QueryResponse>(b.Value).Error);
    }

    /// <summary><c>POST /query</c>：稽核寫入失敗回 500 與固定訊息，沒有結果內容，也不含例外細節。</summary>
    [Fact]
    public async Task Query_WhenAuditFails_Returns500_WithoutResult()
    {
        var controller = Query((_, _, _) => throw new AuditWriteException(new InvalidOperationException("Password=hunter2")), (_, _) => Task.CompletedTask);

        var result = Assert.IsType<ObjectResult>(await controller.Post(new QueryRequest("q"), default));

        Assert.Equal(500, result.StatusCode);
        var response = Assert.IsType<QueryResponse>(result.Value);
        Assert.False(response.Success);
        Assert.Equal(QueryController.AuditFailedMessage, response.Answer);
        Assert.Empty(response.Rows);
        Assert.Null(response.GeoJson);
        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(response));
    }

    /// <summary><c>POST /query/stream</c>：稽核寫入失敗時，已送出的回答片段之後只跟著 <c>error</c> 事件，沒有 <c>result</c>。</summary>
    [Fact]
    public async Task Stream_WhenAuditFails_SendsErrorAndNoResult()
    {
        var body = new MemoryStream();
        var controller = Stream((_, _, _) => Throwing(), (_, _) => Task.CompletedTask, body);

        await controller.Post(new QueryRequest("q"), default);

        var text = Encoding.UTF8.GetString(body.ToArray());
        var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(["answer", "error"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(QueryController.AuditFailedMessage, events[1].GetProperty("message").GetString());
        Assert.DoesNotContain("hunter2", text);
    }
}
