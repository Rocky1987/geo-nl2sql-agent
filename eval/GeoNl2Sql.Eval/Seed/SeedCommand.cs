using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace GeoNl2Sql.Eval.Seed;

/// <summary>
/// <c>seed</c> 子命令：刪除並重建資料庫 GeoNl2SqlDemo，執行 db/01_schema.sql，灌入 <see cref="SeedData"/> 的合成資料，
/// 最後印出各表筆數與內容雜湊（兩次 seed 的雜湊應完全相同）。
/// 安全限制：只允許操作名為 <see cref="DatabaseName"/> 的資料庫，連線字串指向其他名稱時直接拒絕。
/// </summary>
public static class SeedCommand
{
    /// <summary>唯一允許被刪除重建的資料庫名稱。</summary>
    public const string DatabaseName = "GeoNl2SqlDemo";

    /// <summary>M2 唯讀帳號的 login／user 名稱（需與 db/02_reader.sql 一致）。</summary>
    private const string ReaderLoginName = "geo_reader";

    /// <summary>
    /// 執行 seed。
    /// </summary>
    /// <param name="config">已載入的設定；讀取 <c>ConnectionStrings:Demo</c>。</param>
    /// <exception cref="InvalidOperationException">缺少連線字串，或連線字串的資料庫不是 GeoNl2SqlDemo。</exception>
    public static async Task RunAsync(IConfiguration config)
    {
        var demo = config.GetConnectionString("Demo")
                   ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Demo（見 README 的設定與金鑰）。");
        var demoBuilder = new SqlConnectionStringBuilder(demo);
        if (demoBuilder.InitialCatalog != DatabaseName)
            throw new InvalidOperationException(
                $"拒絕執行：seed 只能重建 {DatabaseName}，但連線字串的資料庫是「{demoBuilder.InitialCatalog}」。");

        var master = new SqlConnectionStringBuilder(demo) { InitialCatalog = "master" }.ConnectionString;
        await RecreateDatabaseAsync(master);
        var readerCreated = await EnsureReaderLoginAsync(master, config.GetConnectionString("Reader"));

        var data = SeedData.Generate();
        await using var conn = new SqlConnection(demo);
        await conn.OpenAsync();
        await RunScriptAsync(conn, "01_schema.sql");
        if (readerCreated) await RunScriptAsync(conn, "02_reader.sql");
        await InsertAsync(conn, data);
        await PrintSummaryAsync(conn);
    }

