using Microsoft.Data.SqlClient;

namespace GeoNl2Sql.Core.Audit;

/// <summary>寫入稽核紀錄。寫入失敗要往外丟例外，呼叫端據此讓整個請求失敗（docs/m4-implementation-plan.md §5.3）。</summary>
public interface IAuditWriter
{
    /// <summary>寫入一筆紀錄。</summary>
    /// <param name="record">要寫入的紀錄。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <exception cref="Exception">寫入失敗時丟出例外（任何型別）。</exception>
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>稽核紀錄寫入失敗；<see cref="Exception.InnerException"/> 是原始例外。控制器據此回固定訊息而不送出結果。</summary>
/// <param name="inner">寫入器丟出的原始例外。</param>
public sealed class AuditWriteException(Exception inner) : Exception("稽核紀錄寫入失敗。", inner);

/// <summary>
/// 以 <c>geo_auditor</c>（只有 <c>audit.QueryLog</c> 的 INSERT 權限）寫入稽核表。
/// 用法：<c>await new SqlAuditWriter(connectionString).WriteAsync(record)</c>。
/// </summary>
/// <param name="connectionString"><c>geo_auditor</c> 的連線字串（<c>ConnectionStrings:Auditor</c>）。</param>
public sealed class SqlAuditWriter(string connectionString) : IAuditWriter
{
    private const string InsertSql = """
        INSERT audit.QueryLog (OccurredAt, DurationMs, [Role], Question, Outcome, GuardRule, ToolCalls, [Sql], Attempts, [RowCount], Truncated,
                               ScrubbedCells, ModelCalls, InputTokens, OutputTokens, Provider, ModelId, AgentPromptVersion, Nl2SqlPromptVersion,
                               DetectorVersion, AnswerExcerpt)
        VALUES (@OccurredAt, @DurationMs, @Role, @Question, @Outcome, @GuardRule, @ToolCalls, @Sql, @Attempts, @RowCount, @Truncated,
                @ScrubbedCells, @ModelCalls, @InputTokens, @OutputTokens, @Provider, @ModelId, @AgentPromptVersion, @Nl2SqlPromptVersion,
                @DetectorVersion, @AnswerExcerpt)
        """;

    /// <inheritdoc />
    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(InsertSql, conn);
        var p = cmd.Parameters;
        p.AddWithValue("@OccurredAt", record.OccurredAt);
        p.AddWithValue("@DurationMs", record.DurationMs);
        p.AddWithValue("@Role", record.Role);
        p.AddWithValue("@Question", record.Question);
        p.AddWithValue("@Outcome", record.Outcome);
        p.AddWithValue("@GuardRule", (object?)record.GuardRule ?? DBNull.Value);
        p.AddWithValue("@ToolCalls", (object?)record.ToolCalls ?? DBNull.Value);
        p.AddWithValue("@Sql", (object?)record.Sql ?? DBNull.Value);
        p.AddWithValue("@Attempts", (object?)record.Attempts ?? DBNull.Value);
        p.AddWithValue("@RowCount", (object?)record.RowCount ?? DBNull.Value);
        p.AddWithValue("@Truncated", (object?)record.Truncated ?? DBNull.Value);
        p.AddWithValue("@ScrubbedCells", record.ScrubbedCells);
        p.AddWithValue("@ModelCalls", record.ModelCalls);
        p.AddWithValue("@InputTokens", (object?)record.InputTokens ?? DBNull.Value);
        p.AddWithValue("@OutputTokens", (object?)record.OutputTokens ?? DBNull.Value);
        p.AddWithValue("@Provider", record.Provider);
        p.AddWithValue("@ModelId", record.ModelId);
        p.AddWithValue("@AgentPromptVersion", record.AgentPromptVersion);
        p.AddWithValue("@Nl2SqlPromptVersion", record.Nl2SqlPromptVersion);
        p.AddWithValue("@DetectorVersion", (object?)record.DetectorVersion ?? DBNull.Value);
        p.AddWithValue("@AnswerExcerpt", (object?)record.AnswerExcerpt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
