using System.Text.Json;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Spatial;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace GeoNl2Sql.Tests.Spatial;

/// <summary>
/// <see cref="GeoJsonBuilder"/> 與 <see cref="GeoJsonValidator"/> 的離線測試（docs/m3-implementation-plan.md §3.5）：
/// 手寫幾何經 SQL Server 位元組格式往返後組成 GeoJSON 並通過檢查；故意壞掉的 GeoJSON 要被檢查器抓到。
/// </summary>
public class GeoJsonTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    /// <summary>以 SQL Server 的位元組格式寫出幾何（模擬執行器讀到的 geography／geometry 欄位）。</summary>
    private static byte[] ToBytes(Geometry g, bool geography = true) => new SqlServerBytesWriter { IsGeography = geography }.Write(g);

    /// <summary>逆時針（符合 geography 與 RFC 7946 外環方向）的方形。</summary>
    private static Polygon Square(double x, double y, double size) =>
        Factory.CreatePolygon([new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size), new(x, y)]);

    private static SqlQueryResult Result(string[] columns, string[] types, params object?[][] rows) => new(columns, rows, false, types);

    /// <summary>點加上其他欄位：每列一個 Feature，其餘欄位成為 properties，位元組欄位以長度表示。</summary>
    [Fact]
    public void PointRow_BecomesFeatureWithProperties()
    {
        var result = Result(["StationName", "Location", "Raw"], ["nvarchar", "geography", "varbinary"],
            ["BS-1", ToBytes(Factory.CreatePoint(new Coordinate(121.5, 25.05))), new byte[] { 1, 2, 3 }]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        Assert.Empty(GeoJsonValidator.Validate(json));
        using var doc = JsonDocument.Parse(json);
        var feature = doc.RootElement.GetProperty("features")[0];
        Assert.Equal("Point", feature.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(121.5, feature.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal("BS-1", feature.GetProperty("properties").GetProperty("StationName").GetString());
        Assert.Equal("<3 bytes>", feature.GetProperty("properties").GetProperty("Raw").GetString());
    }

    /// <summary>逆時針的多邊形經 geography 位元組往返後，仍通過檢查（外環逆時針）。</summary>
    [Fact]
    public void Polygon_RoundTripsThroughSqlServerBytes_AndPasses()
    {
        var result = Result(["Boundary"], ["geography"], [ToBytes(Square(121.4, 24.9, 0.1))]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        Assert.Empty(GeoJsonValidator.Validate(json));
    }

    /// <summary>geometry 欄位（平面）也能轉換。</summary>
    [Fact]
    public void GeometryColumn_IsSupported()
    {
        var result = Result(["g"], ["geometry"], [ToBytes(Factory.CreatePoint(new Coordinate(10, 20)), geography: false)]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        Assert.Empty(GeoJsonValidator.Validate(json));
    }

    /// <summary>沒有空間欄位、或沒有欄位型別：回傳 null，前端就不畫地圖。</summary>
    [Fact]
    public void NoSpatialColumn_ReturnsNull()
    {
        Assert.Null(GeoJsonBuilder.Build(Result(["n"], ["int"], [1])));
        Assert.Null(GeoJsonBuilder.Build(new SqlQueryResult(["n"], [new object?[] { 1 }], false)));
    }

    /// <summary>空間值為 NULL 的列不產生 Feature；有空間欄位但全為 NULL 時得到空的 FeatureCollection，仍是合法 GeoJSON。</summary>
    [Fact]
    public void NullSpatialValue_IsSkipped()
    {
        var result = Result(["Location"], ["geography"], new object?[] { null }, [ToBytes(Factory.CreatePoint(new Coordinate(121, 25)))]);

        var collection = GeoJsonBuilder.Build(result)!;

        Assert.Single(collection);
        Assert.Empty(GeoJsonValidator.Validate(GeoJsonBuilder.Serialize(collection)));
        Assert.Empty(GeoJsonBuilder.Build(Result(["Location"], ["geography"], new object?[] { null }))!);
    }

    /// <summary>重複或空白的欄位名稱不會讓組裝丟例外。</summary>
    [Fact]
    public void DuplicateAndEmptyColumnNames_AreMadeUnique()
    {
        var result = Result(["", "", "Name", "Name", "g"], ["int", "int", "nvarchar", "nvarchar", "geography"],
            [1, 2, "a", "b", ToBytes(Factory.CreatePoint(new Coordinate(121, 25)))]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        using var props = JsonDocument.Parse(json);
        var p = props.RootElement.GetProperty("features")[0].GetProperty("properties");
        Assert.Equal("a", p.GetProperty("Name").GetString());
        Assert.Equal("b", p.GetProperty("Name_2").GetString());
        Assert.Equal(1, p.GetProperty("column1").GetInt32());
    }

    /// <summary>一列有兩個空間欄位：各自產生 Feature，並以 geometryColumn 標明來源。</summary>
    [Fact]
    public void TwoSpatialColumns_ProduceTwoFeaturesLabelled()
    {
        var p = ToBytes(Factory.CreatePoint(new Coordinate(121, 25)));
        var result = Result(["a", "b"], ["geography", "geography"], [p, p]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        using var doc = JsonDocument.Parse(json);
        var features = doc.RootElement.GetProperty("features");
        Assert.Equal(2, features.GetArrayLength());
        Assert.Equal("b", features[1].GetProperty("properties").GetProperty("geometryColumn").GetString());
    }

    /// <summary>座標四捨五入到小數 6 位。</summary>
    [Fact]
    public void Coordinates_AreRoundedToSixDecimals()
    {
        var result = Result(["g"], ["geography"], [ToBytes(Factory.CreatePoint(new Coordinate(121.123456789, 25.987654321)))]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");
        Assert.Equal(121.123457, c[0].GetDouble());
        Assert.Equal(25.987654, c[1].GetDouble());
    }

    /// <summary>WKT 路徑（工具用）與位元組路徑得到相同座標。</summary>
    [Fact]
    public void Wkt_AndBytesPaths_GiveSameCoordinates()
    {
        var fromBytes = GeographyReader.FromBytes(ToBytes(Square(121.4, 24.9, 0.1)), "geography");
        var fromWkt = GeographyReader.FromWkt("POLYGON ((121.4 24.9, 121.5 24.9, 121.5 25, 121.4 25, 121.4 24.9))");

        Assert.True(fromBytes.EqualsExact(fromWkt, 1e-9));
    }

    // ---- 檢查器的反面測試：每一種壞掉的輸入都要被抓到 ----

    /// <summary>壞掉的 GeoJSON 與預期出現的錯誤片段。</summary>
    public static TheoryData<string, string> BadDocuments => new()
    {
        { """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}""", "首尾" },
        { """{"type":"Polygon","coordinates":[[[0,0],[1,0],[0,0]]]}""", "至少要有 4 個位置" },
        { """{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}""", "外環必須是逆時針" },
        { """{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,10],[0,0]],[[2,2],[4,2],[4,4],[2,4],[2,2]]]}""", "內環必須是順時針" },
        { """{"type":"Point","coordinates":[25.05,121.5]}""", "緯度" },
        { """{"type":"Point","coordinates":[121.5,95]}""", "緯度" },
        { """{"type":"Point","coordinates":[200,25]}""", "經度" },
        { """{"type":"Point","coordinates":[121.5]}""", "2～3 個數字" },
        { """{"type":"Point","coordinates":[[121.5,25]]}""", "位置必須" },
        { """{"type":"LineString","coordinates":[[1,1]]}""", "至少要有 2 個位置" },
        { """{"type":"Polygon","coordinates":[[0,0],[1,0]]}""", "必須是陣列" },
        { """{"type":"Circle","coordinates":[1,1]}""", "不認得" },
        { """{"coordinates":[1,1]}""", "type" },
        { """{"type":"Point"}""", "coordinates" },
        { """{"type":"Feature","geometry":{"type":"Point","coordinates":[1,1]}}""", "properties" },
        { """{"type":"Feature","properties":{}}""", "geometry" },
        { """{"type":"FeatureCollection"}""", "features" },
        { "not json", "不是合法的 JSON" },
    };

    /// <summary>每一種壞輸入都回傳至少一條包含預期片段的錯誤。</summary>
    [Theory]
    [MemberData(nameof(BadDocuments))]
    public void Validator_RejectsBadDocument(string json, string expected)
    {
        var errors = GeoJsonValidator.Validate(json);

        Assert.Contains(errors, e => e.Contains(expected));
    }

    /// <summary>合法的各種幾何（含內環、MultiPolygon、GeometryCollection、null geometry）全部通過，避免檢查器過嚴。</summary>
    [Fact]
    public void Validator_AcceptsValidDocuments()
    {
        string[] good =
        [
            """{"type":"Point","coordinates":[121.5,25.05]}""",
            """{"type":"MultiPoint","coordinates":[[1,1],[2,2]]}""",
            """{"type":"LineString","coordinates":[[1,1],[2,2]]}""",
            """{"type":"MultiLineString","coordinates":[[[1,1],[2,2]],[[3,3],[4,4]]]}""",
            """{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,10],[0,0]],[[2,2],[2,4],[4,4],[4,2],[2,2]]]}""",
            """{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,1],[0,0]]],[[[5,5],[6,5],[6,6],[5,6],[5,5]]]]}""",
            """{"type":"GeometryCollection","geometries":[{"type":"Point","coordinates":[1,1]}]}""",
            """{"type":"Feature","geometry":null,"properties":null}""",
            """{"type":"FeatureCollection","features":[]}""",
        ];
        foreach (var json in good) Assert.Empty(GeoJsonValidator.Validate(json));
    }

    /// <summary>順時針的多邊形經位元組往返後組出 GeoJSON：寫出器不得偷偷改方向，檢查器要看到真實方向。</summary>
    [Fact]
    public void Builder_DoesNotFixRingOrientation()
    {
        // 直接用 WKT 組一個順時針外環（SQL Server 的 geography 不接受，但 geometry 接受）。
        var clockwise = Factory.CreatePolygon([new(0, 0), new(0, 1), new(1, 1), new(1, 0), new(0, 0)]);
        var result = Result(["g"], ["geometry"], [ToBytes(clockwise, geography: false)]);

        var json = GeoJsonBuilder.Serialize(GeoJsonBuilder.Build(result)!);

        Assert.Contains(GeoJsonValidator.Validate(json), e => e.Contains("外環必須是逆時針"));
    }
}
