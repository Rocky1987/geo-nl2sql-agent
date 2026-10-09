using GeoNl2Sql.Core.Guardrails;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace GeoNl2Sql.Core.Spatial;

/// <summary>
/// 把查詢結果中的空間欄位組成 GeoJSON <c>FeatureCollection</c>：每個空間值一個 <c>Feature</c>，同一列的其餘欄位成為 <c>properties</c>。
/// GeoJSON 只給地圖前端用，不回給模型（docs/m3-implementation-plan.md §2.3）。
/// </summary>
public static class GeoJsonBuilder
{
    /// <summary>座標四捨五入的小數位數；6 位約 0.1 公尺，遠小於 G3 的 0.5 公尺門檻。</summary>
    private const int CoordinateDecimals = 6;


    /// <summary>
    /// 組出 FeatureCollection。空間值為 NULL 的列不產生 Feature。
    /// 一列有多個空間欄位時，每個空間值各產生一個 Feature，並在 <c>properties</c> 加上 <c>geometryColumn</c> 標明來源欄位。
    /// 非空間的 <c>byte[]</c> 欄位以 <c>"&lt;N bytes&gt;"</c> 表示；重複的欄位名稱加底線與序號區分。
    /// </summary>
    /// <param name="result">唯讀執行器的結果；須帶 <see cref="SqlQueryResult.ColumnTypes"/>。</param>
    /// <returns>FeatureCollection；沒有任何空間欄位（或沒有欄位型別）時為 <c>null</c>，呼叫端就不畫地圖。</returns>
    public static FeatureCollection? Build(SqlQueryResult result)
    {
        if (result.ColumnTypes is null) return null;
        var spatial = Enumerable.Range(0, result.Columns.Count).Where(i => GeographyReader.IsSpatialType(result.ColumnTypes[i])).ToList();
        if (spatial.Count == 0) return null;

        var names = UniqueNames(result.Columns);
        var collection = new FeatureCollection();
        foreach (var row in result.Rows)
        {
            foreach (var col in spatial)
            {
                if (row[col] is not byte[] bytes) continue;
                var attributes = new AttributesTable();
                for (var i = 0; i < names.Count; i++)
                    if (!spatial.Contains(i)) attributes.Add(names[i], row[i] is byte[] b ? $"<{b.Length} bytes>" : row[i]);
                if (spatial.Count > 1) attributes.Add("geometryColumn", names[col]);
                collection.Add(new Feature(Round(GeographyReader.FromBytes(bytes, result.ColumnTypes[col])), attributes));
            }
        }
        return collection;
    }

    /// <summary>
    /// 把 FeatureCollection 序列化成 GeoJSON 文字。
    /// </summary>
    /// <param name="collection">要序列化的 FeatureCollection。</param>
    /// <remarks>
    /// 環方向設為 <see cref="RingOrientationOption.DoNotModify"/>：寫出的是 SQL Server 原樣的方向，
    /// 讓 <see cref="GeoJsonValidator"/> 檢查到的是真實資料，而不是寫出器事後修正的結果。
    /// </remarks>
    public static string Serialize(FeatureCollection collection) =>
        new GeoJsonWriter { RingOrientationOption = RingOrientationOption.DoNotModify }.Write(collection);

    /// <summary>
    /// 複製幾何並把座標四捨五入到 <see cref="CoordinateDecimals"/> 位。
    /// </summary>
    /// <param name="geometry">原幾何（不會被修改）。</param>
    internal static Geometry Round(Geometry geometry)
    {
        var copy = geometry.Copy();
        copy.Apply(new RoundFilter());
        copy.GeometryChanged();
        return copy;
    }

    /// <summary>
    /// 讓欄位名稱不重複（<see cref="AttributesTable"/> 不允許同名）。
    /// </summary>
    /// <param name="columns">原欄位名稱，可能含空字串或重複。</param>
    private static List<string> UniqueNames(IReadOnlyList<string> columns)
    {
        var seen = new HashSet<string>();
        var names = new List<string>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            var name = string.IsNullOrEmpty(columns[i]) ? $"column{i + 1}" : columns[i];
            var candidate = name;
            for (var n = 2; !seen.Add(candidate); n++) candidate = $"{name}_{n}";
            names.Add(candidate);
        }
        return names;
    }

    /// <summary>逐座標四捨五入的篩選器。</summary>
    private sealed class RoundFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            seq.SetX(i, Math.Round(seq.GetX(i), CoordinateDecimals));
            seq.SetY(i, Math.Round(seq.GetY(i), CoordinateDecimals));
        }
    }
}
