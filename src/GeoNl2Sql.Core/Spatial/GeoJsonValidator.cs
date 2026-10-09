using System.Text.Json;

namespace GeoNl2Sql.Core.Spatial;

/// <summary>
/// GeoJSON 的 RFC 7946 <b>結構檢查</b>（G1，docs/m3-implementation-plan.md §3.4）。
/// 檢查：型別名稱合法、<c>coordinates</c> 巢狀層數正確、經度 −180～180／緯度 −90～90、
/// 線至少 2 個位置、環至少 4 個位置且首尾相同、外環逆時針／內環順時針、
/// <c>Feature</c> 有 <c>geometry</c> 與 <c>properties</c> 成員。
/// 這不是完整的規格符合性測試（例如不檢查環是否自相交），只擋住常見的結構與座標錯誤。
/// </summary>
public static class GeoJsonValidator
{
    /// <summary>簡單幾何的型別名稱與 <c>coordinates</c> 的巢狀深度（Point 為 0 層陣列包住位置）。</summary>
    private static readonly Dictionary<string, int> GeometryDepth = new()
    {
        ["Point"] = 0, ["MultiPoint"] = 1, ["LineString"] = 1, ["MultiLineString"] = 2, ["Polygon"] = 2, ["MultiPolygon"] = 3,
    };

