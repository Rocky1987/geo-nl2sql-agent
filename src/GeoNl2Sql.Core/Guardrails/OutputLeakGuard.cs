using System.Security.Cryptography;
using System.Text;

namespace GeoNl2Sql.Core.Guardrails;

/// <summary>
/// 輸出防護：擋下把系統提示詞洩漏給使用者的輸出（docs/m4-implementation-plan.md §6.2）。兩種檢查，使用者看得到的輸出都要過：
/// <list type="bullet">
/// <item><b>金絲雀</b>：每次請求在 Agent 指示中放一個隨機字串並寫明不得輸出，輸出裡出現它就攔截。</item>
/// <item><b>逐字重疊</b>：輸出含有受保護文字（Agent 指示、NL2SQL 的 system 訊息）中<b>任何連續 <see cref="OverlapLength"/> 個字元</b>
/// （空白收成單一空格、轉小寫後比對）就攔截。這擋的是「把 schema 說明原文當字串常值 SELECT 出來」這類繞道。</item>
/// </list>
/// 限制（明講）：改寫、翻譯、摘要式的洩漏不會被逐字比對抓到。
/// 本類別建立一次後可跨請求共用（受保護文字的 40 字元索引在建構時算好，之後唯讀）；每次請求用自己的金絲雀。
/// 用法：<c>var guard = new OutputLeakGuard([instructions, nl2sqlPrompt]); var reason = guard.Check(answer, canary);</c>
/// 串流用 <see cref="CreateStream"/>。
/// </summary>
public sealed class OutputLeakGuard
{
    /// <summary>逐字重疊的長度門檻（字元）：連續這麼多字元相同就攔截；少一個字元就不攔。</summary>
    public const int OverlapLength = 40;

    /// <summary>金絲雀的長度（十六進位字元數）。</summary>
    public const int CanaryLength = 16;

    /// <summary>輸出被攔下時回給使用者的固定訊息。</summary>
    public const string BlockedMessage = "回答包含不應公開的系統內部內容，已拒絕輸出。";

    /// <summary>攔截原因：出現金絲雀。</summary>
    public const string CanaryReason = "canary";

    /// <summary>攔截原因：與受保護文字有連續 <see cref="OverlapLength"/> 字元重疊。</summary>
    public const string OverlapReason = "overlap";

    private readonly HashSet<string> _grams = new(StringComparer.Ordinal);

    /// <summary>
    /// 建立防護並為受保護文字建好索引。
    /// </summary>
    /// <param name="protectedTexts">不得洩漏的文字（Agent 指示的樣板、NL2SQL 的 system 訊息）。</param>
    public OutputLeakGuard(IEnumerable<string> protectedTexts)
    {
        foreach (var text in protectedTexts)
        {
            var (collapsed, _) = Collapse(text);
            for (var i = 0; i + OverlapLength <= collapsed.Length; i++)
                _grams.Add(collapsed.Substring(i, OverlapLength));
        }
    }

    /// <summary>產生一個隨機的金絲雀（<see cref="CanaryLength"/> 位十六進位小寫字元）。</summary>
    public static string NewCanary() => Convert.ToHexString(RandomNumberGenerator.GetBytes(CanaryLength / 2)).ToLowerInvariant();

