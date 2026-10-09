using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// G4：空間查詢的估計執行計畫是否使用空間索引（docs/m3-implementation-plan.md §5）。
/// 以擁有者連線取 <c>SHOWPLAN_XML</c>（<c>geo_reader</c> 沒有、也不應為了測試而授予 SHOWPLAN 權限）。
/// 自然計畫沒用索引時，用 <c>WITH (INDEX(…))</c> 提示做<b>探測</b>，證明索引可被使用；提示只出現在這個測試，不進產品的固定模板。
/// 需要本機資料庫。
/// </summary>
[Trait("Category", "Database")]
public class SpatialIndexPlanTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, object> Point = new() { ["@lat"] = 25.0478, ["@lon"] = 121.5170, ["@meters"] = 2000.0, ["@top"] = 20, ["@name"] = "中央區" };

    /// <summary>
    /// 取得估計計畫中用到的索引名稱。<c>SET SHOWPLAN_XML</c> 必須單獨成批，所以前後各用一個命令。
    /// </summary>
    /// <param name="sql">要取計畫的查詢（不會被執行）。</param>
    /// <param name="parameters">查詢參數（以 DECLARE 代入）；可為 <c>null</c>。</param>
    private static async Task<HashSet<string>> IndexesUsedAsync(string sql, Dictionary<string, object>? parameters)
    {
        await using var conn = new SqlConnection(DbConfig.Demo);
        await conn.OpenAsync();
        await using (var on = new SqlCommand("SET SHOWPLAN_XML ON", conn)) await on.ExecuteNonQueryAsync();
        string plan;
        await using (var cmd = new SqlCommand(DeclarePrefix(sql, parameters) + sql, conn)) plan = await ReadPlanAsync(cmd);
        await using (var off = new SqlCommand("SET SHOWPLAN_XML OFF", conn)) await off.ExecuteNonQueryAsync();

        return XDocument.Parse(plan).Descendants()
            .Where(e => e.Name.LocalName == "Object")
            .Select(e => e.Attribute("Index")?.Value)
            .Where(i => i is not null)
            .Select(i => i!.Trim('[', ']'))
            .ToHashSet();
    }

    /// <summary>
    /// 把查詢用到的參數改成同一批次內的 <c>DECLARE</c>（測試用常數），計畫就以實際值編譯，且不經 sp_executesql。
    /// </summary>
    /// <param name="sql">查詢文字。</param>
    /// <param name="parameters">參數名稱對值；可為 <c>null</c>。</param>
    private static string DeclarePrefix(string sql, Dictionary<string, object>? parameters) =>
        parameters is null ? "" : string.Concat(parameters.Where(p => sql.Contains(p.Key)).Select(p => p.Value switch
        {
            double d => $"DECLARE {p.Key} float = {d.ToString("R", CultureInfo.InvariantCulture)}; ",
            int i => $"DECLARE {p.Key} int = {i}; ",
            string t => $"DECLARE {p.Key} nvarchar(100) = N'{t.Replace("'", "''")}'; ",
            _ => throw new NotSupportedException(),
        }));

    /// <summary>
    /// 執行命令並從結果集中取出計畫 XML（以含 <c>ShowPlanXML</c> 的字串辨認）。
    /// </summary>
    /// <param name="cmd">已開啟 SHOWPLAN_XML 的連線上的命令。</param>
    private static async Task<string> ReadPlanAsync(SqlCommand cmd)
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
                if (reader.GetValue(0) is string text && text.Contains("ShowPlanXML")) return text;
        } while (await reader.NextResultAsync());
        throw new InvalidOperationException("結果中沒有計畫 XML");
    }

    /// <summary>
    /// 在查詢中指定資料表後面加上索引提示；表名後若有別名，提示放在別名之後。
    /// </summary>
    /// <param name="sql">原查詢。</param>
    /// <param name="table">資料表（不含 dbo.）。</param>
    /// <param name="index">索引名稱。</param>
    private static string WithIndexHint(string sql, string table, string index) =>
        Regex.Replace(sql, $@"dbo\.{table}(?!\w)(\s+(?<alias>[A-Za-z_]\w*))?", m =>
        {
            var alias = m.Groups["alias"];
            return alias.Success && !Keywords.Contains(alias.Value)
                ? $"{m.Value} WITH (INDEX({index}))"
                : $"dbo.{table} WITH (INDEX({index})){(alias.Success ? " " + alias.Value : "")}";
        });

    /// <summary>表名後面可能出現、但不是別名的關鍵字。</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        { "WHERE", "JOIN", "ON", "ORDER", "GROUP", "INNER", "LEFT", "RIGHT", "CROSS", "UNION" };

    /// <summary>標準集 7 題空間題的標準 SQL（來自 questions.json）。</summary>
    public static TheoryData<string, string> SpatialGoldQueries()
    {
        var data = new TheoryData<string, string>();
        var all = JsonSerializer.Deserialize<List<JsonElement>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", "questions.json")))!;
        foreach (var q in all.Where(q => q.GetProperty("category").GetString() == "spatial"))
            data.Add(q.GetProperty("id").GetString()!, q.GetProperty("goldSql").GetString()!);
        return data;
    }

    /// <summary>可被空間索引加速的查詢（有對常數或另一張表欄位的 STIntersects／STDistance 篩選）：索引必須「可被使用」。</summary>
    private static readonly HashSet<string> IndexApplicable = ["StationSql", "G01", "G03", "G07"];

    /// <summary>
    /// 固定模板的計畫。<c>StationSql</c> 有空間篩選，索引必須可用；<c>CentroidSql</c> 只依名稱查詢，沒有空間篩選，索引不適用，僅記錄。
    /// </summary>
    [Theory]
    [InlineData("StationSql", SpatialQueries.StationSql, "BaseStation", "SIX_BaseStation_Location")]
    [InlineData("CentroidSql", SpatialQueries.CentroidSql, "District", "SIX_District_Boundary")]
    public async Task Templates_Plan(string label, string sql, string table, string index)
    {
        var usable = await Report(label, sql, Point, table, index);
        Assert.Equal(IndexApplicable.Contains(label), usable);
    }

    /// <summary>
    /// 標準集空間題的計畫。G01、G03、G07 有可用索引的篩選；G02（最近鄰缺 <c>IS NOT NULL</c> 條件）、G04／G05（只算面積）、
    /// G06（自我連接的 <c>STDistance</c>）索引不適用——標準 SQL 已凍結，只記錄不改寫。
    /// </summary>
    [Theory]
    [MemberData(nameof(SpatialGoldQueries))]
    public async Task GoldSpatialQueries_Plan(string id, string sql)
    {
        // 標準 SQL 同時可能用到兩張表；兩個索引都探測，任一可用即算。
        var usable = await Report(id, sql, null, "BaseStation", "SIX_BaseStation_Location")
                     | await Report(id, sql, null, "District", "SIX_District_Boundary");
        Assert.Equal(IndexApplicable.Contains(id), usable);
    }

    /// <summary>
    /// 輸出一個查詢對某索引的使用情形。只在查詢涉及該表時才有意義。
    /// </summary>
    /// <param name="label">輸出用的名稱。</param>
    /// <param name="sql">查詢。</param>
    /// <param name="parameters">參數（以 DECLARE 代入）；可為 <c>null</c>。</param>
    /// <param name="table">資料表（不含 dbo.）。</param>
    /// <param name="index">該表的空間索引名稱。</param>
    /// <returns>自然計畫使用，或加提示後可使用時為 true；查詢不涉及該表或索引不適用時為 false。</returns>
    private async Task<bool> Report(string label, string sql, Dictionary<string, object>? parameters, string table, string index)
    {
        if (!sql.Contains($"dbo.{table}")) return false;
        var natural = await IndexesUsedAsync(sql, parameters);
        var usable = natural.Contains(index);
        var line = $"{label} / {index}：自然計畫 {(natural.Contains(index) ? "使用" : "未使用")}（用到：{string.Join(", ", natural.DefaultIfEmpty("無"))}）";
        if (!natural.Contains(index))
        {
            var hinted = WithIndexHint(sql, table, index);
            try
            {
                var probe = await IndexesUsedAsync(hinted, parameters);
                usable = probe.Contains(index);
                line += $"；加提示後 {(usable ? "可使用" : "仍未使用")}";
            }
            catch (SqlException ex)
            {
                line += $"；加提示後無法產生計畫，索引不適用（{ex.Message.ReplaceLineEndings(" ")}）";
            }
        }
        output.WriteLine(line);
        return usable;
    }
}
