using System.ComponentModel;
using System.Globalization;
using System.Text;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using NetTopologySuite.Features;

namespace GeoNl2Sql.Core.Agent;

/// <summary>模型在一次請求中呼叫過的一個工具（量測與除錯用）。</summary>
/// <param name="Name">工具名稱。</param>
/// <param name="Arguments">模型給的參數（名稱對值，值為字串或數字）。</param>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object> Arguments);

/// <summary>
/// Agent 的三個工具（docs/m3-implementation-plan.md §6.1）。<b>每次請求建立一個新實例</b>：
/// 它持有該次請求的 <see cref="MapResult"/> 與呼叫紀錄，所以並行請求互不干擾。
/// 工具只接受參數，SQL 是固定模板（G6）；回給模型的是文字摘要，GeoJSON 走 <see cref="MapResult"/> 旁路，不給模型看座標資料。
/// 工具內預期會發生的錯誤（參數不合法、找不到行政區、資料庫錯誤）轉成固定訊息回給模型；
/// 資料庫錯誤只回 <see cref="SqlErrorSanitizer"/> 消毒後的文字，其餘非預期例外照常往外傳。
/// 工具描述是提示詞的一部分，改動要同步更新 <see cref="GeoAgent.PromptVersion"/>。
/// </summary>
public sealed class GeoTools
{
    /// <summary>回給模型的資料列數上限。</summary>
    private const int PreviewRows = 20;

    private readonly Nl2SqlPipeline _pipeline;
    private readonly SpatialQueries _spatial;
    private readonly object _gate = new();
    private readonly List<ToolCall> _calls = [];

    /// <summary>
    /// 建立本次請求的工具集。
    /// </summary>
    /// <param name="pipeline">自然語言轉 SQL 管線（M2，不變）。</param>
    /// <param name="spatial">質心與緩衝區的固定模板查詢。</param>
    /// <param name="map">本次請求的地圖旁路。</param>
    public GeoTools(Nl2SqlPipeline pipeline, SpatialQueries spatial, MapResult map)
    {
        _pipeline = pipeline;
        _spatial = spatial;
        Map = map;
    }

    /// <summary>本次請求的地圖旁路。</summary>
    public MapResult Map { get; }

    /// <summary>最近一次 <c>query_database</c> 的完整結果（含生成的 SQL 與各次嘗試）；沒呼叫過為 <c>null</c>。</summary>
    public Nl2SqlResult? LastQuery { get; private set; }

