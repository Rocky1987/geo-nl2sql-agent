using GeoNl2Sql.Core.Guardrails;
using NetTopologySuite.Geometries;

namespace GeoNl2Sql.Core.Spatial;

/// <summary>
/// 空間運算的設定（設定區段 <c>Spatial</c>）。
/// </summary>
public sealed class SpatialOptions
{
    /// <summary>設定區段名稱。</summary>
    public const string SectionName = "Spatial";

    /// <summary>緩衝區半徑上限（公尺）；超過直接拒絕，不送到資料庫。</summary>
    public double MaxBufferMeters { get; set; } = 50_000;

    /// <summary>緩衝區結果最多列出幾座基地台（最近的優先）；總數另外回報。</summary>
    public int MaxListedStations { get; set; } = 20;
}

/// <summary>某行政區的質心。</summary>
/// <param name="DistrictName">行政區名稱（資料庫中的原文）。</param>
/// <param name="Centroid">質心點，座標為 (經度, 緯度)，SRID 4326。</param>
public sealed record DistrictCentroidResult(string DistrictName, Point Centroid);

/// <summary>緩衝區內的一座基地台。</summary>
/// <param name="StationId">基地台編號。</param>
/// <param name="StationName">基地台名稱。</param>
/// <param name="Location">位置，座標為 (經度, 緯度)。</param>
/// <param name="DistanceMeters">與圓心的距離（公尺，測地線）。</param>
public sealed record StationHit(int StationId, string StationName, Point Location, double DistanceMeters);

/// <summary>某點的緩衝區與落在其中的基地台。</summary>
/// <param name="Buffer">緩衝區多邊形，座標為 (經度, 緯度)。</param>
/// <param name="IsValid">SQL Server 的 <c>STIsValid()</c> 結果。</param>
/// <param name="AreaSquareMeters">SQL Server 的 <c>STArea()</c>（平方公尺）。</param>
/// <param name="StationCount">落在緩衝區內的基地台總數。</param>
/// <param name="Stations">最近的前 <see cref="SpatialOptions.MaxListedStations"/> 座。</param>
public sealed record BufferResult(Polygon Buffer, bool IsValid, double AreaSquareMeters, int StationCount, IReadOnlyList<StationHit> Stations);

/// <summary>
/// 確定性的空間運算（質心、緩衝區），供 Agent 工具呼叫。SQL 是固定模板，使用者與模型給的值只能以參數傳入（G6）；
/// 空間欄位在 SQL 端加 <c>.ToString()</c> 取得 WKT，再由 <see cref="GeographyReader.FromWkt"/> 解析（docs/m3-implementation-plan.md §3.1）。
/// 質心一律走 geometry 轉換法，<b>不得</b>以外接框的中心點冒充（可行性報告 §9；有測試掃描本檔）。
/// </summary>
public sealed class SpatialQueries
{
    private readonly ReadOnlySqlExecutor _executor;
    private readonly SpatialOptions _options;

    /// <summary>
    /// 建立實例。
    /// </summary>
    /// <param name="executor">唯讀執行器（<c>geo_reader</c>）。</param>
    /// <param name="options">緩衝區上限等設定。</param>
    public SpatialQueries(ReadOnlySqlExecutor executor, SpatialOptions options)
    {
        _executor = executor;
        _options = options;
    }

    /// <summary>
    /// 查某行政區的質心。SQL 端把 <c>geography</c> 邊界經 WKB 轉成 SRID 4326 的 <c>geometry</c>，以 <c>STCentroid()</c> 計算，再轉回 <c>geography</c>。
    /// <b>這是平面（度數）質心</b>：小範圍行政區誤差可忽略，大範圍或高緯度會有偏差。
    /// </summary>
    /// <param name="districtName">行政區名稱，須與資料庫完全相符；以參數傳入。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>質心；找不到該行政區時為 <c>null</c>。</returns>
    public async Task<DistrictCentroidResult?> DistrictCentroidAsync(string districtName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DistrictName,
                   geography::STGeomFromWKB(
                       geometry::STGeomFromWKB(Boundary.STAsBinary(), 4326).STCentroid().STAsBinary(), 4326).ToString()
            FROM dbo.District
            WHERE DistrictName = @name
            """;
        var result = await _executor.ExecuteAsync(sql, new Dictionary<string, object> { ["@name"] = districtName }, cancellationToken);
        if (result.Rows.Count == 0) return null;
        var row = result.Rows[0];
        return new DistrictCentroidResult((string)row[0]!, (Point)GeographyReader.FromWkt((string)row[1]!));
    }

    /// <summary>
    /// 以某點為圓心、指定半徑（公尺）做緩衝區，並列出落在其中的基地台。
    /// 圓心以 <c>geography::Point(緯度, 經度, 4326)</c> 建立（<b>緯度在前</b>），半徑單位為公尺。
    /// 參數超出範圍時直接丟例外，不連資料庫。
    /// </summary>
    /// <param name="latitude">圓心緯度，−90～90。</param>
    /// <param name="longitude">圓心經度，−180～180。</param>
    /// <param name="meters">半徑（公尺），須大於 0 且不超過 <see cref="SpatialOptions.MaxBufferMeters"/>。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一參數不合法（含 NaN／無限大）。</exception>
    public async Task<BufferResult> BufferAroundPointAsync(double latitude, double longitude, double meters,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "緯度必須在 −90～90 之間");
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "經度必須在 −180～180 之間");
        if (!double.IsFinite(meters) || meters <= 0 || meters > _options.MaxBufferMeters)
            throw new ArgumentOutOfRangeException(nameof(meters), meters, $"半徑必須大於 0 且不超過 {_options.MaxBufferMeters} 公尺");

        var parameters = new Dictionary<string, object> { ["@lat"] = latitude, ["@lon"] = longitude, ["@meters"] = meters };

        const string bufferSql = """
            SELECT b.ToString(), b.STIsValid(), b.STArea()
            FROM (SELECT geography::Point(@lat, @lon, 4326).STBuffer(@meters) AS b) AS x
            """;
        var buffer = (await _executor.ExecuteAsync(bufferSql, parameters, cancellationToken)).Rows[0];

        // COUNT(*) OVER() 在 TOP 之前計算，所以每列帶的都是符合條件的總數。
        const string stationSql = """
            SELECT TOP (@top) StationId, StationName, Location.ToString(),
                   Location.STDistance(geography::Point(@lat, @lon, 4326)), COUNT(*) OVER()
            FROM dbo.BaseStation
            WHERE Location.STIntersects(geography::Point(@lat, @lon, 4326).STBuffer(@meters)) = 1
            ORDER BY Location.STDistance(geography::Point(@lat, @lon, 4326)), StationId
            """;
        parameters["@top"] = _options.MaxListedStations;
        var stations = await _executor.ExecuteAsync(stationSql, parameters, cancellationToken);
        var hits = stations.Rows
            .Select(r => new StationHit((int)r[0]!, (string)r[1]!, (Point)GeographyReader.FromWkt((string)r[2]!), (double)r[3]!))
            .ToList();
        var count = stations.Rows.Count == 0 ? 0 : (int)stations.Rows[0][4]!;

        return new BufferResult((Polygon)GeographyReader.FromWkt((string)buffer[0]!), (bool)buffer[1]!, (double)buffer[2]!, count, hits);
    }
}
