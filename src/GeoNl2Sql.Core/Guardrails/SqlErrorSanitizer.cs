namespace GeoNl2Sql.Core.Guardrails;

/// <summary>
/// 把 SQL Server 的錯誤號碼轉成固定的訊息再回饋給模型。SQL Server 的原文會帶出物件名稱
/// （例如「無效的資料行名稱 'd2'」），轉傳等於替模型探測 schema，所以永不轉傳原文，只看錯誤號碼。
/// 取捨：修正訊息會比原文模糊，S6 的量測會檢驗對自我修正成功率的實際影響。
/// </summary>
public static class SqlErrorSanitizer
{
    /// <summary>
    /// 依錯誤號碼取得固定訊息。簽章只收號碼（<c>SqlException</c> 無法自行建構），所以可以完全離線測試。
    /// </summary>
    /// <param name="errorNumber"><c>SqlException.Number</c>；逾時為 -2。</param>
    /// <returns>不含任何資料表或欄位名稱的訊息。</returns>
    public static string Sanitize(int errorNumber) => errorNumber switch
    {
        207 => "查詢引用了不存在的欄位，請只使用 schema 中列出的欄位。",
        208 => "查詢引用了不存在的資料表或物件。",
        4104 => "有無法繫結的多段式識別碼，請檢查資料表別名與 CTE 的作用範圍。",
        102 or 156 => "語法錯誤。",
        195 or 4121 => "使用了不存在的函式；geography 函式要用方法呼叫語法，例如 a.Location.STDistance(b.Location)。",
        8120 => "SELECT 中有欄位未包含在 GROUP BY 或聚合函式中。",
        229 or 230 or 262 => "權限不足（唯讀查詢只能讀取允許的資料表）。",
        -2 => "查詢逾時，請簡化查詢。",
        _ => $"執行失敗（錯誤碼 {errorNumber}）。",
    };
}
