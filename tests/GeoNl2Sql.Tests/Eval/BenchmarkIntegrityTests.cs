using System.Text.Json;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;

namespace GeoNl2Sql.Tests.Eval;

/// <summary>
/// M5 的 60 題標準集 <c>benchmark.json</c> 本身的完整性（docs/m5-implementation-plan.md §3.5）：題數、類別、難度標籤、
/// 原 30 題與 M1 凍結的 <c>questions.json</c> 逐欄相同、標準 SQL 通過驗證器且不依賴目前時間。
/// 題集在任何模型看到新題目之前凍結（tag <c>m5-questions-frozen</c>）；這些測試防止之後不小心刪改。
/// </summary>
public class BenchmarkIntegrityTests
{
    /// <summary>標準集的一題（欄位與 Eval 的題集 JSON 相同）。</summary>
    /// <param name="Id">題號，例如 S09。</param>
    /// <param name="Category">類別：single、join、aggregate、spatial。</param>
    /// <param name="Question">問題文字。</param>
    /// <param name="GoldSql">標準 SQL。</param>
    /// <param name="Ordered">列順序是否為題意的一部分。</param>
    /// <param name="Difficulty">難度：easy、medium、hard；<c>questions.json</c> 沒有這個欄位，讀到的是 null。</param>
    private sealed record Item(string Id, string Category, string Question, string GoldSql, bool Ordered, string? Difficulty);

    /// <summary>讀取輸出目錄下的題集檔。</summary>
    /// <param name="folder">輸出目錄下的資料夾（連結檔的位置）。</param>
    /// <param name="fileName">檔名。</param>
    /// <returns>題目清單。</returns>
    private static List<Item> Load(string folder, string fileName) =>
        JsonSerializer.Deserialize<List<Item>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, folder, fileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>60 題標準集。</summary>
    private static List<Item> Benchmark() => Load("Eval", "benchmark.json");

    /// <summary>60 題、題號不重複、四個類別各 15 題，難度只能是 easy／medium／hard。</summary>
    [Fact]
    public void Benchmark_Has60UniqueIds_15PerCategory_AndKnownDifficulty()
    {
        var items = Benchmark();

        Assert.Equal(60, items.Count);
        Assert.Equal(60, items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(["aggregate", "join", "single", "spatial"], items.Select(i => i.Category).Distinct().Order());
        Assert.All(items.GroupBy(i => i.Category), g => Assert.Equal(15, g.Count()));
        Assert.All(items, i => Assert.Contains(i.Difficulty, new[] { "easy", "medium", "hard" }));
    }

    /// <summary>原 30 題逐字照抄：題號、類別、問題、標準 SQL、<c>ordered</c> 都與 <c>questions.json</c> 相同（只多了難度標籤）。</summary>
    [Fact]
    public void Benchmark_Original30_MatchFrozenQuestionsFieldByField()
    {
        var frozen = Load("Guardrails", "questions.json");
        var byId = Benchmark().ToDictionary(i => i.Id);

        Assert.Equal(30, frozen.Count);
        Assert.All(frozen, q =>
        {
            Assert.True(byId.TryGetValue(q.Id, out var copy), $"{q.Id} 不在 benchmark.json");
            Assert.Equal(q, copy! with { Difficulty = null });
        });
    }

    /// <summary>標準 SQL 的 Theory 資料。</summary>
    public static IEnumerable<object[]> Gold() => Benchmark().Select(i => new object[] { i.Id, i.GoldSql });

    /// <summary>每題標準 SQL 都通過驗證器，且不使用目前時間或亂數（否則標準答案會隨執行時間改變）。</summary>
    /// <param name="id">題號。</param>
    /// <param name="sql">標準 SQL。</param>
    [Theory]
    [MemberData(nameof(Gold))]
    public void GoldSql_PassesValidator_AndIsDeterministic(string id, string sql)
    {
        var result = new SqlValidator(DemoSchema.Tables).Validate(sql);

        Assert.True(result.IsValid, $"{id} 被驗證器拒絕（{result.Reason}）");
        foreach (var banned in new[] { "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "CURRENT_TIMESTAMP", "NEWID" })
            Assert.DoesNotContain(banned, sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 附帶量測（計畫 §3.5，延伸 M4 的 H7）：新 30 題的問題文字不被輸入偵測擋下。
    /// 若之後改規則造成誤擋，改規則而不是改題目。
    /// </summary>
    [Fact]
    public void NewQuestions_AreNotBlockedByInputDetector()
    {
        var frozenIds = Load("Guardrails", "questions.json").Select(q => q.Id).ToHashSet();
        var added = Benchmark().Where(i => !frozenIds.Contains(i.Id)).ToList();

        Assert.Equal(30, added.Count);
        Assert.All(added, i => Assert.True(PromptInjectionDetector.Detect(i.Question) is null,
            $"{i.Id} 被誤擋：{PromptInjectionDetector.Detect(i.Question)}"));
    }
}
