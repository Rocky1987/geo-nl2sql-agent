using Microsoft.Data.SqlClient;

namespace GeoNl2Sql.Core.Guardrails;

/// <summary>
/// 查詢的資源上限（設定區段 <c>Query</c>）。
/// </summary>
public sealed class QueryLimits
{
    /// <summary>設定區段名稱。</summary>
    public const string SectionName = "Query";

    /// <summary>單一查詢的逾時秒數；超過時 SQL Server 端中止並拋出 <see cref="SqlException"/>（Number == -2）。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>最多讀回的列數；超過即停止讀取並標記截斷。</summary>
    public int MaxRows { get; set; } = 1000;
}

/// <summary>
/// 一次查詢的結果。
/// </summary>
/// <param name="Columns">欄位名稱。</param>
/// <param name="Rows">資料列；geography／geometry 欄位為原始位元組（<c>byte[]</c>），其餘為 ADO.NET 的 CLR 型別，SQL NULL 為 <c>null</c>。</param>
/// <param name="Truncated">true 表示結果超過 <see cref="QueryLimits.MaxRows"/>，後面的列沒有讀回。</param>
/// <param name="ColumnTypes">各欄的 SQL Server 型別名稱（小寫，UDT 去掉資料庫前綴，例如 <c>geography</c>、<c>nvarchar</c>）；與 <paramref name="Columns"/> 等長。未提供時為 <c>null</c>，下游無法辨識空間欄位。</param>
public sealed record SqlQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated,
    IReadOnlyList<string>? ColumnTypes = null);

/// <summary>
/// 第二層防線的執行端：以低權限 SQL login（<c>geo_reader</c>）執行 SQL，並套用逾時與列數上限。
/// 連線身分本身就是唯讀，所以即使驗證器被繞過，資料庫仍會拒絕寫入與 DDL。
/// 本類別不檢查 SQL 內容、不改寫 SQL；錯誤一律以例外往上拋，由呼叫端決定如何消毒後回饋給模型。
/// </summary>
public sealed class ReadOnlySqlExecutor
{
    private readonly string _connectionString;
    private readonly QueryLimits _limits;

    /// <summary>
    /// 建立執行器。
    /// </summary>
    /// <param name="connectionString">唯讀 login 的連線字串（<c>ConnectionStrings:Reader</c>）；呼叫端須確保它是 SQL 驗證的低權限帳號。</param>
    /// <param name="limits">逾時與列數上限。</param>
    public ReadOnlySqlExecutor(string connectionString, QueryLimits limits)
    {
        _connectionString = connectionString;
        _limits = limits;
    }

    /// <summary>
    /// 執行 SQL 並讀回第一個結果集。讀滿 <see cref="QueryLimits.MaxRows"/> 列後若還有資料，
    /// 取消命令並標記 <see cref="SqlQueryResult.Truncated"/>；否則讀完其餘結果集，讓後續語句的錯誤也能浮現。
    /// </summary>
    /// <param name="sql">要執行的 SQL。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>第一個結果集；沒有結果集的語句回傳零欄零列。</returns>
    /// <exception cref="SqlException">權限不足、語法或執行錯誤，逾時時 <see cref="SqlException.Number"/> 為 -2。</exception>
    public Task<SqlQueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sql, null, cancellationToken);

    /// <summary>
    /// 帶參數的版本，供工具的固定模板使用（docs/m3-implementation-plan.md §3.2）：值一律以 <see cref="SqlParameter"/> 傳入，不拼進 SQL 文字。
    /// 其餘行為（唯讀身分、逾時、列數上限、截斷）與無參數版本相同。模型生成的 SQL 仍只走無參數版本。
    /// </summary>
    /// <param name="sql">含 <c>@名稱</c> 參數的 SQL。</param>
    /// <param name="parameters">參數名稱（含 <c>@</c>）對值；為 <c>null</c> 或空表示沒有參數。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>第一個結果集。</returns>
    /// <exception cref="SqlException">權限不足、語法或執行錯誤，逾時時 <see cref="SqlException.Number"/> 為 -2。</exception>
    public async Task<SqlQueryResult> ExecuteAsync(string sql, IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _limits.TimeoutSeconds };
        if (parameters is not null)
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var columnTypes = Enumerable.Range(0, reader.FieldCount).Select(i => NormalizeTypeName(reader.GetDataTypeName(i))).ToList();
        var rows = new List<object?[]>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= _limits.MaxRows)
            {
                truncated = true;
                cmd.Cancel();
                break;
            }
            rows.Add(ReadRow(reader));
        }
        if (!truncated)
        {
            // 多語句時，後面語句的錯誤要等讀到對應的結果才會拋出。
            while (await reader.NextResultAsync(cancellationToken)) { }
        }
        return new SqlQueryResult(columns, rows, truncated, columnTypes);
    }

    /// <summary>
    /// 整理 <see cref="SqlDataReader.GetDataTypeName(int)"/> 的結果：UDT 會帶資料庫與結構前綴（例如 <c>Db.sys.geography</c>），只取最後一段並轉小寫。
    /// </summary>
    /// <param name="name">驅動程式回報的型別名稱。</param>
    private static string NormalizeTypeName(string name) => name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant();

    /// <summary>
    /// 讀出目前這一列。geography／geometry 是 UDT，沒有 Microsoft.SqlServer.Types 時不能 GetValue，改讀原始位元組。
    /// </summary>
    /// <param name="reader">已定位到某一列的讀取器。</param>
    private static object?[] ReadRow(SqlDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            if (reader.IsDBNull(i)) continue;
            var type = reader.GetDataTypeName(i);
            row[i] = type.EndsWith("geography", StringComparison.OrdinalIgnoreCase) || type.EndsWith("geometry", StringComparison.OrdinalIgnoreCase)
                ? reader.GetSqlBytes(i).Value
                : reader.GetValue(i);
        }
        return row;
    }
}
