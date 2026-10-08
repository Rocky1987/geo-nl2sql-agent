using System.Data;
using System.Globalization;
using Bogus;

namespace GeoNl2Sql.Eval.Seed;

/// <summary>行政區一列。<paramref name="Wkt"/> 為 POLYGON 的 WKT（經度在前）。</summary>
public sealed record DistrictRow(int Id, string Name, string Wkt, int Population);

/// <summary>基地台一列。<paramref name="Wkt"/> 為 POINT 的 WKT（經度在前）。</summary>
public sealed record StationRow(
    int Id, string Name, int DistrictId, string Wkt, string Band, string Status, DateTime InstalledDate);

/// <summary>
/// 以固定種子產生 GeoNl2SqlDemo 的全部合成資料（不碰資料庫）。
/// 同一個種子、同一版本的 Bogus 下，每次 <see cref="Generate"/> 的結果逐筆相同。
/// 列舉值（Band、Status、Cause、flg1）必須與 db/schema-description.md 一致。
/// </summary>
public sealed class SeedData
{
    /// <summary>亂數種子。改動它會改變全部資料。</summary>
    public const int Seed = 20261008;

    private const int StationCount = 200;
    private const int CustomerCount = 1000;
    private const int SubscriptionCount = 1500;
    private const int OutageCount = 300;

    /// <summary>每一區的虛構名稱，順序為由南到北、由西到東（列優先）。</summary>
    private static readonly string[] DistrictNames =
        ["海濱區", "南湖區", "東港區", "西林區", "中央區", "白石區", "丘陵區", "青山區", "北原區"];

    private static readonly string[] Bands = ["700MHz", "1800MHz", "2600MHz", "3500MHz"];
    private static readonly string[] Causes = ["PowerFailure", "FiberCut", "Weather", "Hardware", "Software"];

    /// <summary>9 個行政區。</summary>
    public List<DistrictRow> Districts { get; } = [];

    /// <summary>基地台，至少每區 10 站。</summary>
    public List<StationRow> Stations { get; } = [];

    /// <summary>資費方案（欄位：PlanId, PlanName, MonthlyFee, DataCapGb）。</summary>
    public DataTable ServicePlans { get; } = NewTable("PlanId:int", "PlanName:string", "MonthlyFee:decimal", "DataCapGb:int");

    /// <summary>客戶（欄位與 dbo.Customer 相同順序）。</summary>
    public DataTable Customers { get; } = NewTable(
        "CustomerId:int", "FullName:string", "NationalId:string", "Phone:string", "Email:string", "DistrictId:int", "flg1:byte");

    /// <summary>訂閱（欄位與 dbo.Subscription 相同順序）。</summary>
    public DataTable Subscriptions { get; } = NewTable(
        "SubscriptionId:int", "CustomerId:int", "PlanId:int", "StartDate:date", "dt2:date");

    /// <summary>中斷事件（欄位與 dbo.OutageEvent 相同順序）。</summary>
    public DataTable Outages { get; } = NewTable(
        "OutageId:int", "StationId:int", "StartedAt:date", "DurationMinutes:int", "Cause:string");

    /// <summary>
    /// 產生全部資料。
    /// </summary>
    /// <returns>填滿的 <see cref="SeedData"/>。</returns>
    public static SeedData Generate()
    {
        var data = new SeedData();
        var rng = new Random(Seed);
        Randomizer.Seed = new Random(Seed);   // 必須在建立 Faker 之前設定
        var faker = new Faker("zh_TW");

        data.BuildDistricts(rng);
        data.BuildStations(rng);
        data.BuildPlans();
        data.BuildCustomers(rng, faker);
        data.BuildSubscriptions(rng);
        data.BuildOutages(rng);
        return data;
    }

    // 3×3 網格：每格 0.10°×0.10°，左下角起點 (121.40E, 24.90N)，落在虛構的台灣北部範圍。
    private const double GridLon0 = 121.40, GridLat0 = 24.90, CellSize = 0.10;

    private static (double Lon, double Lat) CellOrigin(int districtIndex) =>
        (GridLon0 + (districtIndex % 3) * CellSize, GridLat0 + (districtIndex / 3) * CellSize);

    private static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>
    /// 建立 9 個矩形行政區。外環依「東 → 北 → 西 → 南」的順序，從上方看為逆時針，
    /// 符合 geography 的方向要求（方向相反會變成「地球上除了這塊以外的全部」）。
    /// </summary>
    private void BuildDistricts(Random rng)
    {
        for (var i = 0; i < 9; i++)
        {
            var (lon, lat) = CellOrigin(i);
            var wkt = $"POLYGON(({F(lon)} {F(lat)}, {F(lon + CellSize)} {F(lat)}, " +
                      $"{F(lon + CellSize)} {F(lat + CellSize)}, {F(lon)} {F(lat + CellSize)}, {F(lon)} {F(lat)}))";
            Districts.Add(new DistrictRow(i + 1, DistrictNames[i], wkt, rng.Next(20_000, 150_001)));
        }
    }

