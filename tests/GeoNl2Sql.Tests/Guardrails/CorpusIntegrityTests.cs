using System.Text.Json;

namespace GeoNl2Sql.Tests.Guardrails;

/// <summary>
/// 檢查攻擊語料庫與正常查詢集本身的格式與筆數（docs/m2-implementation-plan.md §3.4）。
/// 語料庫在驗證器動工前凍結；這些測試防止之後不小心刪改、讓「阻擋率 100%」的分母悄悄縮水。
/// </summary>
public class CorpusIntegrityTests
{
    /// <summary>各攻擊類別應有的筆數（計畫 §3.2），總和 60。</summary>
    private static readonly Dictionary<string, int> ExpectedCategoryCounts = new()
    {
        ["multi_statement"] = 8, ["batch_separator"] = 4, ["comment"] = 8, ["case_whitespace"] = 6,
        ["dynamic_sql"] = 10, ["cte_wrapped"] = 6, ["select_into"] = 4, ["external"] = 4,
        ["catalog_probe"] = 4, ["session"] = 6,
    };

    /// <summary>攻擊語料庫的一筆資料。</summary>
    /// <param name="Id">編號，例如 MS02。</param>
    /// <param name="Category">攻擊類別。</param>
    /// <param name="Sql">攻擊 SQL。</param>
    /// <param name="Note">這條攻擊在試什麼。</param>
    /// <param name="DbExpect">驗證器被關掉時，唯讀 login 的預期結果：denied、read_only 或 tempdb_only。</param>
    private sealed record Attack(string Id, string Category, string Sql, string Note, string DbExpect);

    /// <summary>正常查詢集的一筆資料。</summary>
    /// <param name="Id">編號，例如 BN01。</param>
    /// <param name="Sql">看似可疑但無害的查詢。</param>
    /// <param name="Note">為何看似可疑。</param>
    private sealed record Benign(string Id, string Sql, string Note);

    /// <summary>讀取輸出目錄下 Guardrails 資料夾中的 JSON 檔並反序列化。</summary>
    /// <typeparam name="T">元素型別。</typeparam>
    /// <param name="fileName">檔名。</param>
    /// <returns>檔案內容。</returns>
    private static List<T> Load<T>(string fileName) =>
        JsonSerializer.Deserialize<List<T>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", fileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>攻擊語料庫共 60 條，編號不重複，SQL 不為空也不重複。</summary>
    [Fact]
    public void AttackCorpus_Has60UniqueEntries()
    {
        var attacks = Load<Attack>("attack-sql.json");

        Assert.Equal(60, attacks.Count);
        Assert.Equal(attacks.Count, attacks.Select(a => a.Id).Distinct().Count());
        Assert.Equal(attacks.Count, attacks.Select(a => a.Sql).Distinct().Count());
        Assert.All(attacks, a => Assert.False(string.IsNullOrWhiteSpace(a.Sql), a.Id));
        Assert.All(attacks, a => Assert.False(string.IsNullOrWhiteSpace(a.Note), a.Id));
    }

    /// <summary>每個類別的筆數符合計畫，且沒有計畫以外的類別。</summary>
    [Fact]
    public void AttackCorpus_CategoryCountsMatchPlan()
    {
        var actual = Load<Attack>("attack-sql.json").GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(ExpectedCategoryCounts.OrderBy(kv => kv.Key), actual.OrderBy(kv => kv.Key));
    }

    /// <summary>dbExpect 只能是三個定義過的值。</summary>
    [Fact]
    public void AttackCorpus_DbExpectIsKnownValue()
    {
        Assert.All(Load<Attack>("attack-sql.json"),
            a => Assert.Contains(a.DbExpect, new[] { "denied", "read_only", "tempdb_only" }));
    }

    /// <summary>正常查詢集至少 10 條，編號與 SQL 不重複，也不與攻擊語料庫重複。</summary>
    [Fact]
    public void BenignCorpus_HasAtLeast10UniqueEntries()
    {
        var benign = Load<Benign>("benign-sql.json");
        var attackSql = Load<Attack>("attack-sql.json").Select(a => a.Sql).ToHashSet();

        Assert.True(benign.Count >= 10);
        Assert.Equal(benign.Count, benign.Select(b => b.Id).Distinct().Count());
        Assert.Equal(benign.Count, benign.Select(b => b.Sql).Distinct().Count());
        Assert.DoesNotContain(benign, b => attackSql.Contains(b.Sql));
    }

    /// <summary>連結進來的 30 題標準題集可讀取，且每題都有標準 SQL。</summary>
    [Fact]
    public void GoldQuestions_Has30EntriesWithSql()
    {
        var gold = JsonSerializer.Deserialize<List<JsonElement>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", "questions.json")))!;

        Assert.Equal(30, gold.Count);
        Assert.All(gold, q => Assert.False(string.IsNullOrWhiteSpace(q.GetProperty("goldSql").GetString())));
    }
}
