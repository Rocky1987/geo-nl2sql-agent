using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Spatial;
using NetTopologySuite.Geometries;
using Xunit.Abstractions;

namespace GeoNl2Sql.Tests.Spatial;

/// <summary>
/// S2 不需要資料庫的測試（docs/m3-implementation-plan.md §4.3）：4326↔3826 往返（G3）、質心不得冒充的檢查、緩衝區參數拒絕。
/// </summary>
public class SpatialOfflineTests(ITestOutputHelper output)
{
    /// <summary>找到存放 <c>GeoNl2Sql.slnx</c> 的儲存庫根目錄。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GeoNl2Sql.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到儲存庫根目錄");
    }

    /// <summary>涵蓋台灣本島與離島範圍的格點，4326 → 3826 → 4326 → 3826 最大位移（公尺）須 &lt; 0.5。</summary>
    [Fact]
    public void RoundTrip_GridOverTaiwan_IsUnderHalfMeter()
    {
        double max = 0;
        for (var lon = 119.5; lon <= 122.2; lon += 0.1)
            for (var lat = 21.8; lat <= 25.4; lat += 0.1)
            {
                var xy = Tm2Projection.ToTm2(lon, lat);
                var back = Tm2Projection.ToLonLat(xy.X, xy.Y);
                max = Math.Max(max, xy.Distance(Tm2Projection.ToTm2(back.X, back.Y)));
            }
        output.WriteLine($"往返最大位移：{max:E3} 公尺");
        Assert.True(max < 0.5);
    }

    /// <summary>已知點的投影值合理：中央經線上東距等於假東距 250000 公尺。</summary>
    [Fact]
    public void Projection_CentralMeridian_HasFalseEasting()
    {
        Assert.Equal(250000, Tm2Projection.ToTm2(121, 24).X, 3);
    }

    /// <summary>質心必須走 geometry 轉換法：SpatialQueries 的原始碼不得出現外接框中心函式（把「不得冒充」做成可執行的檢查）。</summary>
    [Fact]
    public void SpatialQueriesSource_DoesNotUseEnvelopeCenter()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GeoNl2Sql.Core", "Spatial", "SpatialQueries.cs"));
        Assert.DoesNotContain("EnvelopeCenter", source);
        Assert.Contains("STCentroid", source);
    }

    /// <summary>說明為何要擋：不規則形狀（L 形）的外接框中心與真質心不同。</summary>
    [Fact]
    public void EnvelopeCenter_DiffersFromTrueCentroid_ForIrregularShape()
    {
        var l = new GeometryFactory().CreatePolygon([new(0, 0), new(10, 0), new(10, 2), new(2, 2), new(2, 10), new(0, 10), new(0, 0)]);
        var envelopeCenter = l.EnvelopeInternal.Centre;
        var centroid = l.Centroid.Coordinate;
        output.WriteLine($"外接框中心 {envelopeCenter}，質心 {centroid}");
        Assert.True(envelopeCenter.Distance(centroid) > 1);
    }

    /// <summary>不合法的緩衝區參數在連線之前就被拒絕（連線字串指向不可達的位址，若真的連線會是別種例外）。</summary>
    [Theory]
    [InlineData(91, 121, 500)]
    [InlineData(-91, 121, 500)]
    [InlineData(25, 181, 500)]
    [InlineData(25, -181, 500)]
    [InlineData(25, 121, 0)]
    [InlineData(25, 121, -5)]
    [InlineData(25, 121, 50_001)]
    [InlineData(double.NaN, 121, 500)]
    [InlineData(25, double.PositiveInfinity, 500)]
    [InlineData(25, 121, double.NaN)]
    public async Task BufferAroundPoint_RejectsBadArguments_BeforeConnecting(double lat, double lon, double meters)
    {
        var queries = new SpatialQueries(new ReadOnlySqlExecutor("Server=tcp:127.0.0.1,1;Connect Timeout=1", new QueryLimits()), new SpatialOptions());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.BufferAroundPointAsync(lat, lon, meters));
    }
}
