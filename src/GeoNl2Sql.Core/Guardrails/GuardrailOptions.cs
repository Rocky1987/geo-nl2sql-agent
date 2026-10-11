namespace GeoNl2Sql.Core.Guardrails;

/// <summary>
/// 提示詞注入護欄的開關，<b>只給評估程式（攻擊測試的 B 輪）與測試用的建構參數</b>：
/// Web 不從設定讀取它，部署時沒有辦法把護欄關掉（docs/m4-implementation-plan.md §7.4）。
/// 關掉的只有三道機率性或規則式的護欄；資料庫權限、SQL 驗證器、個資遮蔽、稽核永遠開著。
/// </summary>
/// <param name="InputDetection">輸入偵測（<see cref="PromptInjectionDetector"/>）。</param>
/// <param name="OutputGuard">輸出防護（金絲雀與逐字重疊，<see cref="OutputLeakGuard"/>）；關掉時 Agent 指示也不帶金絲雀。</param>
/// <param name="ScrubToolResults">工具結果清理（資料庫來的疑似指令字串不交給模型）。</param>
public sealed record GuardrailOptions(bool InputDetection = true, bool OutputGuard = true, bool ScrubToolResults = true)
{
    /// <summary>全開（Web 與預設行為）。</summary>
    public static GuardrailOptions Default { get; } = new();

    /// <summary>三道護欄全關（攻擊測試 B 輪：只剩資料庫與驗證器）。</summary>
    public static GuardrailOptions None { get; } = new(false, false, false);
}