    /// <summary>
    /// 建立基地台。前 90 站依序輪流分配給 9 區（保證每區至少 10 站），其餘隨機分配；
    /// 點位在所屬格子內縮 0.005° 的範圍內隨機撒點，避免落在邊界上。
    /// </summary>
    private void BuildStations(Random rng)
    {
        var baseDate = new DateTime(2012, 1, 1);
        for (var i = 0; i < StationCount; i++)
        {
            var d = i < 90 ? i % 9 : rng.Next(9);
            var (lon0, lat0) = CellOrigin(d);
            var lon = lon0 + 0.005 + rng.NextDouble() * (CellSize - 0.01);
            var lat = lat0 + 0.005 + rng.NextDouble() * (CellSize - 0.01);
            var roll = rng.Next(100);
            var status = roll < 80 ? "Active" : roll < 92 ? "Maintenance" : "Decommissioned";
            Stations.Add(new StationRow(
                i + 1, $"BS-{i + 1:D4}", d + 1, $"POINT({F(lon)} {F(lat)})",
                Bands[rng.Next(Bands.Length)], status, baseDate.AddDays(rng.Next(0, 365 * 14))));
        }
    }

    /// <summary>建立 6 筆固定的資費方案。</summary>
    private void BuildPlans()
    {
        (string Name, decimal Fee, int Gb)[] plans =
        [
            ("輕量方案", 299m, 5), ("標準方案", 499m, 20), ("進階方案", 699m, 50),
            ("無限方案", 999m, 300), ("企業方案", 1299m, 500), ("學生方案", 199m, 10),
        ];
        for (var i = 0; i < plans.Length; i++)
            ServicePlans.Rows.Add(i + 1, plans[i].Name, plans[i].Fee, plans[i].Gb);
    }

    /// <summary>
    /// 建立客戶。姓名由 Bogus（zh_TW）產生；身分證字號為含檢查碼的合法格式；
    /// 電話為 09 開頭 10 碼；Email 使用保留網域 example.com，不會對應任何真實信箱。
    /// flg1：80% 個人(0)、17% 企業(1)、3% 政府機關(2)。
    /// </summary>
    private void BuildCustomers(Random rng, Faker faker)
    {
        for (var i = 1; i <= CustomerCount; i++)
        {
            var roll = rng.Next(100);
            var type = (byte)(roll < 80 ? 0 : roll < 97 ? 1 : 2);
            Customers.Rows.Add(
                i,
                faker.Name.LastName() + faker.Name.FirstName(),
                MakeNationalId(rng),
                "09" + rng.Next(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture),
                $"user{i:D4}@example.com",
                rng.Next(1, 10),
                type);
        }
    }

    /// <summary>
    /// 產生一個格式合法的身分證字號：英文字母 + 性別碼(1/2) + 7 位數字 + 檢查碼。
    /// 前九碼加權和（字母轉兩位數 n：n/10 + (n%10)*9，再加 d1..d8 乘 8..1，檢查碼乘 1）須為 10 的倍數。
    /// </summary>
    private static string MakeNationalId(Random rng)
    {
        // 字母對應碼（A..Z）。
        int[] codes = [10, 11, 12, 13, 14, 15, 16, 17, 34, 18, 19, 20, 21, 22, 35, 23, 24, 25, 26, 27, 28, 29, 32, 30, 31, 33];
        var letter = rng.Next(26);
        var digits = new int[8];
        digits[0] = rng.Next(1, 3);
        for (var i = 1; i < 8; i++) digits[i] = rng.Next(10);
        var n = codes[letter];
        var sum = n / 10 + n % 10 * 9;
        for (var i = 0; i < 8; i++) sum += digits[i] * (8 - i);
        var check = (10 - sum % 10) % 10;
        return ((char)('A' + letter)).ToString() + string.Concat(digits) + check;
    }

    /// <summary>
    /// 建立訂閱。每位客戶至少一筆，其餘隨機補到 1,500 筆；dt2（合約到期日）為起始日 + 12/24/36 個月。
    /// </summary>
    private void BuildSubscriptions(Random rng)
    {
        var first = new DateTime(2022, 1, 1);
        int[] months = [12, 24, 36];
        for (var i = 1; i <= SubscriptionCount; i++)
        {
            var customerId = i <= CustomerCount ? i : rng.Next(1, CustomerCount + 1);
            var start = first.AddDays(rng.Next(0, 1369));   // 2022-01-01 起約 3.75 年內
            Subscriptions.Rows.Add(i, customerId, rng.Next(1, 7), start, start.AddMonths(months[rng.Next(3)]));
        }
    }

    /// <summary>
    /// 建立中斷事件：發生於 2026-01-01 起 9 個月內（精確到秒），持續 5～600 分鐘。
    /// </summary>
    private void BuildOutages(Random rng)
    {
        var first = new DateTime(2026, 1, 1);
        for (var i = 1; i <= OutageCount; i++)
            Outages.Rows.Add(
                i, rng.Next(1, StationCount + 1), first.AddSeconds(rng.Next(0, 273 * 86_400)),
                rng.Next(5, 601), Causes[rng.Next(Causes.Length)]);
    }

    /// <summary>
    /// 建立欄位結構。<paramref name="columns"/> 的格式為「名稱:型別」，型別為 int/string/decimal/byte/date。
    /// 其中 date 以 DateTime 儲存（SqlBulkCopy 會依目標欄位轉成 date 或 datetime2）。
    /// </summary>
    private static DataTable NewTable(params string[] columns)
    {
        var table = new DataTable();
        foreach (var c in columns)
        {
            var parts = c.Split(':');
            Type type = parts[1] switch
            {
                "int" => typeof(int),
                "string" => typeof(string),
                "decimal" => typeof(decimal),
                "byte" => typeof(byte),
                "date" => typeof(DateTime),
                _ => throw new ArgumentException($"未知的欄位型別：{parts[1]}"),
            };
            table.Columns.Add(parts[0], type);
        }
        return table;
    }
}
