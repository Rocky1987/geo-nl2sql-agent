using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Eval.Common;

/// <summary><c>--replay</c> 模式下快取裡沒有對應的模型回應。</summary>
/// <param name="tag">缺少回應的題號。</param>
public sealed class CacheMissException(string tag) : Exception($"快取中沒有 {tag} 的模型回應（--replay 不呼叫模型）。")
{
    /// <summary>缺少回應的題號。</summary>
    public string Tag { get; } = tag;
}

/// <summary>快取檔的一筆（JSON Lines 的一行）。不存 system 訊息原文與查詢結果，只存模型回應。</summary>
/// <param name="Key">請求的 SHA-256（見 <see cref="CachingChatClient"/>）。</param>
/// <param name="Id">寫入時的題號，方便人工查閱；不參與比對。</param>
/// <param name="Text">模型回應文字。</param>
/// <param name="InputTokens">輸入 Token 數（供應商有回報時）。</param>
/// <param name="OutputTokens">輸出 Token 數（供應商有回報時）。</param>
/// <param name="ElapsedMs">當次實測的呼叫耗時（毫秒）。</param>
/// <param name="ModelVersion">供應商回報的模型版本。</param>
/// <param name="StatusCode">模型服務回了 HTTP 錯誤時的狀態碼；正常回應為 null。</param>
/// <param name="Error">HTTP 錯誤的訊息；正常回應為 null。</param>
public sealed record CacheEntry(string Key, string Id, string Text, long? InputTokens, long? OutputTokens, long ElapsedMs,
    string? ModelVersion, int? StatusCode = null, string? Error = null);

/// <summary>
/// 模型回應的快取（docs/m5-implementation-plan.md §2.4），<b>只給評估程式使用</b>，包在模型用戶端外面。
/// 鍵是 SHA-256（供應商、模型 ID、輪次、每則訊息的角色與全文、溫度、輸出上限）：提示詞或 schema 說明一改鍵就不同；
/// 輪次在鍵裡，第 2、3 輪才不會直接拿到第 1 輪的回應。每個（模型、輪次）一個 JSON Lines 檔，每次實際呼叫後立即附加一行，
/// 中斷後重跑不重複付費。命中時回傳存下的文字、用量、耗時（放在 <see cref="ElapsedMsKey"/>）與模型版本，與實際呼叫時回傳的內容相同。
/// 用法：每題開始前設定 <see cref="Tag"/>；<c>innerFactory</c> 為 null 即 <c>--replay</c>（缺快取就丟 <see cref="CacheMissException"/>）。
/// </summary>
public sealed class CachingChatClient : IChatClient
{
    /// <summary>回應的 <c>AdditionalProperties</c> 中存放呼叫耗時（毫秒，long）的鍵。</summary>
    public const string ElapsedMsKey = "elapsedMs";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly string _provider;
    private readonly string _modelId;
    private readonly int _run;
    private readonly Func<IChatClient>? _innerFactory;
    private readonly bool _recordServerErrors;
    private readonly Dictionary<string, CacheEntry> _entries = [];
    private IChatClient? _inner;

    /// <summary>建立快取用戶端，並讀入既有的快取檔。</summary>
    /// <param name="file">這個（模型、輪次）的快取檔路徑；不存在時第一次寫入才建立。</param>
    /// <param name="provider">供應商名稱（進入鍵）。</param>
    /// <param name="modelId">模型 ID（進入鍵）。</param>
    /// <param name="run">輪次（進入鍵）。</param>
    /// <param name="innerFactory">快取沒有時用來建立實際用戶端的函式，第一次未命中才呼叫（快取完整時不需要金鑰）；null 表示只讀快取。本類別不負責釋放它建立的用戶端。</param>
    /// <param name="recordServerErrors">true 時，模型服務回 HTTP 錯誤（有狀態碼，例如本機 Ollama 的 500）也寫入快取，重播時丟出相同的例外；連線失敗等沒有狀態碼的錯誤一律不寫。</param>
    public CachingChatClient(string file, string provider, string modelId, int run, Func<IChatClient>? innerFactory, bool recordServerErrors = false)
    {
        _file = file;
        _provider = provider;
        _modelId = modelId;
        _run = run;
        _innerFactory = innerFactory;
        _recordServerErrors = recordServerErrors;
        if (File.Exists(file))
            foreach (var line in File.ReadLines(file).Where(l => l.Length > 0))
            {
                var entry = JsonSerializer.Deserialize<CacheEntry>(line, Json)!;
                _entries[entry.Key] = entry;
            }
    }

    /// <summary>目前的題號，寫進新的快取行與 <see cref="CacheMissException"/>。</summary>
    public string Tag { get; set; } = "";

    /// <summary>命中次數。</summary>
    public int Hits { get; private set; }

    /// <summary>實際呼叫模型的次數。</summary>
    public int Misses { get; private set; }

    /// <inheritdoc />
    /// <exception cref="CacheMissException">只讀快取且沒有對應的回應。</exception>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var key = Key(list, options);
        if (_entries.TryGetValue(key, out var hit))
        {
            Hits++;
            return ToResponse(hit);
        }
        if (_innerFactory is null) throw new CacheMissException(Tag);

        _inner ??= _innerFactory();
        var sw = Stopwatch.StartNew();
        CacheEntry entry;
        try
        {
            var response = await _inner.GetResponseAsync(list, options, cancellationToken);
            entry = new CacheEntry(key, Tag, response.Text, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
                sw.ElapsedMilliseconds, response.ModelId);
        }
        catch (HttpRequestException ex) when (_recordServerErrors && ex.StatusCode is not null)
        {
            entry = new CacheEntry(key, Tag, "", null, null, sw.ElapsedMilliseconds, null, (int)ex.StatusCode, ex.Message);
        }
        Misses++;
        _entries[key] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        await File.AppendAllTextAsync(_file, JsonSerializer.Serialize(entry, Json) + "\n", cancellationToken);
        return ToResponse(entry);
    }

    /// <summary>計算請求的鍵：把鍵的各個成分序列化成 JSON 後取 SHA-256。</summary>
    /// <param name="messages">要送出的訊息。</param>
    /// <param name="options">請求選項（取溫度與輸出上限）。</param>
    private string Key(List<ChatMessage> messages, ChatOptions? options)
    {
        var material = JsonSerializer.Serialize(new
        {
            provider = _provider, modelId = _modelId, run = _run,
            temperature = options?.Temperature, maxOutputTokens = options?.MaxOutputTokens,
            messages = messages.Select(m => new { role = m.Role.Value, text = m.Text }),
        }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>把快取行還原成回應；記錄的是 HTTP 錯誤時丟出相同狀態碼的例外。</summary>
    /// <param name="entry">快取行。</param>
    private static ChatResponse ToResponse(CacheEntry entry)
    {
        if (entry.StatusCode is { } status) throw new HttpRequestException(entry.Error, null, (HttpStatusCode)status);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, entry.Text))
        {
            ModelId = entry.ModelVersion,
            Usage = new UsageDetails { InputTokenCount = entry.InputTokens, OutputTokenCount = entry.OutputTokens },
            AdditionalProperties = new() { [ElapsedMsKey] = entry.ElapsedMs },
        };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("評估只使用非串流呼叫。");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
