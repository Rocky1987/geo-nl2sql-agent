using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// 資料庫測試的連線設定與資料快照。連線字串依序取自 user-secrets（與 Eval 共用）與環境變數
/// （<c>ConnectionStrings__Reader</c>、<c>ConnectionStrings__Demo</c>）。
/// 這些測試標記 <c>[Trait("Category", "Database")]</c>，CI 以 <c>--filter "Category!=Database"</c> 排除。
/// </summary>
internal static class DbConfig
{
    /// <summary>已載入的設定。</summary>
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddUserSecrets(typeof(DbConfig).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();

    /// <summary>唯讀 login <c>geo_reader</c> 的連線字串。</summary>
    /// <exception cref="InvalidOperationException">未設定時，附上設定方式。</exception>
    public static string Reader => Config.GetConnectionString("Reader")
        ?? throw new InvalidOperationException(
            "找不到 ConnectionStrings:Reader。請先設定 user-secrets 並執行 seed（見 docs/m2-implementation-plan.md §5.3）。");

    /// <summary>管理身分（Windows 驗證）的連線字串，只用來取資料快照與比對標準答案；預設指向本機 SQLEXPRESS 的 GeoNl2SqlDemo。</summary>
    public static string Demo => Config.GetConnectionString("Demo")
        ?? @"Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;Integrated Security=true;TrustServerCertificate=true";

    /// <summary>6 張表的查詢（與 seed 的雜湊算法一致：依主鍵排序，geography 轉 WKT）。</summary>
    private static readonly (string Table, string Query)[] Tables =
    [
        ("District", "SELECT DistrictId, DistrictName, Boundary.STAsText(), Population FROM dbo.District ORDER BY DistrictId"),
        ("ServicePlan", "SELECT * FROM dbo.ServicePlan ORDER BY PlanId"),
        ("BaseStation", "SELECT StationId, StationName, DistrictId, Location.STAsText(), Band, Status, InstalledDate FROM dbo.BaseStation ORDER BY StationId"),
        ("Customer", "SELECT * FROM dbo.Customer ORDER BY CustomerId"),
        ("Subscription", "SELECT * FROM dbo.Subscription ORDER BY SubscriptionId"),
        ("OutageEvent", "SELECT * FROM dbo.OutageEvent ORDER BY OutageId"),
    ];

    /// <summary>
    /// 取得資料庫快照：每張表的內容 SHA-256，使用者物件清單（<c>sys.objects</c>）與 <c>geo_reader</c> 的權限清單的 SHA-256。
    /// 任何一格資料或任何物件（含修改時間）不同，快照就不同。
    /// </summary>
    /// <returns>鍵為表名、<c>sys.objects</c> 或權限清單，值為雜湊字串。</returns>
    public static async Task<Dictionary<string, string>> SnapshotAsync()
    {
        await using var conn = new SqlConnection(Demo);
        await conn.OpenAsync();
        var result = new Dictionary<string, string>();
        foreach (var (table, query) in Tables.Concat(new[] { ("sys.objects",
                     "SELECT name, type, create_date, modify_date FROM sys.objects WHERE is_ms_shipped = 0 ORDER BY object_id"),
                     ("geo_reader 權限",
                      "SELECT state_desc, permission_name, class_desc, major_id FROM sys.database_permissions WHERE grantee_principal_id = USER_ID('geo_reader') ORDER BY class_desc, permission_name, major_id") }))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var cmd = new SqlCommand(query, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < cells.Length; i++)
                    cells[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                sha.AppendData(Encoding.UTF8.GetBytes(string.Join('\u001f', cells) + "\n"));
            }
            result[table] = Convert.ToHexString(sha.GetHashAndReset());
        }
        return result;
    }
}
