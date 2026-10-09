using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Tests.Spatial;
using NetTopologySuite.Geometries;
using Xunit.Abstractions;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// 質心（G2）、緩衝區與投影往返（G3）的資料庫測試（docs/m3-implementation-plan.md §4.3）。
/// 對照基準是 NetTopologySuite 在 EPSG:3826 平面上的獨立計算。需要本機資料庫與 <c>ConnectionStrings:Reader</c>。
/// </summary>
[Trait("Category", "Database")]
public class SpatialQueriesTests(ITestOutputHelper output)
{
    private static ReadOnlySqlExecutor Executor() => new(DbConfig.Reader, new QueryLimits());

    private static SpatialQueries Queries() => new(Executor(), new SpatialOptions());

    /// <summary>
    /// G2：每個行政區，SQL 端質心與「邊界投影到 3826 後用 NTS 算質心」的距離，除以該行政區外接框對角線（3826）須 &lt; 0.1%。
    /// 同時印出絕對距離（公尺），因為小分母的相對誤差容易誤導。
    /// </summary>
    [Fact]
    public async Task DistrictCentroids_MatchProjectedReference_Under0Point1Percent()
    {
        var districts = await Executor().ExecuteAsync("SELECT DistrictName, Boundary.ToString() FROM dbo.District ORDER BY DistrictId");
        Assert.NotEmpty(districts.Rows);

        foreach (var row in districts.Rows)
        {
            var name = (string)row[0]!;
            var projected = Tm2Projection.ToTm2(GeographyReader.FromWkt((string)row[1]!));
            var reference = projected.Centroid.Coordinate;
            var diagonal = projected.EnvelopeInternal.Diameter;

            var sql = await Queries().DistrictCentroidAsync(name);
            Assert.NotNull(sql);
            var meters = Tm2Projection.ToTm2(sql.Centroid.X, sql.Centroid.Y).Distance(reference);
            var ratio = meters / diagonal;
            output.WriteLine($"{name}: 差 {meters:F2} m，對角線 {diagonal:F0} m，相對 {ratio:P4}");
            Assert.True(ratio < 0.001, $"{name} 的質心誤差 {ratio:P4} 超過 0.1%");
        }
    }

    /// <summary>找不到的行政區回傳 null；含 SQL 特殊字元的名稱只是找不到，不會被當成 SQL（參數化）。</summary>
    [Theory]
    [InlineData("不存在的區")]
    [InlineData("'; DROP TABLE dbo.District; --")]
    public async Task UnknownDistrict_ReturnsNull(string name)
    {
        Assert.Null(await Queries().DistrictCentroidAsync(name));
    }

    /// <summary>
    /// 緩衝區：有效、面積與理論 πr² 及 NTS 在 3826 的同半徑緩衝接近，列出的基地台都在半徑內且由近到遠。
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(10000)]
    public async Task Buffer_IsValid_AndAreaMatchesReferences(double meters)
    {
        const double lat = 25.0478, lon = 121.5170;

        var result = await Queries().BufferAroundPointAsync(lat, lon, meters);

        var theoretical = Math.PI * meters * meters;
        var ntsArea = new GeometryFactory().CreatePoint(Tm2Projection.ToTm2(lon, lat)).Buffer(meters, 64).Area;
        output.WriteLine($"r={meters}: SQL {result.AreaSquareMeters:F0}，πr² {theoretical:F0}（{result.AreaSquareMeters / theoretical - 1:P3}），NTS {ntsArea:F0}（{result.AreaSquareMeters / ntsArea - 1:P3}），基地台 {result.StationCount}");
        Assert.True(result.IsValid);
        Assert.True(Math.Abs(result.AreaSquareMeters / theoretical - 1) < 0.002, "與 πr² 差距超過 0.2%");
        Assert.True(Math.Abs(result.AreaSquareMeters / ntsArea - 1) < 0.002, "與 NTS 投影緩衝差距超過 0.2%");
        Assert.True(result.Stations.Count <= Math.Min(result.StationCount, 20));
        Assert.All(result.Stations, s => Assert.True(s.DistanceMeters <= meters * 1.01));
        Assert.Equal(result.Stations.OrderBy(s => s.DistanceMeters).Select(s => s.StationId), result.Stations.Select(s => s.StationId));
    }

    /// <summary>大半徑時總數大於列出數量：總數照實回報，列出的只有前 N 座。</summary>
    [Fact]
    public async Task Buffer_LargeRadius_ReportsTotalButListsOnlyTopN()
    {
        var result = await Queries().BufferAroundPointAsync(25.0478, 121.5170, 50_000);

        Assert.True(result.StationCount > 20);
        Assert.Equal(20, result.Stations.Count);
    }

    /// <summary>圓心附近沒有基地台時，總數為 0、清單為空，不是錯誤。</summary>
    [Fact]
    public async Task Buffer_WithNoStations_ReturnsZero()
    {
        var result = await Queries().BufferAroundPointAsync(0, 0, 500);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.StationCount);
        Assert.Empty(result.Stations);
    }

    /// <summary>G3：全部基地台點位 4326 → 3826 → 4326，最大位移（以 3826 公尺計）須 &lt; 0.5 公尺。</summary>
    [Fact]
    public async Task AllStations_ProjectionRoundTrip_IsUnderHalfMeter()
    {
        var stations = await Executor().ExecuteAsync("SELECT Location.ToString() FROM dbo.BaseStation");
        Assert.NotEmpty(stations.Rows);

        double max = 0;
        foreach (var row in stations.Rows)
        {
            var p = (Point)GeographyReader.FromWkt((string)row[0]!);
            var xy = Tm2Projection.ToTm2(p.X, p.Y);
            var back = Tm2Projection.ToLonLat(xy.X, xy.Y);
            max = Math.Max(max, xy.Distance(Tm2Projection.ToTm2(back.X, back.Y)));
        }
        output.WriteLine($"{stations.Rows.Count} 座基地台往返最大位移：{max:E3} 公尺");
        Assert.True(max < 0.5);
    }
}
