using GeoNl2Sql.Core.Audit;
using Microsoft.Data.SqlClient;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// M4 S3 的稽核表實測（docs/m4-implementation-plan.md §5.1、§5.4）：<c>audit.QueryLog</c> 是 append-only ledger；
/// <c>geo_auditor</c> 只能 INSERT；兩個 reader 讀不到也寫不了；擁有者也不能 UPDATE／DELETE／TRUNCATE。
/// 需要本機資料庫與三個 login。測試寫入的紀錄無法刪除（append-only），會一直留到下次 seed 重建資料庫。
/// </summary>
[Trait("Category", "Database")]
public class AuditDatabaseTests
{
    private const string InsertOne =
        "INSERT audit.QueryLog (OccurredAt, DurationMs, [Role], Question, Outcome, ScrubbedCells, ModelCalls, Provider, ModelId, AgentPromptVersion, Nl2SqlPromptVersion) " +
        "VALUES (SYSUTCDATETIME(), 1, N'analyst', N'AuditDatabaseTests', 'success', 0, 0, 'Ollama', 'm', 'v', 'v')";

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<SqlException> FailsAsync(string connectionString, string sql) =>
        await Assert.ThrowsAsync<SqlException>(() => ScalarAsync<object>(connectionString, sql));

    /// <summary>資料表是 append-only ledger（不是一般資料表）。</summary>
    [Fact]
    public async Task QueryLog_IsAppendOnlyLedgerTable()
    {
        var type = await ScalarAsync<string>(DbConfig.Demo,
            "SELECT ledger_type_desc FROM sys.tables WHERE name = 'QueryLog' AND SCHEMA_NAME(schema_id) = 'audit'");

        Assert.Equal("APPEND_ONLY_LEDGER_TABLE", type);
    }

    /// <summary><c>SqlAuditWriter</c>（geo_auditor）寫入後，欄位原樣讀回；沒回報的 token 與未命中的規則是 NULL。</summary>
    [Fact]
    public async Task Auditor_CanInsert_AndRecordRoundTrips()
    {
        var question = $"往返測試 {Guid.NewGuid():N}";
        var record = new AuditRecord
        {
            OccurredAt = DateTime.UtcNow,
            DurationMs = 123,
            Role = "admin",
            Question = question,
            Outcome = AuditOutcome.Success,
            ToolCalls = "[{\"name\":\"query_database\"}]",
            Sql = "SELECT 1",
            Attempts = 2,
            RowCount = 7,
            Truncated = false,
            ModelCalls = 3,
            InputTokens = null,
            OutputTokens = 15,
            Provider = "Anthropic",
            ModelId = "test-model",
            AgentPromptVersion = "v2",
            Nl2SqlPromptVersion = "described",
            AnswerExcerpt = "好",
        };

        await new SqlAuditWriter(DbConfig.Auditor).WriteAsync(record);

        await using var conn = new SqlConnection(DbConfig.Demo);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT [Role], Outcome, DurationMs, [Sql], Attempts, [RowCount], Truncated, ModelCalls, InputTokens, OutputTokens, GuardRule, DetectorVersion, Provider, ModelId " +
            "FROM audit.QueryLog WHERE Question = @q", conn);
        cmd.Parameters.AddWithValue("@q", question);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("admin", r.GetString(0));
        Assert.Equal("success", r.GetString(1));
        Assert.Equal(123, r.GetInt32(2));
        Assert.Equal("SELECT 1", r.GetString(3));
        Assert.Equal(2, r.GetInt32(4));
        Assert.Equal(7, r.GetInt32(5));
        Assert.False(r.GetBoolean(6));
        Assert.Equal(3, r.GetInt32(7));
        Assert.True(r.IsDBNull(8));
        Assert.Equal(15L, r.GetInt64(9));
        Assert.True(r.IsDBNull(10));
        Assert.True(r.IsDBNull(11));
        Assert.Equal("Anthropic", r.GetString(12));
        Assert.Equal("test-model", r.GetString(13));
        Assert.False(await r.ReadAsync());
    }

    /// <summary><c>geo_auditor</c> 只寫不讀：SELECT、UPDATE、DELETE、TRUNCATE，以及讀業務資料表，全部被拒。</summary>
    /// <param name="sql">應被拒絕的語句。</param>
    [Theory]
    [InlineData("SELECT COUNT(*) FROM audit.QueryLog")]
    [InlineData("UPDATE audit.QueryLog SET Question = N'x'")]
    [InlineData("DELETE FROM audit.QueryLog")]
    [InlineData("TRUNCATE TABLE audit.QueryLog")]
    [InlineData("SELECT COUNT(*) FROM dbo.District")]
    [InlineData("SELECT COUNT(*) FROM dbo.Customer")]
    public async Task Auditor_CannotReadOrChange(string sql)
    {
        await FailsAsync(DbConfig.Auditor, sql);
    }

    /// <summary>兩個 reader 讀不到、寫不了稽核表（模型被誘騙也碰不到）。</summary>
    /// <param name="sql">應被拒絕的語句。</param>
    [Theory]
    [InlineData("SELECT COUNT(*) FROM audit.QueryLog")]
    [InlineData(InsertOne)]
    [InlineData("DELETE FROM audit.QueryLog")]
    public async Task Readers_CannotTouchAuditTable(string sql)
    {
        await FailsAsync(DbConfig.Reader, sql);
        await FailsAsync(DbConfig.ReaderPii, sql);
    }

    /// <summary>append-only：連擁有者（管理身分）也不能 UPDATE、DELETE 或 TRUNCATE，而 INSERT 照常可用。</summary>
    [Fact]
    public async Task Owner_CannotUpdateDeleteOrTruncate()
    {
        await ScalarAsync<object?>(DbConfig.Demo, InsertOne + "; SELECT 1");

        await FailsAsync(DbConfig.Demo, "UPDATE audit.QueryLog SET Question = N'x'");
        await FailsAsync(DbConfig.Demo, "DELETE FROM audit.QueryLog");
        await FailsAsync(DbConfig.Demo, "TRUNCATE TABLE audit.QueryLog");
    }
}
