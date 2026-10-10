using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GeoNl2Sql.Core.Guardrails;
using Microsoft.Data.SqlClient;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// 第二層防線的實測（docs/m2-implementation-plan.md §5.5）：繞過驗證器，把攻擊語料直接交給
/// <see cref="ReadOnlySqlExecutor"/>，證明低權限 login 本身就擋得住；並驗證逾時、列數上限，
/// 以及 30 題標準 SQL 在 <c>geo_reader</c> 下仍可正常執行（權限沒有收過頭）。
/// 需要本機資料庫與 <c>ConnectionStrings:Reader</c>。
/// </summary>
[Trait("Category", "Database")]
public class ReadOnlyBoundaryTests
{
    /// <summary>讀取輸出目錄下 Guardrails 資料夾中的 JSON 檔。</summary>
    /// <param name="fileName">檔名。</param>
    private static List<JsonElement> Load(string fileName) =>
        JsonSerializer.Deserialize<List<JsonElement>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", fileName)))!;

    /// <summary>
    /// B3：60 條攻擊逐條直接送給唯讀執行器。<c>denied</c> 必須收到 SQL Server 的拒絕（不是逾時）；
    /// 其餘類別（<c>read_only</c>、<c>tempdb_only</c>）可成功或失敗，但全部跑完後 6 張表、<c>sys.objects</c> 與 <c>geo_reader</c> 的權限必須完全相同。
    /// </summary>
    [Theory]
    [InlineData("reader")]
    [InlineData("reader_pii")]
    public async Task Attacks_BypassingValidator_AreDeniedAndLeaveDatabaseUnchanged(string login)
    {
        var before = await DbConfig.SnapshotAsync();
        // WAITFOR 類語句會等到逾時，縮短逾時以免整體太慢。M4：geo_reader_pii 也必須同樣唯讀。
        var connectionString = login == "reader" ? DbConfig.Reader : DbConfig.ReaderPii;
        var executor = new ReadOnlySqlExecutor(connectionString, new QueryLimits { TimeoutSeconds = 3, MaxRows = 1000 });
        var problems = new List<string>();

        foreach (var attack in Load("attack-sql.json"))
        {
            var id = attack.GetProperty("id").GetString()!;
            var expect = attack.GetProperty("dbExpect").GetString()!;
            try
            {
                await executor.ExecuteAsync(attack.GetProperty("sql").GetString()!);
                if (expect == "denied") problems.Add($"{id} 預期被資料庫拒絕，卻執行成功");
            }
            catch (SqlException ex)
            {
                if (expect == "denied" && ex.Number == -2) problems.Add($"{id} 預期被拒絕，實際是逾時");
            }
        }

        var after = await DbConfig.SnapshotAsync();
        foreach (var (key, hash) in before)
            if (after[key] != hash) problems.Add($"{key} 在攻擊後發生變化");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>B4：合法但很重的查詢在逾時秒數後收到 SQL Server 逾時（錯誤號碼 -2），且不會拖到數十秒。</summary>
    [Fact]
    public async Task HeavyQuery_TimesOut()
    {
        var executor = new ReadOnlySqlExecutor(DbConfig.Reader, new QueryLimits { TimeoutSeconds = 1, MaxRows = 1000 });
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<SqlException>(() => executor.ExecuteAsync(
            "SELECT COUNT(*) FROM Customer a CROSS JOIN Customer b CROSS JOIN Customer c CROSS JOIN Customer d"));

        Assert.Equal(-2, ex.Number);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"逾時花了 {sw.Elapsed.TotalSeconds:F1} 秒");
    }

    /// <summary>B4：超過 <c>MaxRows</c> 時只回傳 MaxRows 列並標記截斷；剛好等於上限不算截斷。</summary>
    [Fact]
    public async Task RowCap_TruncatesOnlyWhenMoreRowsExist()
    {
        var capped = await new ReadOnlySqlExecutor(DbConfig.Reader, new QueryLimits { MaxRows = 10 })
            .ExecuteAsync("SELECT * FROM Customer");
        var exact = await new ReadOnlySqlExecutor(DbConfig.Reader, new QueryLimits { MaxRows = 5 })
            .ExecuteAsync("SELECT TOP 5 * FROM Customer");

        Assert.Equal(10, capped.Rows.Count);
        Assert.True(capped.Truncated);
        Assert.Equal(5, exact.Rows.Count);
        Assert.False(exact.Truncated);
    }

    /// <summary>geography 欄位讀成原始位元組，不需要 Microsoft.SqlServer.Types。</summary>
    [Fact]
    public async Task GeographyColumn_IsReadAsBytes()
    {
        var result = await new ReadOnlySqlExecutor(DbConfig.Reader, new QueryLimits())
            .ExecuteAsync("SELECT TOP 1 Location FROM dbo.BaseStation");

        Assert.IsType<byte[]>(Assert.Single(result.Rows)[0]);
    }

    /// <summary>30 題標準 SQL 的 Theory 資料。</summary>
    public static IEnumerable<object[]> Gold() =>
        Load("questions.json").Select(e => new object[]
        {
            e.GetProperty("id").GetString()!, e.GetProperty("goldSql").GetString()!, e.GetProperty("ordered").GetBoolean(),
        });

    /// <summary>
    /// B7（M4 改）：標準 SQL 以 <c>geo_reader_pii</c> 執行成功，結果與管理身分執行的相同（證明權限沒有收過頭）。
    /// 以 <c>geo_reader</c> 執行時，唯一例外是 J04：它選了 <c>dbo.Customer.FullName</c>，個資欄位本來就該被拒（錯誤 230）；
    /// 其餘題目與管理身分的結果仍相同。
    /// </summary>
    /// <param name="id">題號。</param>
    /// <param name="sql">標準 SQL。</param>
    /// <param name="ordered">列順序是否為題意的一部分。</param>
    [Theory]
    [MemberData(nameof(Gold))]
    public async Task GoldSql_AsReaders_MatchAdminResult(string id, string sql, bool ordered)
    {
        var limits = new QueryLimits();
        var asAdmin = await new ReadOnlySqlExecutor(DbConfig.Demo, limits).ExecuteAsync(sql);
        var asPii = await new ReadOnlySqlExecutor(DbConfig.ReaderPii, limits).ExecuteAsync(sql);
        Assert.True(Normalize(asPii, ordered).SequenceEqual(Normalize(asAdmin, ordered)), $"{id} geo_reader_pii 的結果與管理身分不同");

        if (id == "J04")
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => new ReadOnlySqlExecutor(DbConfig.Reader, limits).ExecuteAsync(sql));
            Assert.Equal(230, ex.Number);
            return;
        }
        var asReader = await new ReadOnlySqlExecutor(DbConfig.Reader, limits).ExecuteAsync(sql);
        Assert.True(Normalize(asReader, ordered).SequenceEqual(Normalize(asAdmin, ordered)), $"{id} geo_reader 的結果與管理身分不同");
        Assert.NotEmpty(asReader.Columns);
    }

    /// <summary>把結果集轉成可比對的字串列；不在意順序時排序。</summary>
    /// <param name="result">查詢結果。</param>
    /// <param name="ordered">是否保留原順序。</param>
    private static IEnumerable<string> Normalize(SqlQueryResult result, bool ordered)
    {
        var rows = result.Rows.Select(r => string.Join('\u001f', r.Select(c => c switch
        {
            null => "<NULL>",
            byte[] b => Convert.ToHexString(b),
            var v => Convert.ToString(v, CultureInfo.InvariantCulture),
        })));
        return ordered ? rows : rows.Order(StringComparer.Ordinal);
    }
}
