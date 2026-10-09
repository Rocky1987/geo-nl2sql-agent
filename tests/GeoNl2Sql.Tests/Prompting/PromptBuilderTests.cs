using GeoNl2Sql.Core.Nl2Sql;

namespace GeoNl2Sql.Tests.Prompting;

/// <summary>
/// 快照測試：<see cref="PromptBuilder"/> 的輸出必須與 M1 量測時記錄的 system 訊息逐字相同
/// （docs/m2-implementation-plan.md §6.2）。快照取自 M1 的 claude-haiku-4-5 結果 JSON 的 <c>systemPrompt</c>
/// （described 為 C2、plain 為 C1；各輪的內容完全一致）。這個測試失敗代表 M2 的準確率不能再直接和 M1 基線比較。
/// </summary>
public class PromptBuilderTests
{
    /// <summary>讀取輸出目錄下 Prompting 資料夾中的檔案，換行統一為 <c>\n</c>（避免 git 換行轉換造成誤判）。</summary>
    /// <param name="fileName">檔名。</param>
    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompting", fileName)).Replace("\r\n", "\n");

    /// <summary>described 版與 plain 版都與 M1 的記錄逐字相同。</summary>
    /// <param name="version">schema 版本。</param>
    /// <param name="snapshotFile">快照檔名。</param>
    [Theory]
    [InlineData(PromptBuilder.Described, "described-system-prompt.txt")]
    [InlineData(PromptBuilder.Plain, "plain-system-prompt.txt")]
    public void SystemPrompt_MatchesM1Snapshot(string version, string snapshotFile)
    {
        var actual = PromptBuilder.BuildSystemPrompt(Read("schema-description.md"), version);

        Assert.Equal(Read(snapshotFile), actual);
    }

    /// <summary>未指定版本時使用 described。</summary>
    [Fact]
    public void DefaultVersion_IsDescribed()
    {
        var markdown = Read("schema-description.md");

        Assert.Equal(PromptBuilder.BuildSystemPrompt(markdown, PromptBuilder.Described), PromptBuilder.BuildSystemPrompt(markdown));
    }
}
