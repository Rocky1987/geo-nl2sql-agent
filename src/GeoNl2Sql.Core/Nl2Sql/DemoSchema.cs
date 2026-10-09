namespace GeoNl2Sql.Core.Nl2Sql;

/// <summary>示範資料庫 GeoNl2SqlDemo 的固定資訊，供組裝管線時提供給 <c>SqlValidator</c>。</summary>
public static class DemoSchema
{
    /// <summary>允許查詢的 6 張資料表（不含結構描述）；與 db/schema-description.md 一致。</summary>
    public static readonly IReadOnlySet<string> Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "District", "ServicePlan", "BaseStation", "Customer", "Subscription", "OutageEvent",
    };
}
