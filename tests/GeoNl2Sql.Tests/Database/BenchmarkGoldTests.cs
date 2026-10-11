using System.Text.Json;
using GeoNl2Sql.Core.Guardrails;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// M5 的 60 題標準 SQL 在資料庫上的檢查（docs/m5-implementation-plan.md §3.5）：以擁有者身分執行時結果非空、
/// 不超過 200 列（避免比對時被列數上限截斷）；以 <c>geo_reader_pii</c> 執行的結果與擁有者相同（§2.3 的計分以它重跑）。
/// 需要本機資料庫與 <c>ConnectionStrings:ReaderPii</c>。
/// </summary>
[Trait("Category", "Database")]
public class BenchmarkGoldTests
{
    /// <summary>標準 SQL 的 Theory 資料（題號、標準 SQL、ordered）。</summary>
    public static IEnumerable<object[]> Gold() =>
        JsonSerializer.Deserialize<List<JsonElement>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Eval", "benchmark.json")))!
            .Select(e => new object[]
            {
                e.GetProperty("id").GetString()!, e.GetProperty("goldSql").GetString()!, e.GetProperty("ordered").GetBoolean(),
            });

    /// <summary>
    /// 結果非空且不超過 200 列；只有一列一欄的彙總結果（例如 <c>COUNT(*)</c>）不得為 0 或 NULL，否則等於沒有答案。
    /// <c>geo_reader_pii</c> 的結果與擁有者逐列相同。
    /// </summary>
    /// <param name="id">題號。</param>
    /// <param name="sql">標準 SQL。</param>
    /// <param name="ordered">列順序是否為題意的一部分。</param>
    [Theory]
    [MemberData(nameof(Gold))]
    public async Task GoldSql_IsNonEmpty_AtMost200Rows_AndSameAsReaderPii(string id, string sql, bool ordered)
    {
        var limits = new QueryLimits();
        var asOwner = await new ReadOnlySqlExecutor(DbConfig.Demo, limits).ExecuteAsync(sql);
        var asPii = await new ReadOnlySqlExecutor(DbConfig.ReaderPii, limits).ExecuteAsync(sql);

        Assert.NotEmpty(asOwner.Rows);
        Assert.True(asOwner.Rows.Count <= 200, $"{id} 有 {asOwner.Rows.Count} 列");
        if (asOwner.Rows is [[var single]])
            Assert.False(single is null or 0 or 0L or 0m or 0d, $"{id} 的彙總結果為 {single ?? "NULL"}");
        Assert.True(ReadOnlyBoundaryTests.Normalize(asPii, ordered).SequenceEqual(ReadOnlyBoundaryTests.Normalize(asOwner, ordered)),
            $"{id} geo_reader_pii 的結果與擁有者不同");
    }
}
