using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using ProjNet.IO.CoordinateSystems;

namespace GeoNl2Sql.Tests.Spatial;

/// <summary>
/// 測試用的投影對照基準：WGS84 經緯度（EPSG:4326）與 TWD97 / TM2 zone 121（EPSG:3826，單位公尺）互轉，使用 ProjNet。
/// 這是驗證 SQL Server 結果用的「獨立計算」，不是產品功能（docs/m3-implementation-plan.md §4.2）。
/// </summary>
internal static class Tm2Projection
{
    /// <summary>EPSG:3826 的 WKT 定義（中央經線 121°、尺度 0.9999、假東距 250000）。</summary>
    private const string Epsg3826Wkt =
        "PROJCS[\"TWD97 / TM2 zone 121\",GEOGCS[\"TWD97\",DATUM[\"Taiwan_Datum_1997\",SPHEROID[\"GRS 1980\",6378137,298.257222101]]," +
        "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Transverse_Mercator\"]," +
        "PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",121],PARAMETER[\"scale_factor\",0.9999]," +
        "PARAMETER[\"false_easting\",250000],PARAMETER[\"false_northing\",0],UNIT[\"metre\",1],AUTHORITY[\"EPSG\",\"3826\"]]";

    private static readonly CoordinateSystem Tm2 = CoordinateSystemWktReader.Parse(Epsg3826Wkt) as CoordinateSystem
        ?? throw new InvalidOperationException("EPSG:3826 定義無法解析");

    private static readonly ICoordinateTransformation Forward =
        new CoordinateTransformationFactory().CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, Tm2);

    private static readonly ICoordinateTransformation Inverse =
        new CoordinateTransformationFactory().CreateFromCoordinateSystems(Tm2, GeographicCoordinateSystem.WGS84);

    /// <summary>經緯度（度）→ 3826 平面座標（公尺）。</summary>
    /// <param name="lon">經度。</param>
    /// <param name="lat">緯度。</param>
    public static Coordinate ToTm2(double lon, double lat)
    {
        var p = Forward.MathTransform.Transform([lon, lat]);
        return new Coordinate(p[0], p[1]);
    }

    /// <summary>3826 平面座標（公尺）→ 經緯度（度）。</summary>
    /// <param name="x">東距（公尺）。</param>
    /// <param name="y">北距（公尺）。</param>
    public static Coordinate ToLonLat(double x, double y)
    {
        var p = Inverse.MathTransform.Transform([x, y]);
        return new Coordinate(p[0], p[1]);
    }

    /// <summary>把幾何的每個座標從經緯度投影到 3826（回傳複本）。</summary>
    /// <param name="g">經緯度座標的幾何。</param>
    public static Geometry ToTm2(Geometry g) => Map(g, c => ToTm2(c.X, c.Y));

    /// <summary>
    /// 對幾何的每個座標套用轉換（回傳複本）。
    /// </summary>
    /// <param name="g">原幾何。</param>
    /// <param name="f">座標轉換函式。</param>
    private static Geometry Map(Geometry g, Func<Coordinate, Coordinate> f)
    {
        var copy = g.Copy();
        copy.Apply(new MapFilter(f));
        copy.GeometryChanged();
        return copy;
    }

    /// <summary>逐座標轉換的篩選器。</summary>
    private sealed class MapFilter(Func<Coordinate, Coordinate> f) : ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            var c = f(new Coordinate(seq.GetX(i), seq.GetY(i)));
            seq.SetX(i, c.X);
            seq.SetY(i, c.Y);
        }
    }
}
