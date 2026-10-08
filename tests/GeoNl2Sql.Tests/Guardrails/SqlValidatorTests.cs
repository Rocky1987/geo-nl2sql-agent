using System.Text.Json;
using GeoNl2Sql.Core.Guardrails;

namespace GeoNl2Sql.Tests.Guardrails;

/// <summary>
/// 驗證器的完成條件（docs/m2-implementation-plan.md §4.3）：60 條攻擊全部拒絕（B2），
/// 30 題標準 SQL 與正常查詢集全部放行（B7）。逐筆列出，失敗時能看到是哪一條。
/// </summary>
public class SqlValidatorTests
{
    /// <summary>資料庫中允許查詢的 6 張表。</summary>
    private static readonly SqlValidator Validator = new(new HashSet<string>
    {
        "District", "ServicePlan", "BaseStation", "Customer", "Subscription", "OutageEvent",
    });

    /// <summary>讀取輸出目錄下 Guardrails 資料夾中的 JSON 檔。</summary>
    /// <param name="fileName">檔名。</param>
    private static List<JsonElement> Load(string fileName) =>
        JsonSerializer.Deserialize<List<JsonElement>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", fileName)))!;

    /// <summary>攻擊語料庫的資料列，供 Theory 逐筆列出。</summary>
    public static IEnumerable<object[]> Attacks() =>
        Load("attack-sql.json").Select(e => new object[] { e.GetProperty("id").GetString()!, e.GetProperty("sql").GetString()! });

    /// <summary>正常查詢集的資料列。</summary>
    public static IEnumerable<object[]> Benign() =>
        Load("benign-sql.json").Select(e => new object[] { e.GetProperty("id").GetString()!, e.GetProperty("sql").GetString()! });

    /// <summary>30 題標準 SQL 的資料列。</summary>
    public static IEnumerable<object[]> Gold() =>
        Load("questions.json").Select(e => new object[] { e.GetProperty("id").GetString()!, e.GetProperty("goldSql").GetString()! });

    /// <summary>每條攻擊都必須被拒絕，且附有原因。</summary>
    /// <param name="id">攻擊編號。</param>
    /// <param name="sql">攻擊 SQL。</param>
    [Theory]
    [MemberData(nameof(Attacks))]
    public void Attack_IsRejected(string id, string sql)
    {
        var result = Validator.Validate(sql);

        Assert.False(result.IsValid, $"{id} 未被擋下：{sql}");
        Assert.False(string.IsNullOrWhiteSpace(result.Reason), id);
    }

    /// <summary>看似可疑的合法查詢必須放行。</summary>
    /// <param name="id">編號。</param>
    /// <param name="sql">查詢。</param>
    [Theory]
    [MemberData(nameof(Benign))]
    public void Benign_IsAccepted(string id, string sql)
    {
        var result = Validator.Validate(sql);

        Assert.True(result.IsValid, $"{id} 被誤擋（{result.Reason}）：{sql}");
    }

    /// <summary>語法完全合法、只違反單一規則的 SQL：證明規則本身有作用，而不是靠解析失敗擋下。</summary>
    /// <param name="sql">只違反一條規則的 SQL。</param>
    /// <param name="reason">預期的拒絕原因。</param>
    [Theory]
    [InlineData("SELECT CHAR(65) FROM dbo.Customer", "不允許的函式")]
    [InlineData("SELECT dbo.MyFunc(1) FROM dbo.Customer", "不允許的函式")]
    [InlineData("SELECT * FROM dbo.Customer WHERE CustomerId IN (SELECT CustomerId FROM sys.tables)", "不允許的資料表或來源")]
    [InlineData("SELECT * FROM OtherDb.dbo.Customer", "不允許的資料表或來源")]
    [InlineData("SELECT * FROM sys.Customer", "不允許的資料表或來源")]
    [InlineData("SELECT * FROM OPENJSON(N'[]')", "不允許的資料表或來源")]
    [InlineData("SELECT * INTO dbo.Leak FROM dbo.Customer", "不允許 SELECT INTO")]
    [InlineData("DELETE FROM dbo.Customer", "只允許單一 SELECT 語句")]
    public void SingleRuleViolation_IsRejectedWithExpectedReason(string sql, string reason)
    {
        var result = Validator.Validate(sql);

        Assert.False(result.IsValid);
        Assert.Equal(reason, result.Reason);
    }

    /// <summary>30 題標準 SQL 必須放行。</summary>
    /// <param name="id">題號。</param>
    /// <param name="sql">標準 SQL。</param>
    [Theory]
    [MemberData(nameof(Gold))]
    public void GoldSql_IsAccepted(string id, string sql)
    {
        var result = Validator.Validate(sql);

        Assert.True(result.IsValid, $"{id} 被誤擋（{result.Reason}）：{sql}");
    }
}
