using System.Text;
using System.Text.Json;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Web.Controllers;
using GeoNl2Sql.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeoNl2Sql.Tests.Web;

/// <summary>
/// <c>POST /query/stream</c> 的離線測試（以假的串流 Agent 取代模型與資料庫）：事件格式、輸入驗證與失敗路徑不洩漏例外細節。
/// </summary>
public class QueryStreamControllerTests
{
    private static QueryStreamController Controller(AgentStreamRunner run, MemoryStream body)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return new QueryStreamController(run, NullLogger<QueryStreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private static async IAsyncEnumerable<GeoAgentEvent> Events(params GeoAgentEvent[] events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GeoAgentEvent> Throwing()
    {
        await Task.CompletedTask;
        if (DateTime.UtcNow.Year > 0) throw new InvalidOperationException("Server=secret;Password=hunter2");
        yield break;
    }

    private static List<JsonElement> Lines(MemoryStream body) =>
        Encoding.UTF8.GetString(body.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    /// <summary>事件依序輸出為一行一個 JSON：step、answer、result。</summary>
    [Fact]
    public async Task Stream_WritesOneJsonLinePerEvent()
    {
        var body = new MemoryStream();
        var done = new GeoAgentResult("好的", null, [], null, false);
        var controller = Controller((_, _, _) => Events(new ToolStarted("query_database"), new AnswerDelta("好"), new AnswerDelta("的"), new Completed(done)), body);

        await controller.Post(new QueryRequest("問題"), default);

        var lines = Lines(body);
        Assert.Equal(["step", "answer", "answer", "result"], lines.Select(l => l.GetProperty("type").GetString()));
        Assert.Equal("query_database", lines[0].GetProperty("tool").GetString());
        Assert.Equal("好", lines[1].GetProperty("delta").GetString());
        Assert.Equal("好的", lines[3].GetProperty("data").GetProperty("answer").GetString());
    }

    /// <summary>空問題與過長問題回 400，不呼叫 Agent。</summary>
    [Fact]
    public async Task InvalidQuestion_Returns400_WithoutCallingAgent()
    {
        var calls = 0;
        var controller = Controller((_, _, _) => { calls++; return Events(); }, new MemoryStream());

        Assert.IsType<BadRequestObjectResult>(await controller.Post(new QueryRequest("   "), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Post(new QueryRequest(new string('問', QueryController.MaxQuestionLength + 1)), default));
        Assert.Equal(0, calls);
    }

    /// <summary>串流途中丟出例外：輸出一行固定訊息的 error 事件，不含例外文字。</summary>
    [Fact]
    public async Task Exception_WritesFixedErrorEvent_WithoutLeakingDetails()
    {
        var body = new MemoryStream();
        var controller = Controller((_, _, _) => Throwing(), body);

        await controller.Post(new QueryRequest("q"), default);

        var line = Assert.Single(Lines(body));
        Assert.Equal("error", line.GetProperty("type").GetString());
        Assert.Equal(QueryController.ModelErrorMessage, line.GetProperty("message").GetString());
        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(body.ToArray()));
    }
}
