using System.Globalization;

namespace GeoNl2Sql.Eval.Common;

/// <summary>執行結果比對（execution accuracy），自 M1 的 spike 抽出，spike 與 pipeline 共用。</summary>
public static class ResultComparer
{
    /// <summary>
    /// execution accuracy 比對：欄位數相同，且列的多重集合相同（ordered 時順序也須相同）。
    /// 欄位名稱不比；數值容差 1e-6（大於 1 的數值按比例放大）；NULL 等於 NULL。
    /// </summary>
    /// <param name="gold">標準結果。</param>
    /// <param name="actual">生成 SQL 的結果。</param>
    /// <param name="ordered">是否比對列順序。</param>
    /// <returns>兩個結果集相同時為 true。</returns>
    public static bool SameResult(List<object?[]> gold, List<object?[]> actual, bool ordered)
    {
        if (gold.Count != actual.Count) return false;
        if (gold.Count > 0 && gold[0].Length != actual[0].Length) return false;
        if (gold.Count == 0) return true;
        if (!ordered)
        {
            // 以四捨五入到 6 位的文字當排序鍵，讓容差內的數值排在相同位置。
            static string Key(object?[] row) => string.Join('\u001f', row.Select(v => v is double d ? d.ToString("F6", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)));
            gold = gold.OrderBy(Key, StringComparer.Ordinal).ToList();
            actual = actual.OrderBy(Key, StringComparer.Ordinal).ToList();
        }
        return gold.Zip(actual).All(pair => pair.First.Zip(pair.Second).All(c => c switch
        {
            (null, null) => true,
            (double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))),
            var (a, b) => Equals(a, b),
        }));
    }

    /// <summary>
    /// 把執行器回傳的列轉成與 spike 的標準結果相同的表示法：數值統一轉成 double，空間型別（byte[]）轉成十六進位字串。
    /// </summary>
    /// <param name="rows">執行器回傳的列。</param>
    /// <returns>可交給 <see cref="SameResult"/> 比對的列。</returns>
    public static List<object?[]> Normalize(IEnumerable<object?[]> rows) => rows.Select(row => row.Select(v => v switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        byte or short or int or long or decimal or float or double => (object)Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => v,
    }).ToArray()).ToList();
}
