using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Spatial;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// 空間資料讀取的資料庫測試（docs/m3-implementation-plan.md §3.5）：真實的行政區與基地台經唯讀執行器讀出、
/// 轉成 GeoJSON 後 100% 通過檢查，且位元組路徑與 SQL 端 <c>ToString()</c> 的 WKT 路徑逐座標相同。
/// 需要本機資料庫與 <c>ConnectionStrings:Reader</c>。
/// </summary>
[Trait("Category", "Database")]
public class SpatialReadTests
{
    private static ReadOnlySqlExecutor Executor() => new(DbConfig.Reader, new QueryLimits());

    /// <summary>執行器回報空間欄位的型別為 <c>geography</c>（已去掉資料庫前綴），下游才認得出來。</summary>
    [Fact]
    public async Task Executor_ReportsGeographyColumnType()
    {
        var result = await Executor().ExecuteAsync("SELECT DistrictName, Boundary, Population FROM dbo.District");

        Assert.Equal(["nvarchar", "geography", "int"], result.ColumnTypes);
    }

    /// <summary>全部行政區（多邊形）轉出的 GeoJSON 通過檢查，含外環逆時針，且不依賴寫出器修正方向。</summary>
    [Fact]
    public async Task AllDistricts_ProduceValidGeoJson()
    {
        var result = await Executor().ExecuteAsync("SELECT DistrictId, DistrictName, Boundary FROM dbo.District");

        var collection = GeoJsonBuilder.Build(result)!;

        Assert.Equal(result.Rows.Count, collection.Count);
        Assert.NotEmpty(collection);
        Assert.Empty(GeoJsonValidator.Validate(GeoJsonBuilder.Serialize(collection)));
    }

    /// <summary>全部基地台（點）轉出的 GeoJSON 通過檢查。</summary>
    [Fact]
    public async Task AllBaseStations_ProduceValidGeoJson()
    {
        var result = await Executor().ExecuteAsync("SELECT StationId, StationName, Location FROM dbo.BaseStation");

        var collection = GeoJsonBuilder.Build(result)!;

        Assert.Equal(result.Rows.Count, collection.Count);
        Assert.NotEmpty(collection);
        Assert.Empty(GeoJsonValidator.Validate(GeoJsonBuilder.Serialize(collection)));
    }

    /// <summary>位元組路徑與 <c>ToString()</c> 的 WKT 路徑：每一列的每個座標差距為 0（經度在前）。</summary>
    [Theory]
    [InlineData("District", "Boundary")]
    [InlineData("BaseStation", "Location")]
    public async Task BytesPath_MatchesToStringWktPath(string table, string column)
    {
        var result = await Executor().ExecuteAsync($"SELECT {column}, {column}.ToString() FROM dbo.{table}");

        Assert.NotEmpty(result.Rows);
        foreach (var row in result.Rows)
        {
            var fromBytes = GeographyReader.FromBytes((byte[])row[0]!, "geography");
            var fromWkt = GeographyReader.FromWkt((string)row[1]!);
            Assert.True(fromBytes.EqualsExact(fromWkt, 1e-9), $"{table} 的位元組與 WKT 解析結果不同");
        }
    }
}
