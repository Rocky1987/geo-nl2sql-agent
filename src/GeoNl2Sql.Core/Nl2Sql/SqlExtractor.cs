using System.Text.RegularExpressions;

namespace GeoNl2Sql.Core.Nl2Sql;

/// <summary>
/// 從模型的回應文字抽出 SQL。只負責「找到那段文字」，不判斷安全與否——那是 <c>SqlValidator</c> 的工作。
/// </summary>
public static class SqlExtractor
{
    /// <summary>
    /// 抽取規則：優先取第一個 <c>```sql</c> 區塊的內容；沒有區塊時取第一個 <c>SELECT</c>／<c>WITH</c> 起始到結尾的文字。
    /// 結果去除前後空白與結尾分號。
    /// </summary>
    /// <param name="response">模型回應的完整文字。</param>
    /// <returns>抽出的 SQL；抽不到時為 <c>null</c>（區塊內為空白也視為抽不到）。</returns>
    public static string? Extract(string response)
    {
        var match = Regex.Match(response, @"```sql\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(response, @"\b(?:SELECT|WITH)\b.*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var sql = (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).Trim().TrimEnd(';').Trim();
        return sql.Length == 0 ? null : sql;
    }
}
