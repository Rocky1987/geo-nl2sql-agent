using System.Text.Json;

namespace GeoNl2Sql.Eval.Benchmark;

/// <summary>M5 結果檔與題集共用的 JSON 規則（camelCase、縮排、中文不跳脫）。</summary>
public static class BenchmarkJson
{
    /// <summary>序列化選項。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>一次嘗試的紀錄（不含模型原始回應；原始回應在快取裡）。</summary>
/// <param name="Sql">抽出的 SQL；抽不到為 null。</param>
/// <param name="Failure">失敗類型；成功為 null。</param>
/// <param name="Message">回饋給模型的消毒後訊息；成功為 null。</param>
public sealed record BenchmarkAttempt(string? Sql, string? Failure, string? Message);

/// <summary>結果檔中的一題（docs/m5-implementation-plan.md §4.1）。<b>不記錄查詢結果的資料列</b>，避免把個資寫進公開的結果檔。</summary>
/// <param name="Id">題號。</param>
/// <param name="Category">類別。</param>
/// <param name="Difficulty">難度：easy、medium、hard。</param>
/// <param name="Origin"><c>original</c>（M1 凍結的 30 題）或 <c>new</c>（M5 新增）。</param>
/// <param name="FirstCorrect">第 1 次嘗試是否正確（無修正）。</param>
/// <param name="FirstFailure">第 1 次嘗試的失敗類型。</param>
/// <param name="Correct">最終是否正確（有修正）。</param>
/// <param name="Failure">最終的失敗類型。</param>
/// <param name="Attempts">每次嘗試。</param>
/// <param name="FalseRejects">驗證器誤擋次數。</param>
/// <param name="PermissionDenied">被權限拒絕（錯誤 230）的嘗試數。</param>
/// <param name="ModelCalls">模型呼叫次數。</param>
/// <param name="InputTokens">輸入 Token 合計。</param>
/// <param name="OutputTokens">輸出 Token 合計。</param>
/// <param name="ModelMs">模型呼叫耗時合計（毫秒）。</param>
/// <param name="DbMs">管線內資料庫執行耗時合計（毫秒）。</param>
/// <param name="ModelVersion">供應商回報的模型版本。</param>
public sealed record BenchmarkItem(string Id, string Category, string Difficulty, string Origin,
    bool FirstCorrect, string? FirstFailure, bool Correct, string? Failure, IReadOnlyList<BenchmarkAttempt> Attempts,
    int FalseRejects, int PermissionDenied, int ModelCalls, long InputTokens, long OutputTokens, long ModelMs, long DbMs, string? ModelVersion);

/// <summary>一個（模型、輪次）的結果檔（§4.2 的檔頭＋每題紀錄）。不存 system 訊息原文，只存 SHA-256。</summary>
/// <param name="BenchmarkSha256"><c>benchmark.json</c> 的 SHA-256。</param>
/// <param name="SystemPromptSha256">system 訊息的 SHA-256。</param>
/// <param name="PromptVersion"><c>PromptBuilder</c> 的版本。</param>
/// <param name="MaxCorrections">管線的修正次數上限。</param>
/// <param name="Track"><c>cloud</c> 或 <c>local</c>。</param>
/// <param name="Provider">供應商。</param>
/// <param name="ModelId">模型 ID。</param>
/// <param name="Temperature">溫度（管線的固定值）。</param>
/// <param name="MaxOutputTokens">輸出上限（管線的固定值）。</param>
/// <param name="Run">輪次（從 1 起）。</param>
/// <param name="StartedAt">開始時間。</param>
/// <param name="CacheMode"><c>default</c>、<c>replay</c> 或 <c>fresh</c>。</param>
/// <param name="CacheHits">快取命中次數。</param>
/// <param name="CacheMisses">實際呼叫模型的次數。</param>
/// <param name="Items">每題紀錄。</param>
public sealed record BenchmarkRunFile(string BenchmarkSha256, string SystemPromptSha256, string PromptVersion, int MaxCorrections,
    string Track, string Provider, string ModelId, double Temperature, int MaxOutputTokens, int Run, DateTimeOffset StartedAt,
    string CacheMode, int CacheHits, int CacheMisses, IReadOnlyList<BenchmarkItem> Items);
