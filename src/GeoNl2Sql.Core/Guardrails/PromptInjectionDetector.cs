using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GeoNl2Sql.Core.Guardrails;

/// <summary>
/// 提示詞注入的輸入偵測（docs/m4-implementation-plan.md §6.1）：規則式、確定性，不呼叫模型。
/// 同一份規則也用來檢查工具結果裡的字串儲存格（間接注入，§6.3）。
/// 先正規化再比對：Unicode 相容正規化（全形轉半形）、轉小寫、去掉零寬與其他格式字元；
/// 規則分兩種比對對象——「緊湊」文字（再去掉全部空白，擋拆字與多餘空白）與「分詞」文字（空白收成單一空格，需要字詞邊界的規則用）。
/// 規則以 <see cref="Version"/> 標記；改規則要遞增版本，並重新量測。
/// 這是示範等級的防線，不是完整的防護：改寫、翻譯、編碼與多輪鋪陳的攻擊會漏掉，攻擊測試（S5）如實回報。
/// 用法：<c>if (PromptInjectionDetector.Detect(question) is { } rule) { /* 拒絕，規則代號為 rule */ }</c>。
/// </summary>
public static class PromptInjectionDetector
{
    /// <summary>規則版本，寫進稽核紀錄。</summary>
    public const string Version = "r1";

    /// <summary>輸入被擋下時回給使用者的固定訊息。</summary>
    public const string RejectMessage = "這個問題看起來像是要改變系統規則或取得系統內部資訊，已拒絕處理。";

    /// <summary>工具結果中被移除的儲存格在給模型的摘要裡顯示的文字。</summary>
    public const string ScrubbedPlaceholder = "[已移除疑似指令的內容]";

    // 英文識別字的邊界：前後都不是英數字或底線（\b 在中英文相鄰時不成立，因為中文字也算 \w）。
    private const string B = "(?<![a-z0-9_])";
    private const string E = "(?![a-z0-9_])";

    /// <summary>一條規則。<paramref name="Compact"/> 為 true 時比對去掉空白的文字，否則比對分詞文字。</summary>
    private sealed record Rule(string Id, Regex Pattern, bool Compact);

    private static Rule R(string id, bool compact, string pattern) =>
        new(id, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)), compact);

    private static readonly Rule[] Rules =
    [
        // 覆寫指示
        R("override-zh", true, "(忽略|無視|忽視|忘記|忘掉|不要理會|不理會|略過|跳過|覆蓋|取代)(先前|之前|以上|上面|上述|前面|原本|既有|所有|全部|任何|你的|這些|那些)+的?(指示|指令|規則|設定|限制|提示|約束)"),
        R("override-en", true, "(ignore|disregard|forget|override|bypass)(all|any|the|your|my|previous|prior|above|earlier|preceding|former|these|those|of)*(instructions?|prompts?|rules?|directions?|guidelines?|restrictions?|constraints?)"),

        // 角色扮演與越獄
        R("jailbreak-zh", true, "開發者模式|開發模式|沒有任何(限制|規則|道德)|不受(任何)?(限制|規則|約束)|解除(所有|全部|任何)?限制|無限制模式|越獄|假裝(你|自己)(是|沒有|可以|不)|扮演(一個|一位)?(不受|沒有|無)"),
        R("jailbreak-en", false, $"{B}(dan|jailbreak(en)?|developer mode|dev mode|god mode|do anything now){E}|pretend (you are|to be|you're)|you are now (a|an|in)|act as (an? )?(unrestricted|unfiltered|jailbroken)|without (any )?(restrictions|limitations|rules|filters)"),

        // 要求提示詞（含金絲雀）
        R("prompt-zh", true, "系統(提示|指示|訊息|指令|設定)|(你的|上面的|以上的|初始|最初)(指示|指令|提示詞?|規則)|(重複|複述|輸出|印出|列印|逐字|原文)(上面|以上|前面|開頭)的?(所有)?(內容|文字|訊息|指示|對話)|金絲雀|canary"),
        R("prompt-en", true, "systemprompt|systemmessage|systeminstructions?|initialprompt|hiddenprompt|yourinstructions|yourprompt|yourrules|yourguidelines|(repeat|print|reveal|output|recite)(everything|all|the|text|words)*(above|before)"),

        // 要求寫入或系統物件
        R("write-sql", false, $"{B}(drop|truncate|alter) +(table|database|view|schema|login|user|index){E}|{B}delete +from{E}|{B}insert +into{E}|{B}update +\\S+ +set{E}|{B}create +(table|login|user|database){E}|{B}(xp|sp)_\\w+|{B}exec(ute)? *\\(|{B}shutdown{E}|{B}grant +\\w+ +on{E}"),
        R("write-zh", true, "(刪除|清空|刪掉|移除|修改|竄改|更改)(所有|全部|整張|整個|每一筆)的?(客戶|資料表|表格|資料庫|資料|紀錄|基地台)|寫入(資料庫|資料表)|(新增|插入)一(筆|個)(客戶|基地台|紀錄)"),
        R("system-object", false, $"{B}(sys|information_schema|master|msdb|tempdb)\\.\\w+|{B}(sysobjects|syscolumns|syslogins|sysdatabases){E}"),
    ];

    /// <summary>
    /// 檢查一段文字。
    /// </summary>
    /// <param name="text">使用者的問題，或工具結果裡的一個字串儲存格。</param>
    /// <returns>命中的規則代號（例如 <c>override-zh</c>）；沒命中或文字為空為 null。</returns>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var (spaced, compact) = Normalize(text);
        foreach (var rule in Rules)
            if (rule.Pattern.IsMatch(rule.Compact ? compact : spaced)) return rule.Id;
        return null;
    }

    /// <summary>
    /// 正規化：Unicode 相容正規化（全形轉半形）、轉小寫、去掉格式字元（零寬空白、零寬連字等）。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <returns><c>Spaced</c>：空白收成單一空格；<c>Compact</c>：完全去掉空白。</returns>
    private static (string Spaced, string Compact) Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var spaced = new StringBuilder(folded.Length);
        var compact = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c))
            {
                if (spaced.Length > 0 && spaced[^1] != ' ') spaced.Append(' ');
                continue;
            }
            spaced.Append(c);
            compact.Append(c);
        }
        return (spaced.ToString().Trim(), compact.ToString());
    }
}
