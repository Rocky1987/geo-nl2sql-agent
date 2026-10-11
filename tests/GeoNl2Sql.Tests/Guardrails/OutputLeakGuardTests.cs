using System.Text;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;

namespace GeoNl2Sql.Tests.Guardrails;

/// <summary>
/// 輸出防護的離線測試（docs/m4-implementation-plan.md §6.2、§6.5）：金絲雀與 40 字元重疊被攔、39 字元不攔、
/// 串流逐字餵入時在重疊完整出現前沒有任何字元被放行，以及真實的提示詞與 42 題凍結題組的標準 SQL 不會誤攔。
/// </summary>
public class OutputLeakGuardTests
{
    private const string Canary = "a1b2c3d4e5f60718";

    /// <summary>40 個不重複的字元（每個字元不同，避免在任何位置自行湊出別的重疊）。</summary>
    private static readonly string Secret = new(Enumerable.Range(0x4E00, 40).Select(i => (char)i).ToArray());

    private static OutputLeakGuard Guard() => new(["這是一段不相關的說明。" + Secret + "後面還有一些別的文字。"]);

    /// <summary>輸出含金絲雀（任何大小寫、夾在文字中、被空白包圍）就攔，沒有就放行。</summary>
    [Theory]
    [InlineData("內部代號是 a1b2c3d4e5f60718。", OutputLeakGuard.CanaryReason)]
    [InlineData("A1B2C3D4E5F60718", OutputLeakGuard.CanaryReason)]
    [InlineData("前綴a1b2c3d4e5f60718後綴", OutputLeakGuard.CanaryReason)]
    [InlineData("a1b2c3d4e5f6071", null)] // 少一個字元
    [InlineData("正常的回答，沒有任何機密。", null)]
    public void Canary_IsBlocked(string text, string? expected)
    {
        Assert.Equal(expected, Guard().Check(text, Canary));
    }

    /// <summary>連續 40 個字元與受保護文字相同就攔；39 個不攔；空白差異與大小寫不影響。</summary>
    [Fact]
    public void Overlap_FortyCharsBlocked_ThirtyNineAllowed()
    {
        var guard = Guard();

        Assert.Equal(OutputLeakGuard.OverlapReason, guard.Check("前面的字" + Secret + "後面的字", Canary));
        Assert.Null(guard.Check("前面的字" + Secret[..39] + "後面的字", Canary));
        Assert.Null(guard.Check(Secret[..20] + "不同的字" + Secret[20..], Canary));
    }

    /// <summary>空白被收成單一空格再比對：換行與多個空格不能用來躲過比對。</summary>
    [Fact]
    public void Overlap_IgnoresWhitespaceDifferences_AndCase()
    {
        const string prompt = "Return only one SELECT statement for the question, without any explanation.";
        var guard = new OutputLeakGuard([prompt]);

        Assert.Equal(OutputLeakGuard.OverlapReason, guard.Check("RETURN   only one\nSELECT statement\tfor the QUESTION, without", Canary));
        Assert.Null(guard.Check("Return only one SELECT", Canary));
    }

    /// <summary>空字串與 null 不會出事。</summary>
    [Fact]
    public void EmptyOutput_IsAllowed()
    {
        Assert.Null(Guard().Check(null, Canary));
        Assert.Null(Guard().Check("", Canary));
        Assert.Null(Guard().CheckAll([null, "", "正常"], Canary));
    }

    /// <summary>每次產生的金絲雀都是 16 位小寫十六進位，而且不重複。</summary>
    [Fact]
    public void NewCanary_IsRandomHex()
    {
        var canaries = Enumerable.Range(0, 50).Select(_ => OutputLeakGuard.NewCanary()).ToList();

        Assert.All(canaries, c => Assert.Matches("^[0-9a-f]{16}$", c));
        Assert.Equal(50, canaries.Distinct().Count());
    }

    /// <summary>串流：逐字餵入含 40 字元重疊的回答，在最後一個重疊字元到達時被攔，而且先前放行的文字不含重疊的任何字元。</summary>
    [Fact]
    public void Stream_NeverReleasesAnyCharOfALeak()
    {
        var text = "好的，以下是說明：" + Secret + "以上。";
        var stream = Guard().CreateStream(Canary);
        var released = new StringBuilder();
        string? blocked = null;

        foreach (var ch in text)
        {
            var step = stream.Push(ch.ToString());
            released.Append(step.Release);
            if (step.BlockReason is not null) { blocked = step.BlockReason; break; }
        }

        Assert.Equal(OutputLeakGuard.OverlapReason, blocked);
        Assert.DoesNotContain(Secret[0].ToString(), released.ToString());
        Assert.StartsWith(released.ToString(), "好的，以下是說明：");
    }

