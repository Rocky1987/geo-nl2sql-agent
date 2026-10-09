using GeoNl2Sql.Core.Nl2Sql;

namespace GeoNl2Sql.Tests.Prompting;

/// <summary>
/// <see cref="SqlExtractor"/> 的抽取規則（docs/m2-implementation-plan.md §6.2）：區塊優先，其次 SELECT／WITH 起始，否則抽不到。
/// </summary>
public class SqlExtractorTests
{
    /// <summary>有 sql 區塊：取區塊內容，去除前後空白與結尾分號。</summary>
    [Fact]
    public void SqlBlock_IsExtracted()
    {
        var text = "好的，查詢如下：\n```sql\nSELECT StationName FROM dbo.BaseStation;\n```\n以上。";

        Assert.Equal("SELECT StationName FROM dbo.BaseStation", SqlExtractor.Extract(text));
    }

    /// <summary>區塊標籤大小寫不拘。</summary>
    [Fact]
    public void SqlBlock_TagIsCaseInsensitive()
    {
        Assert.Equal("SELECT 1", SqlExtractor.Extract("```SQL\nSELECT 1\n```"));
    }

    /// <summary>沒有區塊：從第一個 SELECT 起取到結尾。</summary>
    [Fact]
    public void NoBlock_FallsBackToFirstSelect()
    {
        var text = "答案是 SELECT COUNT(*) FROM dbo.Customer";

        Assert.Equal("SELECT COUNT(*) FROM dbo.Customer", SqlExtractor.Extract(text));
    }

    /// <summary>沒有區塊且以 WITH 起始的查詢。</summary>
    [Fact]
    public void NoBlock_FallsBackToWith()
    {
        Assert.StartsWith("WITH x AS", SqlExtractor.Extract("WITH x AS (SELECT 1 a) SELECT * FROM x"));
    }

    /// <summary>多個區塊：只取第一個（M1 的行為；之後由驗證器要求單一語句）。</summary>
    [Fact]
    public void MultipleBlocks_TakesFirst()
    {
        var text = "```sql\nSELECT 1\n```\n或者\n```sql\nSELECT 2\n```";

        Assert.Equal("SELECT 1", SqlExtractor.Extract(text));
    }

    /// <summary>抽取不判斷安全：危險語句照樣抽出，交給驗證器拒絕。</summary>
    [Fact]
    public void DangerousSql_IsStillExtracted()
    {
        Assert.Equal("DROP TABLE dbo.Customer", SqlExtractor.Extract("```sql\nDROP TABLE dbo.Customer\n```"));
    }

    /// <summary>沒有任何 SQL、空區塊、空字串都抽不到。</summary>
    /// <param name="text">模型回應。</param>
    [Theory]
    [InlineData("抱歉，我無法回答這個問題。")]
    [InlineData("```sql\n\n```")]
    [InlineData("")]
    public void NoSql_ReturnsNull(string text)
    {
        Assert.Null(SqlExtractor.Extract(text));
    }
}
