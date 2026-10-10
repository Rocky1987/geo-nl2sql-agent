namespace GeoNl2Sql.Core.Audit;

/// <summary>稽核紀錄的 <c>Outcome</c> 欄位值（docs/m4-implementation-plan.md §5.1）。</summary>
public static class AuditOutcome
{
    /// <summary>正常回答。</summary>
    public const string Success = "success";

    /// <summary>查詢沒有成功（SQL 被拒絕或執行失敗）。</summary>
    public const string Failed = "failed";

    /// <summary>問題被輸入偵測擋下，沒有呼叫模型。</summary>
    public const string BlockedInput = "blocked_input";

    /// <summary>回答被輸出防護攔下。</summary>
    public const string BlockedOutput = "blocked_output";

    /// <summary>超過工具輪數上限。</summary>
    public const string Limit = "limit";

    /// <summary>模型服務出錯。</summary>
    public const string ModelError = "model_error";

    /// <summary>請求不合法（空問題、過長、角色不合法），沒有進到 Agent。</summary>
    public const string Invalid = "invalid";
}

/// <summary>
/// 一次請求的稽核紀錄，對應資料表 <c>audit.QueryLog</c> 的一列（欄位意義見 db/05_audit.sql）。
/// 不含結果的資料列（避免稽核表變成個資外洩點）。
/// </summary>
public sealed record AuditRecord
{
    /// <summary><see cref="Question"/>、<see cref="AnswerExcerpt"/> 的長度上限（字元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary><see cref="Role"/> 的長度上限（字元）。</summary>
    public const int MaxRoleLength = 50;

    /// <summary>請求發生時間（UTC）。</summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>整個請求的耗時（毫秒）。</summary>
    public int DurationMs { get; init; }

    /// <summary>請求選擇的角色（自選，不是驗證過的身分）；不合法的請求記原文。</summary>
    public string Role { get; init; } = "";

    /// <summary>使用者的問題原文（最多 <see cref="MaxTextLength"/> 字元）。</summary>
    public string Question { get; init; } = "";

    /// <summary><see cref="AuditOutcome"/> 的其中一個值。</summary>
    public string Outcome { get; init; } = AuditOutcome.Success;

    /// <summary>命中的偵測規則或輸出防護的原因；未命中為 null。</summary>
    public string? GuardRule { get; init; }

    /// <summary>呼叫過的工具與參數（JSON）；沒呼叫為 null。</summary>
    public string? ToolCalls { get; init; }

    /// <summary>最後一次 <c>query_database</c> 的 SQL；沒查過為 null。</summary>
    public string? Sql { get; init; }

    /// <summary>最後一次 <c>query_database</c> 的 NL2SQL 嘗試次數；沒查過為 null。</summary>
    public int? Attempts { get; init; }

    /// <summary>結果列數；沒有結果為 null。</summary>
    public int? RowCount { get; init; }

    /// <summary>結果是否被截斷；沒有結果為 null。</summary>
    public bool? Truncated { get; init; }

    /// <summary>工具結果清理掉的儲存格數。</summary>
    public int ScrubbedCells { get; init; }

    /// <summary>本次請求所有模型呼叫的次數（含 NL2SQL 管線內的呼叫）。</summary>
    public int ModelCalls { get; init; }

    /// <summary>輸入 token 合計；供應商沒回報用量時為 null（不估算）。</summary>
    public long? InputTokens { get; init; }

    /// <summary>輸出 token 合計；供應商沒回報用量時為 null（不估算）。</summary>
    public long? OutputTokens { get; init; }

    /// <summary>模型供應商（<see cref="ModelProvider"/> 的名稱）。</summary>
    public string Provider { get; init; } = "";

    /// <summary>模型識別字串。</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Agent 提示詞版本。</summary>
    public string AgentPromptVersion { get; init; } = "";

    /// <summary>NL2SQL 提示詞版本。</summary>
    public string Nl2SqlPromptVersion { get; init; } = "";

    /// <summary>輸入偵測規則版本；尚未啟用偵測為 null。</summary>
    public string? DetectorVersion { get; init; }

    /// <summary>回答的前 <see cref="MaxTextLength"/> 字元；沒有回答為 null。</summary>
    public string? AnswerExcerpt { get; init; }

    /// <summary>取字串的前 <paramref name="max"/> 個字元（以 Unicode 字元為單位，不會從代理對中間切開）。</summary>
    /// <param name="text">原文；可為 null。</param>
    /// <param name="max">長度上限。</param>
    /// <returns>截斷後的文字；<paramref name="text"/> 為 null 時回傳 null。</returns>
    public static string? Clip(string? text, int max)
    {
        if (text is null || text.Length <= max) return text;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }
}