    /// <summary>
    /// 檢查一段完整的輸出。
    /// </summary>
    /// <param name="text">使用者看得到的輸出（回答、儲存格、SQL）；可為 null。</param>
    /// <param name="canary">本次請求的金絲雀。</param>
    /// <returns><see cref="CanaryReason"/>、<see cref="OverlapReason"/>，沒問題為 null。</returns>
    public string? Check(string? text, string canary)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var (collapsed, _) = Collapse(text);
        return Inspect(collapsed, canary, 0);
    }

    /// <summary>檢查多段輸出，回傳第一個被攔下的原因。</summary>
    /// <param name="texts">要檢查的輸出。</param>
    /// <param name="canary">本次請求的金絲雀。</param>
    public string? CheckAll(IEnumerable<string?> texts, string canary)
    {
        foreach (var text in texts)
            if (Check(text, canary) is { } reason) return reason;
        return null;
    }

    /// <summary>為串流的回答建立逐段過濾器。</summary>
    /// <param name="canary">本次請求的金絲雀。</param>
    public LeakStream CreateStream(string canary) => new(this, canary);

    /// <summary>
    /// 在已正規化的文字中檢查金絲雀與重疊。重疊只掃描「結尾落在 <paramref name="scanFrom"/> 之後」的視窗（先前的部分已檢查過）。
    /// </summary>
    /// <param name="collapsed">已正規化（空白收合、轉小寫）的文字。</param>
    /// <param name="canary">本次請求的金絲雀。</param>
    /// <param name="scanFrom">先前已掃描到的正規化文字長度；第一次為 0。</param>
    /// <returns>攔截原因；沒問題為 null。</returns>
    private string? Inspect(string collapsed, string canary, int scanFrom)
    {
        if (canary.Length > 0 && collapsed.Contains(canary, StringComparison.OrdinalIgnoreCase)) return CanaryReason;
        var lookup = _grams.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var i = Math.Max(0, scanFrom - (OverlapLength - 1)); i + OverlapLength <= collapsed.Length; i++)
            if (lookup.Contains(collapsed.AsSpan(i, OverlapLength))) return OverlapReason;
        return null;
    }

    /// <summary>空白（含換行）收成單一空格並轉小寫；同時回傳每個正規化字元在原文中的位置。</summary>
    private static (string Text, int[] Map) Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] == ' ') continue;
                sb.Append(' ');
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            map.Add(i);
        }
        return (sb.ToString(), [.. map]);
    }

    /// <summary>串流過濾器放行一步的結果。</summary>
    /// <param name="Release">可以送出的文字（可能為空）。</param>
    /// <param name="BlockReason">被攔下的原因；null 表示沒問題。被攔下時 <paramref name="Release"/> 為空，之後不應再送任何文字。</param>
    public readonly record struct StreamStep(string Release, string? BlockReason);

    /// <summary>
    /// 串流回答的逐段過濾器：收到的文字會暫時保留尾端 <see cref="OverlapLength"/> 個（正規化後的）字元不送出。
    /// 一段 40 字元的重疊在最後一個字元到達時就會被比對到，此時它的第一個字元還在保留區，所以逐字洩漏<b>一個字都不會先送出</b>。
    /// 一個實例對應一段連續的回答；工具呼叫把回答切成多段時，每段結束都要呼叫 <see cref="Complete"/>。
    /// </summary>
    public sealed class LeakStream
    {
        private readonly OutputLeakGuard _guard;
        private readonly string _canary;
        private readonly StringBuilder _seen = new();
        private int _released;
        private int _scanned;

        internal LeakStream(OutputLeakGuard guard, string canary)
        {
            _guard = guard;
            _canary = canary;
        }

        /// <summary>收到一段新文字。</summary>
        /// <param name="delta">模型這一次吐出的文字。</param>
        /// <returns>可以送出的文字，或被攔下的原因。</returns>
        public StreamStep Push(string delta)
        {
            _seen.Append(delta);
            return Step(final: false);
        }

        /// <summary>這一段回答結束：把保留區的最後檢查完並放行，然後重設，可接著處理下一段。</summary>
        /// <returns>剩下可以送出的文字，或被攔下的原因。</returns>
        public StreamStep Complete()
        {
            var step = Step(final: true);
            _seen.Clear();
            _released = 0;
            _scanned = 0;
            return step;
        }

        private StreamStep Step(bool final)
        {
            var text = _seen.ToString();
            var (collapsed, map) = Collapse(text);
            if (_guard.Inspect(collapsed, _canary, _scanned) is { } reason) return new StreamStep("", reason);
            _scanned = collapsed.Length;

            var end = final ? text.Length : collapsed.Length > OverlapLength ? map[collapsed.Length - OverlapLength] : 0;
            end = Math.Max(end, _released);
            var release = text[_released..end];
            _released = end;
            return new StreamStep(release, null);
        }
    }
}