    /// <summary>模型呼叫過的工具，依呼叫順序。</summary>
    public IReadOnlyList<ToolCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>
    /// 轉成 Agent Framework 可用的工具清單。
    /// </summary>
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create(QueryDatabaseAsync, name: "query_database"),
        AIFunctionFactory.Create(GetDistrictCentroidAsync, name: "get_district_centroid"),
        AIFunctionFactory.Create(BufferAroundPointAsync, name: "buffer_around_point"),
    ];

    /// <summary>以自然語言查詢資料庫；內部由 <see cref="Nl2SqlPipeline"/> 生成、驗證並唯讀執行 SQL。</summary>
    /// <param name="question">完整的自然語言問題，保留使用者原本的條件與數字。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>給模型看的文字摘要：欄位、列數與前 20 列；失敗時為固定的失敗原因。</returns>
    [Description("查詢電信資料庫（行政區、基地台、客戶、資費方案、訂閱、斷訊事件），回傳欄位與前 20 列資料。" +
                 "用於一般的統計、篩選、排序、距離與面積問題。若要求行政區的質心或某點周圍的緩衝區，請改用專用工具。")]
    public async Task<string> QueryDatabaseAsync(
        [Description("完整的自然語言問題，保留使用者原本的條件與數字。")] string question,
        CancellationToken cancellationToken = default)
    {
        Record("query_database", ("question", question));
        var result = await _pipeline.AskAsync(question, cancellationToken);
        LastQuery = result;
        if (!result.Success) return $"查詢失敗：{result.FailureReason}";

        var data = result.Data!;
        if (GeoJsonBuilder.Build(data) is { } features) Map.Add(features);
        return Summarize(data);
    }

    /// <summary>查某行政區的質心。</summary>
    /// <param name="districtName">行政區名稱，須與資料庫中的名稱完全相同。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>質心的經緯度；找不到行政區或資料庫錯誤時為固定訊息。</returns>
    [Description("取得某個行政區的質心（幾何中心）座標。使用者要求「某區的中心點／質心／重心」時使用。" +
                 "這是以度數平面計算的質心，小範圍行政區誤差可忽略。")]
    public async Task<string> GetDistrictCentroidAsync(
        [Description("行政區名稱，須與資料庫中的名稱完全相同，例如「中央區」。")] string districtName,
        CancellationToken cancellationToken = default)
    {
        Record("get_district_centroid", ("districtName", districtName));
        try
        {
            var centroid = await _spatial.DistrictCentroidAsync(districtName, cancellationToken);
            if (centroid is null) return $"找不到名為「{districtName}」的行政區。";

            Map.Add([new Feature(GeoJsonBuilder.Round(centroid.Centroid),
                new AttributesTable { { "kind", "centroid" }, { "DistrictName", centroid.DistrictName } })]);
            return $"行政區「{centroid.DistrictName}」的質心：緯度 {Format(centroid.Centroid.Y)}，經度 {Format(centroid.Centroid.X)}（平面質心）。";
        }
        catch (SqlException ex)
        {
            return $"資料庫查詢失敗：{SqlErrorSanitizer.Sanitize(ex.Number)}";
        }
    }

    /// <summary>以某點為圓心做緩衝區並找出範圍內的基地台。</summary>
    /// <param name="latitude">圓心緯度。</param>
    /// <param name="longitude">圓心經度。</param>
    /// <param name="radiusMeters">半徑（公尺）。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>範圍內的基地台數量與最近的幾座；參數不合法或資料庫錯誤時為固定訊息。</returns>
    [Description("以指定座標為圓心、指定半徑（公尺）畫出圓形範圍，回傳範圍內的基地台數量與最近的幾座。" +
                 "使用者問「某座標／某地點周圍 N 公尺（公里請換算成公尺）內有哪些基地台」且要在地圖上看到範圍時使用。")]
    public async Task<string> BufferAroundPointAsync(
        [Description("圓心緯度（度，−90～90）。緯度在前。")] double latitude,
        [Description("圓心經度（度，−180～180）。")] double longitude,
        [Description("半徑，單位是公尺（1 公里＝1000 公尺）。")] double radiusMeters,
        CancellationToken cancellationToken = default)
    {
        Record("buffer_around_point", ("latitude", latitude), ("longitude", longitude), ("radiusMeters", radiusMeters));
        try
        {
            var result = await _spatial.BufferAroundPointAsync(latitude, longitude, radiusMeters, cancellationToken);

            var features = new List<IFeature>
            {
                new Feature(GeoJsonBuilder.Round(result.Buffer),
                    new AttributesTable { { "kind", "buffer" }, { "radiusMeters", radiusMeters } }),
            };
            features.AddRange(result.Stations.Select(s => new Feature(GeoJsonBuilder.Round(s.Location),
                new AttributesTable { { "kind", "station" }, { "StationName", s.StationName }, { "distanceMeters", Math.Round(s.DistanceMeters) } })));
            Map.Add(features);

            var text = new StringBuilder($"圓心（緯度 {Format(latitude)}，經度 {Format(longitude)}）半徑 {radiusMeters:0.##} 公尺的範圍內共有 {result.StationCount} 座基地台。");
            if (result.Stations.Count > 0)
                text.Append($"最近的 {result.Stations.Count} 座：")
                    .Append(string.Join("、", result.Stations.Select(s => $"{s.StationName}（{s.DistanceMeters:0} 公尺）")))
                    .Append('。');
            return text.ToString();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return $"參數不合法：{ex.Message.ReplaceLineEndings(" ")}";
        }
        catch (SqlException ex)
        {
            return $"資料庫查詢失敗：{SqlErrorSanitizer.Sanitize(ex.Number)}";
        }
    }

    /// <summary>記錄一次工具呼叫。</summary>
    /// <param name="name">工具名稱。</param>
    /// <param name="arguments">參數名稱與值。</param>
    private void Record(string name, params (string Name, object Value)[] arguments)
    {
        lock (_gate) _calls.Add(new ToolCall(name, arguments.ToDictionary(a => a.Name, a => a.Value)));
    }

    /// <summary>經緯度以 6 位小數、不受地區設定影響地格式化。</summary>
    /// <param name="value">度數。</param>
    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// 把查詢結果整理成給模型的文字：欄位、列數與前 <see cref="PreviewRows"/> 列。空間欄位只標示型別，不給座標。
    /// </summary>
    /// <param name="data">查詢結果。</param>
    private static string Summarize(SqlQueryResult data)
    {
        var text = new StringBuilder($"查詢成功，共 {data.Rows.Count} 列{(data.Truncated ? "（結果過多，已截斷）" : "")}。欄位：{string.Join("、", data.Columns)}。");
        if (data.Rows.Count == 0) return text.ToString();
        text.Append($"前 {Math.Min(PreviewRows, data.Rows.Count)} 列：");
        foreach (var row in data.Rows.Take(PreviewRows))
            text.Append('\n').Append(string.Join(" | ", row.Select(v => v switch
            {
                null => "NULL",
                byte[] => "<空間資料，已顯示在地圖上>",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            })));
        return text.ToString();
    }
}