    /// <summary>串流：金絲雀被切在兩個片段之間也攔得到，而且它的第一個字元沒有被放行。</summary>
    [Fact]
    public void Stream_CanarySplitAcrossChunks_IsBlocked()
    {
        var stream = Guard().CreateStream(Canary);
        var released = new StringBuilder();

        var a = stream.Push("內部代號是 a1b2c3d4");
        released.Append(a.Release);
        var b = stream.Push("e5f60718，請保密");
        released.Append(b.Release);

        Assert.Null(a.BlockReason);
        Assert.Equal(OutputLeakGuard.CanaryReason, b.BlockReason);
        Assert.DoesNotContain("a1b2", released.ToString());
    }

    /// <summary>串流：沒有洩漏的長回答完整放行（逐段放行的文字加上結尾的剩餘，等於原文），且 39 字元的重疊不攔。</summary>
    [Fact]
    public void Stream_CleanAnswer_IsReleasedInFull()
    {
        var text = "各區基地台數量如下：中央區最多，南湖區其次（" + Secret[..39] + "）。以上是摘要，請參考下方的資料表。";
        var stream = Guard().CreateStream(Canary);
        var released = new StringBuilder();

        foreach (var chunk in text.Chunk(3))
        {
            var step = stream.Push(new string(chunk));
            Assert.Null(step.BlockReason);
            released.Append(step.Release);
        }
        var tail = stream.Complete();
        released.Append(tail.Release);

        Assert.Null(tail.BlockReason);
        Assert.Equal(text, released.ToString());
    }

    /// <summary>串流：回答中的空白不影響保留量的計算（連續空白多的回答，洩漏也一個字都不會先放行）。</summary>
    [Fact]
    public void Stream_WhitespaceHeavyLeak_StillHeldBack()
    {
        var spaced = string.Join("   \n ", Secret.Select(c => c.ToString())); // 每個字之間塞進空白，收合後等於 Secret 逐字加空格
        var guard = new OutputLeakGuard([string.Join(' ', Secret.Select(c => c.ToString()))]);
        var stream = guard.CreateStream(Canary);
        var released = new StringBuilder();
        string? blocked = null;

        foreach (var ch in "開頭" + spaced)
        {
            var step = stream.Push(ch.ToString());
            released.Append(step.Release);
            if (step.BlockReason is not null) { blocked = step.BlockReason; break; }
        }

        Assert.Equal(OutputLeakGuard.OverlapReason, blocked);
        Assert.DoesNotContain(Secret[0].ToString(), released.ToString());
    }

    /// <summary>Complete 之後可以重新開始下一段（工具呼叫把回答切成多段）。</summary>
    [Fact]
    public void Stream_CanBeReusedAfterComplete()
    {
        var stream = Guard().CreateStream(Canary);

        stream.Push("我來查資料庫。");
        var first = stream.Complete();
        var second = stream.Push("結果如下。");
        var rest = stream.Complete();

        Assert.Equal("我來查資料庫。", first.Release);
        Assert.Equal("結果如下。", second.Release + rest.Release);
    }

    /// <summary>
    /// 誤攔檢查：以真實的 Agent 指示與 NL2SQL system 訊息建索引，30 題標準 SQL 與 42 題凍結題組的問題文字都不被攔
    /// （正常查詢不該和提示詞有 40 字元的重疊）。
    /// </summary>
    [Fact]
    public void RealPrompts_DoNotBlockTheFrozenQuestionsOrGoldSql()
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompting", "schema-description.md"));
        var guard = new OutputLeakGuard([GeoAgent.Instructions, GeoAgent.CanaryRule, PromptBuilder.BuildSystemPrompt(schema)]);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", "questions.json")));
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(30, items.Count);
        foreach (var item in items)
        {
            var id = item.GetProperty("id").GetString();
            Assert.True(guard.Check(item.GetProperty("goldSql").GetString(), Canary) is null, $"{id} 的標準 SQL 被誤攔");
            Assert.True(guard.Check(item.GetProperty("question").GetString(), Canary) is null, $"{id} 的問題被誤攔");
        }
    }
}
