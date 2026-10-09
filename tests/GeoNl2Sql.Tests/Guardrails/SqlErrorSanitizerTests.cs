using GeoNl2Sql.Core.Guardrails;

namespace GeoNl2Sql.Tests.Guardrails;

/// <summary>
/// <see cref="SqlErrorSanitizer"/>（B4）：訊息只由錯誤號碼決定，不含資料表名稱，未列出的號碼只帶號碼。
/// 離線執行（<c>SqlException</c> 無法自行建構，所以消毒器只收號碼）。
/// </summary>
public class SqlErrorSanitizerTests
{
    /// <summary>資料庫中的 6 張表；任何訊息都不得出現這些名稱。</summary>
    private static readonly string[] TableNames = ["District", "ServicePlan", "BaseStation", "Customer", "Subscription", "OutageEvent"];

    /// <summary>已列出的錯誤號碼。</summary>
    public static IEnumerable<object[]> KnownNumbers() =>
        new[] { 207, 208, 4104, 102, 156, 195, 4121, 8120, 229, 230, 262, -2 }.Select(n => new object[] { n });

    /// <summary>每個已列出的號碼都有固定訊息，不含任何資料表名稱，也不是通用的後備訊息。</summary>
    /// <param name="number">錯誤號碼。</param>
    [Theory]
    [MemberData(nameof(KnownNumbers))]
    public void KnownNumber_HasFixedMessageWithoutTableNames(int number)
    {
        var message = SqlErrorSanitizer.Sanitize(number);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("錯誤碼", message);
        Assert.All(TableNames, t => Assert.DoesNotContain(t, message, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>同一號碼永遠得到相同訊息；同一組號碼共用同一則訊息。</summary>
    [Fact]
    public void SameNumber_AlwaysSameMessage_AndGroupedNumbersShare()
    {
        Assert.Equal(SqlErrorSanitizer.Sanitize(207), SqlErrorSanitizer.Sanitize(207));
        Assert.Equal(SqlErrorSanitizer.Sanitize(102), SqlErrorSanitizer.Sanitize(156));
        Assert.Equal(SqlErrorSanitizer.Sanitize(195), SqlErrorSanitizer.Sanitize(4121));
        Assert.Equal(SqlErrorSanitizer.Sanitize(229), SqlErrorSanitizer.Sanitize(262));
        Assert.NotEqual(SqlErrorSanitizer.Sanitize(207), SqlErrorSanitizer.Sanitize(208));
    }

    /// <summary>未列出的號碼只回傳通用訊息與號碼本身。</summary>
    [Fact]
    public void UnknownNumber_ReturnsGenericMessageWithNumberOnly()
    {
        Assert.Equal("執行失敗（錯誤碼 8134）。", SqlErrorSanitizer.Sanitize(8134));
    }
}
