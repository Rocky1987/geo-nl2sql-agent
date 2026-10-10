using GeoNl2Sql.Core.Guardrails;
using Microsoft.Data.SqlClient;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// M4 S1 的個資邊界實測（docs/m4-implementation-plan.md §3.3、§3.4）：
/// <c>geo_reader</c> 讀不到 <c>dbo.Customer</c> 的四個個資欄位（錯誤 230），只能經 <c>ai.Customer</c> 遮蔽檢視取值；
/// 不加前綴的 <c>Customer</c> 會解析到檢視；<c>geo_reader_pii</c> 讀得到原值。需要本機資料庫與兩個 login。
/// </summary>
[Trait("Category", "Database")]
public class PiiBoundaryTests
{
    /// <summary>SQL Server 的「欄位權限被拒」錯誤號碼。</summary>
    private const int ColumnPermissionDenied = 230;

    private static ReadOnlySqlExecutor Reader => new(DbConfig.Reader, new QueryLimits());
    private static ReadOnlySqlExecutor ReaderPii => new(DbConfig.ReaderPii, new QueryLimits());
    private static ReadOnlySqlExecutor Admin => new(DbConfig.Demo, new QueryLimits { MaxRows = 5000 });

    /// <summary>個資欄位出現在 SELECT、SELECT *、WHERE、ORDER BY、GROUP BY、JOIN 時，<c>geo_reader</c> 一律被資料庫拒絕。</summary>
    /// <param name="sql">以 dbo.Customer 為對象的查詢。</param>
    [Theory]
    [InlineData("SELECT FullName FROM dbo.Customer")]
    [InlineData("SELECT NationalId, Phone, Email FROM dbo.Customer")]
    [InlineData("SELECT * FROM dbo.Customer")]
    [InlineData("SELECT CustomerId FROM dbo.Customer WHERE Phone LIKE '09%'")]
    [InlineData("SELECT CustomerId FROM dbo.Customer ORDER BY Email")]
    [InlineData("SELECT COUNT(*) FROM dbo.Customer GROUP BY FullName")]
    [InlineData("SELECT s.SubscriptionId FROM dbo.Subscription s JOIN dbo.Customer c ON c.NationalId = N'A123456789' AND c.CustomerId = s.CustomerId")]
    [InlineData("SELECT COUNT(*) FROM dbo.Customer WHERE FullName = N'王小明'")]
    [InlineData("SELECT COUNT(*) FROM dbo.Customer")] // COUNT(*) 需要所有欄位的權限；改寫成不加前綴的 Customer（檢視）即可
    public async Task Reader_PiiColumnsOfBaseTable_AreDenied(string sql)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => Reader.ExecuteAsync(sql));

        Assert.Equal(ColumnPermissionDenied, ex.Number);
    }

    /// <summary>沒有碰到個資欄位的 <c>dbo.Customer</c> 查詢照常可用（權限沒有收過頭）。</summary>
    /// <param name="sql">只用非個資欄位的查詢。</param>
    [Theory]
    [InlineData("SELECT COUNT(*) FROM Customer")]
    [InlineData("SELECT CustomerId, DistrictId, flg1 FROM dbo.Customer")]
    [InlineData("SELECT DistrictId, COUNT(*) FROM dbo.Customer GROUP BY DistrictId")]
    public async Task Reader_NonPiiColumnsOfBaseTable_StillWork(string sql)
    {
        var result = await Reader.ExecuteAsync(sql);

        Assert.NotEmpty(result.Rows);
    }

    /// <summary>
    /// 不加前綴的 <c>Customer</c>（預設結構描述 ai）與明確寫 <c>ai.Customer</c> 取得遮蔽值；
    /// 經檢視讀取時擁有權鏈結讓底層欄位的 DENY 不生效。
    /// </summary>
    /// <param name="sql">讀四個個資欄位的查詢。</param>
    [Theory]
    [InlineData("SELECT FullName, NationalId, Phone, Email FROM Customer ORDER BY CustomerId")]
    [InlineData("SELECT FullName, NationalId, Phone, Email FROM ai.Customer ORDER BY CustomerId")]
    [InlineData("SELECT c.FullName, c.NationalId, c.Phone, c.Email FROM Customer c JOIN Subscription s ON s.CustomerId = c.CustomerId ORDER BY c.CustomerId, s.SubscriptionId")]
    public async Task Reader_ViaMaskedView_ReturnsMaskedValues(string sql)
    {
        var masked = await Reader.ExecuteAsync(sql);

        Assert.NotEmpty(masked.Rows);
        foreach (var row in masked.Rows)
        {
            Assert.EndsWith("**", (string)row[0]!);
            Assert.Matches(@"^[A-Z]\*{7}\d{2}$", ((string)row[1]!).Trim());
            Assert.Matches(@"^.{4}-\*\*\*-.{3}$", (string)row[2]!);
            Assert.Matches(@"^.\*\*\*@.+$", (string)row[3]!);
        }
    }

    /// <summary>其餘五張表不加前綴照常解析到 dbo（預設結構描述 ai 不影響它們）。</summary>
    [Fact]
    public async Task Reader_UnqualifiedOtherTables_ResolveToDbo()
    {
        var viaDefault = await Reader.ExecuteAsync("SELECT COUNT(*) FROM District");
        var viaDbo = await Reader.ExecuteAsync("SELECT COUNT(*) FROM dbo.District");

        Assert.Equal(viaDbo.Rows[0][0], viaDefault.Rows[0][0]);
    }

    /// <summary>檢視 1,000 列的四個欄位沒有任何一個等於原值。</summary>
    [Fact]
    public async Task MaskedView_NeverEqualsOriginal_ForAllRows()
    {
        var original = await Admin.ExecuteAsync("SELECT CustomerId, FullName, NationalId, Phone, Email FROM dbo.Customer ORDER BY CustomerId");
        var masked = await Reader.ExecuteAsync("SELECT CustomerId, FullName, NationalId, Phone, Email FROM ai.Customer ORDER BY CustomerId");

        Assert.Equal(1000, original.Rows.Count);
        Assert.Equal(original.Rows.Count, masked.Rows.Count);
        for (var i = 0; i < original.Rows.Count; i++)
        {
            Assert.Equal(original.Rows[i][0], masked.Rows[i][0]);
            for (var c = 1; c <= 4; c++)
                Assert.NotEqual(original.Rows[i][c], masked.Rows[i][c]);
        }
    }

    /// <summary><c>geo_reader_pii</c> 不加前綴的 <c>Customer</c> 解析到原始表，讀得到與管理身分相同的原值。</summary>
    [Fact]
    public async Task ReaderPii_ReadsOriginalValues()
    {
        const string sql = "SELECT CustomerId, FullName, NationalId, Phone, Email FROM Customer ORDER BY CustomerId";
        var pii = await ReaderPii.ExecuteAsync(sql);
        var admin = await Admin.ExecuteAsync(sql);

        Assert.Equal(admin.Rows.Count, pii.Rows.Count);
        for (var i = 0; i < admin.Rows.Count; i++)
            Assert.Equal(admin.Rows[i], pii.Rows[i]);
    }

    /// <summary><c>geo_reader_pii</c> 不能碰遮蔽檢視所在的 ai 結構描述，也不能寫入。</summary>
    /// <param name="sql">應被拒絕的語句。</param>
    [Theory]
    [InlineData("SELECT * FROM ai.Customer")]
    [InlineData("UPDATE dbo.Customer SET FullName = N'x'")]
    [InlineData("DELETE FROM dbo.Customer")]
    public async Task ReaderPii_AiSchemaAndWrites_AreDenied(string sql)
    {
        await Assert.ThrowsAsync<SqlException>(() => ReaderPii.ExecuteAsync(sql));
    }
}