    /// <summary>連 master，若 GeoNl2SqlDemo 存在則踢掉連線並 DROP，再重新 CREATE。</summary>
    /// <param name="masterConnectionString">指向 master 的連線字串。</param>
    private static async Task RecreateDatabaseAsync(string masterConnectionString)
    {
        await using var conn = new SqlConnection(masterConnectionString);
        await conn.OpenAsync();
        // 資料庫名稱是常數，不是外部輸入，因此可以直接組字串（DDL 不能參數化）。
        var sql = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            CREATE DATABASE [{DatabaseName}];
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"已重建資料庫 {DatabaseName}。");
    }

    /// <summary>
    /// 依 <c>ConnectionStrings:Reader</c> 建立 SQL login（不存在就建立，存在就更新密碼），讓連線密碼只維護一份。
    /// login 是伺服器層級物件，重建資料庫不會刪除它；資料庫內的 user 與權限由 02_reader.sql 建立。
    /// 未設定 Reader 連線字串時略過（M1 的 spike 流程不需要）。
    /// </summary>
    /// <param name="masterConnectionString">指向 master 的連線字串。</param>
    /// <param name="readerConnectionString">唯讀帳號的連線字串（含 User ID 與 Password）；可為 null。</param>
    /// <returns>true 表示已確保 login 存在，呼叫端應接著執行 02_reader.sql；false 表示略過。</returns>
    /// <exception cref="InvalidOperationException">缺少 User ID／Password，或 login 名稱含有 [A-Za-z0-9_] 以外的字元。</exception>
    private static async Task<bool> EnsureReaderLoginAsync(string masterConnectionString, string? readerConnectionString)
    {
        if (string.IsNullOrWhiteSpace(readerConnectionString))
        {
            Console.WriteLine("未設定 ConnectionStrings:Reader，略過 geo_reader 的建立。");
            return false;
        }
        var builder = new SqlConnectionStringBuilder(readerConnectionString);
        if (builder.UserID != ReaderLoginName || string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                $"ConnectionStrings:Reader 必須以 SQL 驗證指定 User ID={ReaderLoginName} 與 Password。");

        // DDL 不能參數化：名稱是常數，密碼只需轉義單引號。
        var password = builder.Password.Replace("'", "''");
        var sql = $"""
            IF SUSER_ID(N'{ReaderLoginName}') IS NULL
                CREATE LOGIN [{ReaderLoginName}] WITH PASSWORD = N'{password}', CHECK_POLICY = ON, DEFAULT_DATABASE = [{DatabaseName}];
            ELSE
                ALTER LOGIN [{ReaderLoginName}] WITH PASSWORD = N'{password}';
            """;
        await using var conn = new SqlConnection(masterConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"已建立或更新 login {ReaderLoginName}。");
        return true;
    }

    /// <summary>讀取輸出目錄下 db/ 的指定 SQL 檔，以單獨一行的 GO 切批後依序執行。</summary>
    /// <param name="conn">已開啟、指向 GeoNl2SqlDemo 的連線。</param>
    /// <param name="fileName">db/ 下的檔名，例如 01_schema.sql。</param>
    private static async Task RunScriptAsync(SqlConnection conn, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "db", fileName);
        var script = await File.ReadAllTextAsync(path);
        var batches = script.Split(['\r', '\n'], StringSplitOptions.None)
            .Aggregate(new List<StringBuilder> { new() }, (acc, line) =>
            {
                if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase)) acc.Add(new StringBuilder());
                else acc[^1].AppendLine(line);
                return acc;
            })
            .Select(b => b.ToString())
            .Where(b => !string.IsNullOrWhiteSpace(b));
        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        Console.WriteLine($"已執行 {fileName}。");
    }

    /// <summary>
    /// 在單一交易內寫入全部資料。
    /// Microsoft.Data.SqlClient 沒有 geography 的 .NET 型別，所以含 geography 的兩張表（District、BaseStation）
    /// 用參數化 INSERT 搭配 <c>geography::STGeomFromText(@wkt, 4326)</c>；其餘表用 SqlBulkCopy。
    /// </summary>
    /// <param name="conn">已開啟、指向 GeoNl2SqlDemo 的連線。</param>
    /// <param name="data">要寫入的資料。</param>
    private static async Task InsertAsync(SqlConnection conn, SeedData data)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        await using (var cmd = new SqlCommand(
            "INSERT dbo.District (DistrictId, DistrictName, Boundary, Population) " +
            "VALUES (@id, @name, geography::STGeomFromText(@wkt, 4326), @pop)", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.Int);
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 50);
            cmd.Parameters.Add("@wkt", SqlDbType.NVarChar, -1);
            cmd.Parameters.Add("@pop", SqlDbType.Int);
            foreach (var d in data.Districts)
            {
                cmd.Parameters["@id"].Value = d.Id;
                cmd.Parameters["@name"].Value = d.Name;
                cmd.Parameters["@wkt"].Value = d.Wkt;
                cmd.Parameters["@pop"].Value = d.Population;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await BulkAsync(conn, tx, "dbo.ServicePlan", data.ServicePlans);

        await using (var cmd = new SqlCommand(
            "INSERT dbo.BaseStation (StationId, StationName, DistrictId, Location, Band, Status, InstalledDate) " +
            "VALUES (@id, @name, @did, geography::STGeomFromText(@wkt, 4326), @band, @status, @date)", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.Int);
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 50);
            cmd.Parameters.Add("@did", SqlDbType.Int);
            cmd.Parameters.Add("@wkt", SqlDbType.NVarChar, -1);
            cmd.Parameters.Add("@band", SqlDbType.NVarChar, 10);
            cmd.Parameters.Add("@status", SqlDbType.NVarChar, 20);
            cmd.Parameters.Add("@date", SqlDbType.Date);
            foreach (var s in data.Stations)
            {
                cmd.Parameters["@id"].Value = s.Id;
                cmd.Parameters["@name"].Value = s.Name;
                cmd.Parameters["@did"].Value = s.DistrictId;
                cmd.Parameters["@wkt"].Value = s.Wkt;
                cmd.Parameters["@band"].Value = s.Band;
                cmd.Parameters["@status"].Value = s.Status;
                cmd.Parameters["@date"].Value = s.InstalledDate;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await BulkAsync(conn, tx, "dbo.Customer", data.Customers);
        await BulkAsync(conn, tx, "dbo.Subscription", data.Subscriptions);
        await BulkAsync(conn, tx, "dbo.OutageEvent", data.Outages);

        await tx.CommitAsync();
    }

    /// <summary>以 SqlBulkCopy 寫入一張表；DataTable 的欄位順序須與目標表相同。</summary>
    /// <param name="conn">已開啟的連線。</param>
    /// <param name="tx">目前交易。</param>
    /// <param name="table">目標表名稱（含結構描述）。</param>
    /// <param name="rows">要寫入的資料。</param>
    private static async Task BulkAsync(SqlConnection conn, SqlTransaction tx, string table, DataTable rows)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = table };
        await bulk.WriteToServerAsync(rows);
    }

    /// <summary>
    /// 印出各表筆數、內容雜湊（SHA-256 前 16 碼）與 geography 的有效性檢查。
    /// 雜湊取自依主鍵排序的完整內容（geography 轉成 WKT 文字），所以只要任何一格資料不同，雜湊就會不同。
    /// </summary>
    /// <param name="conn">已開啟、指向 GeoNl2SqlDemo 的連線。</param>
    private static async Task PrintSummaryAsync(SqlConnection conn)
    {
        (string Table, string Query)[] tables =
        [
            ("District", "SELECT DistrictId, DistrictName, Boundary.STAsText(), Population FROM dbo.District ORDER BY DistrictId"),
            ("ServicePlan", "SELECT * FROM dbo.ServicePlan ORDER BY PlanId"),
            ("BaseStation", "SELECT StationId, StationName, DistrictId, Location.STAsText(), Band, Status, InstalledDate FROM dbo.BaseStation ORDER BY StationId"),
            ("Customer", "SELECT * FROM dbo.Customer ORDER BY CustomerId"),
            ("Subscription", "SELECT * FROM dbo.Subscription ORDER BY SubscriptionId"),
            ("OutageEvent", "SELECT * FROM dbo.OutageEvent ORDER BY OutageId"),
        ];
        Console.WriteLine("資料表            筆數  內容雜湊");
        foreach (var (table, query) in tables)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;
            await using var cmd = new SqlCommand(query, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < cells.Length; i++)
                    cells[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                sha.AppendData(Encoding.UTF8.GetBytes(string.Join('\u001f', cells) + "\n"));
                count++;
            }
            Console.WriteLine($"{table,-16} {count,5}  {Convert.ToHexString(sha.GetHashAndReset())[..16]}");
        }

        await using var check = new SqlCommand(
            """
            SELECT (SELECT MIN(CAST(Boundary.STIsValid() AS int)) FROM dbo.District),
                   (SELECT MIN(CAST(Location.STIsValid() AS int)) FROM dbo.BaseStation),
                   (SELECT MIN(Boundary.STArea()) / 1e6 FROM dbo.District),
                   (SELECT MAX(Boundary.STArea()) / 1e6 FROM dbo.District),
                   (SELECT MIN(c) FROM (SELECT COUNT(*) c FROM dbo.BaseStation GROUP BY DistrictId) t)
            """, conn);
        await using var r = await check.ExecuteReaderAsync();
        await r.ReadAsync();
        Console.WriteLine(
            $"geography 檢查：行政區全有效={r.GetInt32(0) == 1}，基地台全有效={r.GetInt32(1) == 1}，" +
            $"行政區面積 {r.GetDouble(2):F1}～{r.GetDouble(3):F1} km²，每區最少基地台數={r.GetInt32(4)}");
    }
}
