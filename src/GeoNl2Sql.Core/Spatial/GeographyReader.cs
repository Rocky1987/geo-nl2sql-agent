using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace GeoNl2Sql.Core.Spatial;

/// <summary>
/// 把 SQL Server 的空間資料轉成 NetTopologySuite 幾何。兩種來源：
/// 模型生成 SQL 的結果由 <see cref="ReadOnlySqlExecutor"/> 讀成 UDT 原始位元組（<see cref="FromBytes"/>）；
/// 固定模板的工具則在 SQL 端對空間欄位加 <c>.ToString()</c>，直接取得 WKT 文字（<see cref="FromWkt"/>）。
/// 兩者座標順序都是 X＝經度、Y＝緯度（docs/m3-implementation-plan.md §3.1，已實測兩條路徑結果逐座標相同）。
/// </summary>
public static class GeographyReader
{
    /// <summary>空間欄位的 SQL Server 型別名稱（對應 <see cref="Guardrails.SqlQueryResult.ColumnTypes"/>）。</summary>
    private static readonly string[] SpatialTypes = ["geography", "geometry"];

    /// <summary>
    /// 判斷某個 SQL Server 型別名稱是否為空間型別。
    /// </summary>
    /// <param name="typeName">小寫型別名稱，例如 <c>geography</c>。</param>
    /// <returns><c>geography</c> 或 <c>geometry</c> 時為 true。</returns>
    public static bool IsSpatialType(string typeName) => SpatialTypes.Contains(typeName);

    /// <summary>
    /// 解析 SQL Server 空間型別的原始位元組。
    /// </summary>
    /// <param name="bytes">UDT 位元組（<c>SqlDataReader.GetSqlBytes</c> 的內容）。</param>
    /// <param name="typeName">欄位型別，<c>geography</c> 或 <c>geometry</c>；用來決定位元組的解讀方式。</param>
    /// <returns>NetTopologySuite 幾何；座標為 (經度, 緯度)。</returns>
    public static Geometry FromBytes(byte[] bytes, string typeName) =>
        new SqlServerBytesReader { IsGeography = typeName == "geography" }.Read(bytes);

    /// <summary>
    /// 解析 WKT 文字（SQL 端 <c>.ToString()</c> 或 <c>STAsText()</c> 的結果）。
    /// </summary>
    /// <param name="wkt">WKT，例如 <c>POINT (121.42 24.94)</c>。</param>
    /// <returns>NetTopologySuite 幾何；座標為 (經度, 緯度)。</returns>
    public static Geometry FromWkt(string wkt) => new WKTReader().Read(wkt);
}
