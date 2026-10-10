using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using Microsoft.Data.SqlClient;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace GeoNl2Sql.Tests.Agent;

/// <summary>
/// admin 旁路的離線測試（docs/m4-implementation-plan.md §4.2、§4.3，以假的執行器取代資料庫）：
/// 表格換成 <c>geo_reader_pii</c> 的原始值、地圖要素同步重建、模型看到的內容（Agent 的結果）不被改動依據、
/// 沒有成功查詢或重跑失敗時不影響請求。analyst 不經過 <see cref="QueryService.RevealAsync"/>，不會呼叫 pii 執行器。
/// </summary>
public class QueryServiceTests
{
    private static readonly SqlQueryResult Masked = new(["CustomerId", "FullName"], [[1, "王**"], [2, "李**"]], false, ["int", "nvarchar"]);
    private static readonly SqlQueryResult Raw = new(["CustomerId", "FullName"], [[1, "王小明"], [2, "李大華"]], false, ["int", "nvarchar"]);

    private static GeoAgentResult Result(Nl2SqlResult? query, FeatureCollection? map = null, IReadOnlyList<IFeature>? queryFeatures = null) =>
        new("回答", map, [], query, false, queryFeatures);

    private static Nl2SqlResult Ok(SqlQueryResult data, string sql = "SELECT CustomerId, FullName FROM Customer") =>
        new(true, sql, data, null, []);

    /// <summary>表格資料換成原始值，重跑用的是模型那次已通過驗證的同一句 SQL；回答文字不變。</summary>
    [Fact]
    public async Task Reveal_ReplacesTableWithRawValues_UsingSameSql()
    {
        string? ranSql = null;

        var revealed = await QueryService.RevealAsync(Result(Ok(Masked)), (sql, _) => { ranSql = sql; return Task.FromResult(Raw); });

        Assert.Equal("SELECT CustomerId, FullName FROM Customer", ranSql);
        Assert.Same(Raw, revealed.Query!.Data);
        Assert.Equal("回答", revealed.Answer);
        Assert.Equal("SELECT CustomerId, FullName FROM Customer", revealed.Query.Sql);
    }

    /// <summary>沒有查詢、查詢失敗：不呼叫 pii 執行器，原樣回傳。</summary>
    [Fact]
    public async Task Reveal_WithoutSuccessfulQuery_DoesNotCallPii()
    {
        var calls = 0;
        Task<SqlQueryResult> Pii(string sql, CancellationToken ct) { calls++; return Task.FromResult(Raw); }
        var none = Result(null);
        var failed = Result(new Nl2SqlResult(false, null, null, "原因", []));

        Assert.Same(none, await QueryService.RevealAsync(none, Pii));
        Assert.Same(failed, await QueryService.RevealAsync(failed, Pii));
        Assert.Equal(0, calls);
    }

    /// <summary>重跑丟資料庫例外：保留遮蔽後的結果，請求不失敗。</summary>
    [Fact]
    public async Task Reveal_WhenPiiRerunFails_KeepsMaskedResult()
    {
        var result = Result(Ok(Masked));

        var revealed = await QueryService.RevealAsync(result, (_, _) => throw Sql());

        Assert.Same(result, revealed);
        Assert.Same(Masked, revealed.Query!.Data);
    }

    /// <summary>該查詢的地圖要素用原始結果重建並留在原位置；其他工具加的要素不動。</summary>
    [Fact]
    public async Task Reveal_RebuildsOnlyTheQueryFeatures()
    {
        var other = Feature("buffer", "x");
        var oldQuery = Feature(null, "王**");
        var map = new FeatureCollection { other, oldQuery };
        var data = new SqlQueryResult(["Name", "Location"], [["王**", new byte[] { 0 }]], false, ["nvarchar", "geography"]);
        var rawData = new SqlQueryResult(["Name", "Location"], [["王小明", Point(121.5, 25.0)]], false, ["nvarchar", "geography"]);

        var revealed = await QueryService.RevealAsync(Result(Ok(data), map, [oldQuery]), (_, _) => Task.FromResult(rawData));

        var features = revealed.Map!.ToList();
        Assert.Equal(2, features.Count);
        Assert.Same(other, features[0]);
        Assert.NotSame(oldQuery, features[1]);
        Assert.Equal("王小明", features[1].Attributes["Name"]);
    }

    private static Feature Feature(string? kind, string name)
    {
        var attributes = new AttributesTable { { "Name", name } };
        if (kind is not null) attributes.Add("kind", kind);
        return new Feature(new Point(121.5, 25.0) { SRID = 4326 }, attributes);
    }

    /// <summary>SQL Server 的 geography 位元組（NetTopologySuite 的 SqlServerBytes 格式）。</summary>
    private static byte[] Point(double x, double y)
    {
        var writer = new NetTopologySuite.IO.SqlServerBytesWriter { IsGeography = true };
        return writer.Write(new Point(x, y) { SRID = 4326 });
    }

    /// <summary>建立一個 <see cref="SqlException"/>（建構式非公開，用反射建立空的實例）。</summary>
    private static SqlException Sql() =>
        (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
}