    /// <summary>
    /// 檢查一段 GeoJSON 文字。
    /// </summary>
    /// <param name="json">GeoJSON 文字；根物件可為 FeatureCollection、Feature 或幾何。</param>
    /// <returns>錯誤訊息清單（含 JSON 路徑）；空清單表示通過。</returns>
    public static IReadOnlyList<string> Validate(string json)
    {
        var errors = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            ValidateObject(doc.RootElement, "$", errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"$: 不是合法的 JSON（{ex.Message}）");
        }
        return errors;
    }

    /// <summary>檢查任一 GeoJSON 物件（依 <c>type</c> 分派）。</summary>
    /// <param name="element">要檢查的 JSON 元素。</param>
    /// <param name="path">目前的 JSON 路徑，用於錯誤訊息。</param>
    /// <param name="errors">累積錯誤。</param>
    private static void ValidateObject(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object) { errors.Add($"{path}: 必須是物件"); return; }
        if (!element.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: 缺少字串型別的 type");
            return;
        }
        var type = typeEl.GetString()!;
        switch (type)
        {
            case "FeatureCollection":
                if (!element.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                    errors.Add($"{path}: FeatureCollection 必須有 features 陣列");
                else
                {
                    var i = 0;
                    foreach (var f in features.EnumerateArray()) ValidateObject(f, $"{path}.features[{i++}]", errors);
                }
                break;
            case "Feature":
                if (!element.TryGetProperty("geometry", out var geometry)) errors.Add($"{path}: Feature 缺少 geometry 成員");
                else if (geometry.ValueKind != JsonValueKind.Null) ValidateObject(geometry, $"{path}.geometry", errors);
                if (!element.TryGetProperty("properties", out var props)) errors.Add($"{path}: Feature 缺少 properties 成員");
                else if (props.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) errors.Add($"{path}.properties: 必須是物件或 null");
                break;
            case "GeometryCollection":
                if (!element.TryGetProperty("geometries", out var geoms) || geoms.ValueKind != JsonValueKind.Array)
                    errors.Add($"{path}: GeometryCollection 必須有 geometries 陣列");
                else
                {
                    var i = 0;
                    foreach (var g in geoms.EnumerateArray()) ValidateObject(g, $"{path}.geometries[{i++}]", errors);
                }
                break;
            default:
                if (!GeometryDepth.TryGetValue(type, out var depth)) { errors.Add($"{path}: 不認得的 type「{type}」"); break; }
                if (!element.TryGetProperty("coordinates", out var coords)) { errors.Add($"{path}: 缺少 coordinates"); break; }
                ValidateCoordinates(type, coords, depth, $"{path}.coordinates", errors);
                break;
        }
    }

    /// <summary>
    /// 遞迴檢查 <c>coordinates</c>：先確認巢狀層數，到了最內層檢查位置，再依型別檢查線與環。
    /// </summary>
    /// <param name="type">幾何型別。</param>
    /// <param name="element">目前層級的陣列。</param>
    /// <param name="depth">此層以下還有幾層陣列才是「位置」。</param>
    /// <param name="path">JSON 路徑。</param>
    /// <param name="errors">累積錯誤。</param>
    private static void ValidateCoordinates(string type, JsonElement element, int depth, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Array) { errors.Add($"{path}: 必須是陣列"); return; }
        if (depth == 0) { ValidatePosition(element, path, errors); return; }

        var i = 0;
        var isLineLevel = depth == 1;
        foreach (var child in element.EnumerateArray())
            ValidateCoordinates(type, child, depth - 1, $"{path}[{i++}]", errors);
        if (!isLineLevel) return;

        // 此層是「位置的陣列」：線（LineString）、環（Polygon／MultiPolygon 的環）或 MultiPoint 的點集合。
        var positions = element.EnumerateArray().Where(IsPosition).ToList();
        if (type == "MultiPoint" || positions.Count != element.GetArrayLength()) return;
        if (type is "LineString" or "MultiLineString")
        {
            if (positions.Count < 2) errors.Add($"{path}: 線至少要有 2 個位置");
            return;
        }
        ValidateRing(positions, path, IsExteriorRing(path), errors);
    }

    /// <summary>
    /// 由路徑判斷這個環是不是外環：Polygon 的 <c>coordinates[0]</c>、MultiPolygon 的 <c>coordinates[n][0]</c> 為外環，其餘為內環。
    /// </summary>
    /// <param name="path">環所在的 JSON 路徑（以 <c>[索引]</c> 結尾）。</param>
    private static bool IsExteriorRing(string path) => path.EndsWith("[0]", StringComparison.Ordinal);

    /// <summary>檢查位置：2 或 3 個數字，經度與緯度在範圍內。</summary>
    /// <param name="element">位置陣列。</param>
    /// <param name="path">JSON 路徑。</param>
    /// <param name="errors">累積錯誤。</param>
    private static void ValidatePosition(JsonElement element, string path, List<string> errors)
    {
        if (!IsPosition(element)) { errors.Add($"{path}: 位置必須是 2～3 個數字"); return; }
        var lon = element[0].GetDouble();
        var lat = element[1].GetDouble();
        if (lon is < -180 or > 180) errors.Add($"{path}: 經度 {lon} 超出 −180～180（經緯度是否對調？）");
        if (lat is < -90 or > 90) errors.Add($"{path}: 緯度 {lat} 超出 −90～90（經緯度是否對調？）");
    }

    /// <summary>是否為 2～3 個數字的陣列。</summary>
    /// <param name="element">要檢查的元素。</param>
    private static bool IsPosition(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() is 2 or 3
        && element.EnumerateArray().All(n => n.ValueKind == JsonValueKind.Number);

    /// <summary>檢查環：至少 4 個位置、首尾相同、方向（外環逆時針、內環順時針）。</summary>
    /// <param name="positions">環上的位置。</param>
    /// <param name="path">JSON 路徑。</param>
    /// <param name="exterior">是否為外環。</param>
    /// <param name="errors">累積錯誤。</param>
    private static void ValidateRing(List<JsonElement> positions, string path, bool exterior, List<string> errors)
    {
        if (positions.Count < 4) { errors.Add($"{path}: 環至少要有 4 個位置"); return; }
        var xy = positions.Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble())).ToList();
        if (xy[0] != xy[^1]) { errors.Add($"{path}: 環的首尾位置必須相同"); return; }

        // 鞋帶公式：面積 > 0 為逆時針。
        double area2 = 0;
        for (var i = 0; i < xy.Count - 1; i++) area2 += xy[i].X * xy[i + 1].Y - xy[i + 1].X * xy[i].Y;
        var counterClockwise = area2 > 0;
        if (exterior && !counterClockwise) errors.Add($"{path}: 外環必須是逆時針");
        if (!exterior && counterClockwise) errors.Add($"{path}: 內環必須是順時針");
    }
}
