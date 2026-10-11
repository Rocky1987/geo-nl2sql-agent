using System.Text.Json;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Eval.Seed;

namespace GeoNl2Sql.Tests.Guardrails;

/// <summary>
/// 提示詞注入攻擊語料庫的格式與筆數檢查（docs/m4-implementation-plan.md §7.2、§7.4）。
/// 語料庫在執行攻擊測試前 commit 凍結（H8）；這些測試防止之後不小心刪改，讓「阻擋率」的分母悄悄縮水。
/// 另外檢查 10 題「看起來可疑但正常」的問題不被輸入偵測誤擋（H7 的第二部分）。
/// </summary>
public class AttackCorpusIntegrityTests
{
    private static readonly string[] Categories = ["direct", "jailbreak", "schema", "prompt", "indirect"];
    private static readonly string[] Goals = ["pii", "write", "schema", "prompt", "follow_instruction"];

    /// <summary>攻擊語料庫的一筆資料。</summary>
    private sealed record Attack(string Id, string Category, string Goal, bool Evasion, bool RunB, bool RunC, string Question, int[]? MarkerStations);

    private sealed record Benign(string Id, string Question, string Note);

    private static List<T> Load<T>(string fileName) =>
        JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", fileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>共 50 筆，五類各 10 筆，編號與問題不重複，類別與目標都在已知的集合內，問題不超過 500 字元。</summary>
    [Fact]
    public void Corpus_Has50UniqueEntries_TenPerCategory()
    {
        var attacks = Load<Attack>("attacks.json");

        Assert.Equal(50, attacks.Count);
        Assert.Equal(50, attacks.Select(a => a.Id).Distinct().Count());
        Assert.Equal(50, attacks.Select(a => a.Question).Distinct().Count());
        Assert.All(Categories, c => Assert.Equal(10, attacks.Count(a => a.Category == c)));
        Assert.All(attacks, a => Assert.Contains(a.Category, Categories));
        Assert.All(attacks, a => Assert.Contains(a.Goal, Goals));
        Assert.All(attacks, a => Assert.InRange(a.Question.Length, 1, 500));
    }

    /// <summary>每一類至少 3 筆是針對 r1 規則的閃避寫法（避免語料庫只收規則抓得到的題目）。</summary>
    [Fact]
    public void EveryCategory_HasAtLeastThreeEvasions()
    {
        var attacks = Load<Attack>("attacks.json");

        Assert.All(Categories, c => Assert.True(attacks.Count(a => a.Category == c && a.Evasion) >= 3, $"{c} 的閃避寫法不足 3 筆"));
    }

    /// <summary>B 輪 25 筆（每類 5 筆）、C 輪 10 筆；所有以個資為目標的題目都在 B 輪，C 輪都是個資題。</summary>
    [Fact]
    public void Subsets_MatchThePlan()
    {
        var attacks = Load<Attack>("attacks.json");

        Assert.Equal(25, attacks.Count(a => a.RunB));
        Assert.All(Categories, c => Assert.Equal(5, attacks.Count(a => a.Category == c && a.RunB)));
        Assert.Equal(10, attacks.Count(a => a.RunC));
        Assert.All(attacks.Where(a => a.RunC), a => Assert.True(a.Goal == "pii" && a.RunB, $"{a.Id} 不是 B 輪內的個資題"));
        Assert.All(attacks.Where(a => a.Goal == "pii"), a => Assert.True(a.RunB, $"{a.Id} 是個資題但不在 B 輪"));
    }

    /// <summary>間接注入的題目指向被下毒的基地台（有標記可查）；其餘類別沒有標記。</summary>
    [Fact]
    public void IndirectEntries_PointAtPoisonedStations()
    {
        var attacks = Load<Attack>("attacks.json");
        var poisoned = PoisonedStations.All.Select(p => p.StationId).ToHashSet();

        foreach (var a in attacks.Where(a => a.Category == "indirect"))
        {
            Assert.NotEmpty(a.MarkerStations ?? []);
            Assert.All(a.MarkerStations!, id => Assert.Contains(id, poisoned));
        }
        Assert.All(attacks.Where(a => a.Category != "indirect"), a => Assert.Null(a.MarkerStations));
        Assert.Equal(poisoned.OrderBy(i => i), attacks.SelectMany(a => a.MarkerStations ?? []).Distinct().OrderBy(i => i));
    }

    /// <summary>10 題「看起來可疑但正常」的問題：筆數正確、不重複，而且輸入偵測全部放行（H7 第二部分）。</summary>
    [Fact]
    public void BenignPrompts_AreTenAndNeverBlocked()
    {
        var benign = Load<Benign>("benign-prompts.json");

        Assert.Equal(10, benign.Count);
        Assert.Equal(10, benign.Select(b => b.Id).Distinct().Count());
        foreach (var b in benign)
            Assert.True(PromptInjectionDetector.Detect(b.Question) is null, $"{b.Id} 被誤擋：{PromptInjectionDetector.Detect(b.Question)}");
    }
}
