using System.Text.Json;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Web.Controllers;
using GeoNl2Sql.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace GeoNl2Sql.Tests.Web;

/// <summary>
/// <c>POST /query</c> 的離線測試（以假的 Agent 取代模型與資料庫）：回應的欄位契約、問題長度上限、
/// 空間欄位不序列化位元組、GeoJSON 通過檢查，以及失敗路徑不洩漏例外細節。
/// </summary>
public class QueryControllerTests
{
    /// <summary>與 MVC 預設相同的序列化設定，用來確認實際送出的欄位名稱。</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static GeoAgentResult Result(string answer = "好的", FeatureCollection? map = null, Nl2SqlResult? query = null,
        bool hitLimit = false, params string[] tools) =>
        new(answer, map, tools.Select(t => new ToolCall(t, new Dictionary<string, object>())).ToList(), query, hitLimit);

    private static QueryController Controller(Func<string, GeoAgentResult> run, out Func<int> calls)
    {
        var count = 0;
        calls = () => count;
        var controller = new QueryController((q, _, _) => { count++; return Task.FromResult(run(q)); }, (_, _) => Task.CompletedTask, NullLogger<QueryController>.Instance);
        return controller;
    }

    private static FeatureCollection OnePoint() =>
        [new Feature(new Point(121.5, 25.0) { SRID = 4326 }, new AttributesTable { { "kind", "centroid" } })];

    /// <summary>一般查詢：回應含欄位、列、SQL 與工具名稱，序列化後的欄位名稱是契約。</summary>
    [Fact]
    public async Task Query_ReturnsContractFields()
    {
        var data = new SqlQueryResult(["DistrictName", "Cnt"], [["中央區", 3], ["海濱區", 5]], false, ["nvarchar", "int"]);
        var query = new Nl2SqlResult(true, "SELECT 1", data, null, []);
        var controller = Controller(_ => Result("共兩區", null, query, false, "query_database"), out _);

        var ok = Assert.IsType<OkObjectResult>(await controller.Post(new QueryRequest("每區幾座？"), default));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, Web));
        var root = json.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("共兩區", root.GetProperty("answer").GetString());
        Assert.Equal("SELECT 1", root.GetProperty("sql").GetString());
        Assert.Equal(2, root.GetProperty("columns").GetArrayLength());
        Assert.Equal(2, root.GetProperty("rows").GetArrayLength());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("geoJson").ValueKind);
        Assert.Equal("query_database", root.GetProperty("toolsCalled")[0].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    /// <summary>空間欄位不把位元組序列化進 rows；其他位元組欄位寫成「N bytes」。</summary>
    [Fact]
    public async Task Query_SpatialColumn_IsPlaceholder_NotBytes()
    {
        var data = new SqlQueryResult(["Name", "Location", "Blob"], [["站A", new byte[] { 1, 2, 3 }, new byte[] { 9, 9 }]], false,
            ["nvarchar", "geography", "varbinary"]);
        var query = new Nl2SqlResult(true, "SELECT 1", data, null, []);
        var controller = Controller(_ => Result(query: query), out _);

        var ok = Assert.IsType<OkObjectResult>(await controller.Post(new QueryRequest("q"), default));
        var response = Assert.IsType<QueryResponse>(ok.Value);

        Assert.Equal(["站A", QueryController.SpatialPlaceholder, "<2 bytes>"], response.Rows[0]);
    }

    /// <summary>地圖資料轉成 GeoJSON 物件，且通過 G1 的檢查器（端對端版本）。</summary>
    [Fact]
    public async Task Query_GeoJson_PassesValidator()
    {
        var controller = Controller(_ => Result(map: OnePoint(), tools: "get_district_centroid"), out _);

        var ok = Assert.IsType<OkObjectResult>(await controller.Post(new QueryRequest("中央區的質心"), default));
        var response = Assert.IsType<QueryResponse>(ok.Value);

        Assert.NotNull(response.GeoJson);
        Assert.Empty(GeoJsonValidator.Validate(response.GeoJson!.Value.GetRawText()));
    }

    /// <summary>空白問題與超過上限的問題回 400，而且不呼叫 Agent。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyQuestion_Returns400_WithoutCallingAgent(string? question)
    {
        var controller = Controller(_ => Result(), out var calls);

        var bad = Assert.IsType<BadRequestObjectResult>(await controller.Post(new QueryRequest(question), default));

        Assert.False(Assert.IsType<QueryResponse>(bad.Value).Success);
        Assert.Equal(0, calls());
    }

    /// <summary>剛好 500 字元可以通過；501 字元回 400。</summary>
    [Fact]
    public async Task QuestionLength_Boundary()
    {
        var controller = Controller(_ => Result(), out var calls);

        Assert.IsType<OkObjectResult>(await controller.Post(new QueryRequest(new string('問', QueryController.MaxQuestionLength)), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Post(new QueryRequest(new string('問', QueryController.MaxQuestionLength + 1)), default));
        Assert.Equal(1, calls());
    }

    /// <summary>Agent 丟出例外：回 502 與固定訊息，回應中不含例外文字。</summary>
    [Fact]
    public async Task AgentException_Returns502_WithoutLeakingDetails()
    {
        var controller = new QueryController((_, _, _) => throw new InvalidOperationException("Server=secret;Password=hunter2"), (_, _) => Task.CompletedTask,
            NullLogger<QueryController>.Instance);

        var result = Assert.IsType<ObjectResult>(await controller.Post(new QueryRequest("q"), default));

        Assert.Equal(502, result.StatusCode);
        var response = Assert.IsType<QueryResponse>(result.Value);
        Assert.False(response.Success);
        Assert.Equal(QueryController.ModelErrorMessage, response.Error);
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(response, Web));
    }

    /// <summary>超過工具輪數上限與查詢失敗，都是 success=false 加固定訊息。</summary>
    [Fact]
    public async Task HitLimit_And_QueryFailure_AreNotSuccess()
    {
        var failed = new Nl2SqlResult(false, null, null, "內部原因", []);
        var limit = Controller(_ => Result(GeoAgent.LimitMessage, hitLimit: true), out _);
        var broken = Controller(_ => Result(query: failed), out _);

        var a = Assert.IsType<QueryResponse>(Assert.IsType<OkObjectResult>(await limit.Post(new QueryRequest("q"), default)).Value);
        var b = Assert.IsType<QueryResponse>(Assert.IsType<OkObjectResult>(await broken.Post(new QueryRequest("q"), default)).Value);

        Assert.False(a.Success);
        Assert.Equal(GeoAgent.LimitMessage, a.Error);
        Assert.False(b.Success);
        Assert.Equal(QueryController.QueryFailedMessage, b.Error);
        Assert.DoesNotContain("內部原因", JsonSerializer.Serialize(b, Web));
    }
}
