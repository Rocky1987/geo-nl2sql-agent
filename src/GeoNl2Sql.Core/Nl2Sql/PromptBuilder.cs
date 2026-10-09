using System.Text.RegularExpressions;

namespace GeoNl2Sql.Core.Nl2Sql;

/// <summary>
/// 組出 NL2SQL 的 system 訊息。described 版的輸出與 M1 量測基線逐字相同（由快照測試守住），
/// 這是 M2 端到端準確率能和 M1 數字直接比較的前提；修改本類別前必須先確認這一點。
/// </summary>
public static class PromptBuilder
{
    /// <summary>含說明欄與資料表說明的完整 schema 版本（M1 的 C2）。</summary>
    public const string Described = "described";

    /// <summary>只有表名、欄名、型別、關聯與空間慣例的精簡版本（M1 的 C1）。</summary>
    public const string Plain = "plain";

    /// <summary>
    /// 組 system 訊息：輸出規則與 T-SQL 方言提醒，加上 schema 文字。
    /// schema 文字取自 schema-description.md 從「資料庫為」到「## 維護注意」之前的內容；
    /// plain 版再去掉說明欄、表名後的括號說明與資料表段落內的說明文字，所以兩個版本的其餘文字完全相同。
    /// </summary>
    /// <param name="markdown">schema-description.md 的全文。</param>
    /// <param name="schemaVersion"><see cref="Plain"/> 或 <see cref="Described"/>（預設）。</param>
    /// <returns>完整的 system 訊息（換行為 <c>\n</c>）。</returns>
    public static string BuildSystemPrompt(string markdown, string schemaVersion = Described)
    {
        var start = markdown.IndexOf("資料庫為", StringComparison.Ordinal);
        var lines = markdown[start..markdown.IndexOf("## 維護注意", StringComparison.Ordinal)].TrimEnd().Split('\n').Select(l => l.TrimEnd('\r'));
        if (schemaVersion == Plain)
        {
            var section = "";
            lines = lines.Select(l =>
            {
                if (l.StartsWith("## ")) section = l;
                if (l.StartsWith('|')) return string.Join('|', l.Split('|').Take(3)) + "|";
                if (l.StartsWith("### ")) return Regex.Replace(l, "（.*）", "");
                return section == "## 資料表" && l.Length > 0 && !l.StartsWith('#') ? null : l;
            }).OfType<string>();
        }
        return $"""
            你是 SQL Server 的 NL2SQL 助手。請依下方 schema，把使用者的問題轉成一個 T-SQL 查詢。
            規則：
            - 只輸出一個 SELECT 語句（可用 WITH 開頭），放在 ```sql 區塊內，不要解釋。
            - 方言是 T-SQL：取前 N 筆用 TOP，不用 LIMIT。
            - 空間欄位是 SQL Server geography，使用方法呼叫語法，例如 a.Location.STDistance(b.Location)；距離單位是公尺。

            # Schema

            {string.Join('\n', lines)}
            """;
    }
}
