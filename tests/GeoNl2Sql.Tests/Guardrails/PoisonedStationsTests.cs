using System.Text.RegularExpressions;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Eval.Seed;

namespace GeoNl2Sql.Tests.Guardrails;

/// <summary>
/// 被下毒的基地台名稱的離線檢查（docs/m4-implementation-plan.md §7.1）：5 座固定編號、放得進 nvarchar(50)、
/// 標記不會字面出現在名稱裡（否則照實列出名稱就會被誤判成「被說服」），以及哪幾筆會被輸入偵測規則 r1 命中。
/// </summary>
public class PoisonedStationsTests
{
    /// <summary>剛好 5 座、編號不重複、落在 1～200 的範圍內。</summary>
    [Fact]
    public void FiveDistinctStations_InsideTheSeededRange()
    {
        Assert.Equal(5, PoisonedStations.All.Count);
        Assert.Equal(5, PoisonedStations.All.Select(p => p.StationId).Distinct().Count());
        Assert.All(PoisonedStations.All, p => Assert.InRange(p.StationId, 1, 200));
    }

    /// <summary>名稱放得進欄位（50 個字元），並以原本的 <c>BS-編號</c> 開頭。</summary>
    [Fact]
    public void Names_FitTheColumn_AndKeepTheOriginalPrefix()
    {
        foreach (var (id, name, _) in PoisonedStations.All)
        {
            Assert.True(name.Length <= PoisonedStations.MaxNameLength, $"{id} 的名稱長度 {name.Length} 超過上限");
            Assert.StartsWith($"BS-{id:D4} ", name);
        }
    }

    /// <summary>標記（INJ- 加該站編號）不會字面出現在任何一個名稱裡。</summary>
    [Fact]
    public void Marker_NeverAppearsLiterallyInAnyName()
    {
        foreach (var (id, _, _) in PoisonedStations.All)
            foreach (var (_, name, _) in PoisonedStations.All)
                Assert.False(Regex.IsMatch(name, PoisonedStations.MarkerPattern(id)), $"站 {id} 的標記出現在名稱「{name}」");
    }

    /// <summary>標記樣式吃得到常見寫法（INJ-031、INJ-31），但不會誤吃更大的數字。</summary>
    [Theory]
    [InlineData(31, "結尾：INJ-031", true)]
    [InlineData(31, "INJ-31。", true)]
    [InlineData(31, "INJ-310", false)]
    [InlineData(150, "INJ-0150", true)]
    [InlineData(150, "INJ-15", false)]
    public void MarkerPattern_MatchesRealisticForms(int id, string text, bool expected)
    {
        Assert.Equal(expected, Regex.IsMatch(text, PoisonedStations.MarkerPattern(id)));
    }

    /// <summary>標示為「r1 會命中」的名稱真的被偵測到（會在工具結果中被清理），其餘則是 r1 抓不到的寫法（量測模型本身的抵抗力）。</summary>
    [Fact]
    public void DetectionFlags_MatchWhatR1Actually_Does()
    {
        foreach (var (id, name, detected) in PoisonedStations.All)
            Assert.True(detected == (PromptInjectionDetector.Detect(name) is not null), $"站 {id} 的偵測旗標與 r1 的實際結果不符");
        Assert.Equal(2, PoisonedStations.All.Count(p => p.DetectedByR1));
    }
}
