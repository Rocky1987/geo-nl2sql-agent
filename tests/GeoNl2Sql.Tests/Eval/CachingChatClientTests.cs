using System.Net;
using GeoNl2Sql.Eval.Common;
using Microsoft.Extensions.AI;
// Microsoft.Extensions.AI 也有同名的抽象類別，這裡測的是評估用的快取。
using CachingChatClient = GeoNl2Sql.Eval.Common.CachingChatClient;

namespace GeoNl2Sql.Tests.Eval;

/// <summary>
/// 評估用快取的離線測試（docs/m5-implementation-plan.md §2.4、§4.5）：相同請求命中、輪次或訊息不同就不命中、
/// <c>--replay</c> 缺快取時丟例外並帶題號、命中時回傳存下的用量與耗時，且快取檔不含 system 訊息原文。
/// </summary>
public sealed class CachingChatClientTests : IDisposable
{
    /// <summary>每個測試獨立的暫存目錄。</summary>
    private readonly string _dir = Directory.CreateTempSubdirectory("geo-cache-").FullName;

    /// <summary>刪除暫存目錄。</summary>
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>計算呼叫次數的假模型：第 N 次回「回應 N」，輸入 Token 為 100＋N；可設定改為丟出例外。</summary>
    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public Exception? Throw { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"回應 {Calls}"))
            {
                ModelId = "claude-haiku-4-5-20251001",
                Usage = new UsageDetails { InputTokenCount = 100 + Calls, OutputTokenCount = 10 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private const string SystemText = "這是 system 訊息原文，不應寫進快取檔";
    private static readonly ChatOptions Options = new() { Temperature = 0, MaxOutputTokens = 1024 };

    private static List<ChatMessage> Messages(string question) => [new(ChatRole.System, SystemText), new(ChatRole.User, question)];

    /// <summary>建立快取用戶端；<paramref name="inner"/> 為 null 即只讀快取（--replay）。</summary>
    private CachingChatClient Create(CountingChatClient? inner, int run = 1, string file = "cache.jsonl", bool recordServerErrors = false) =>
        new(Path.Combine(_dir, file), "Anthropic", "claude-haiku-4-5", run, inner is null ? null : () => inner, recordServerErrors);

    /// <summary>相同請求第二次命中，不再呼叫模型，回應相同。</summary>
    [Fact]
    public async Task SameRequest_Hits()
    {
        var inner = new CountingChatClient();
        var cache = Create(inner);

        var first = await cache.GetResponseAsync(Messages("中央區有幾座基地台？"), Options);
        var second = await cache.GetResponseAsync(Messages("中央區有幾座基地台？"), Options);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal((1, 1), (cache.Misses, cache.Hits));
    }

    /// <summary>輪次在鍵裡：即使讀同一個快取檔，第 2 輪也不會拿到第 1 輪的回應。</summary>
    [Fact]
    public async Task DifferentRun_Misses()
    {
        var inner = new CountingChatClient();

        await Create(inner, run: 1).GetResponseAsync(Messages("q"), Options);
        var second = await Create(inner, run: 2).GetResponseAsync(Messages("q"), Options);

        Assert.Equal(2, inner.Calls);
        Assert.Equal("回應 2", second.Text);
    }

    /// <summary>訊息差一個字（全形問號改半形）就不命中。</summary>
    [Fact]
    public async Task MessageDifferingByOneCharacter_Misses()
    {
        var inner = new CountingChatClient();
        var cache = Create(inner);

        await cache.GetResponseAsync(Messages("中央區有幾座基地台？"), Options);
        await cache.GetResponseAsync(Messages("中央區有幾座基地台?"), Options);

        Assert.Equal(2, inner.Calls);
    }

    /// <summary>只讀快取時缺少回應：丟出例外，例外帶有題號。</summary>
    [Fact]
    public async Task Replay_MissingEntry_ThrowsWithQuestionId()
    {
        var replay = Create(inner: null);
        replay.Tag = "S09";

        var ex = await Assert.ThrowsAsync<CacheMissException>(() => replay.GetResponseAsync(Messages("q"), Options));

        Assert.Equal("S09", ex.Tag);
        Assert.Contains("S09", ex.Message);
    }

    /// <summary>另一個實例（重播）讀檔命中時，回傳當初存下的文字、用量、耗時與模型版本；快取檔不含 system 訊息原文。</summary>
    [Fact]
    public async Task Hit_FromFile_ReturnsStoredUsageElapsedAndModelVersion()
    {
        var inner = new CountingChatClient();
        var live = Create(inner);
        live.Tag = "J04";
        var original = await live.GetResponseAsync(Messages("q"), Options);

        var replayed = await Create(inner: null).GetResponseAsync(Messages("q"), Options);

        Assert.Equal(1, inner.Calls);
        Assert.Equal("回應 1", replayed.Text);
        Assert.Equal(101, replayed.Usage!.InputTokenCount);
        Assert.Equal(10, replayed.Usage.OutputTokenCount);
        Assert.Equal("claude-haiku-4-5-20251001", replayed.ModelId);
        Assert.Equal(original.AdditionalProperties![CachingChatClient.ElapsedMsKey], replayed.AdditionalProperties![CachingChatClient.ElapsedMsKey]);
        Assert.IsType<long>(replayed.AdditionalProperties[CachingChatClient.ElapsedMsKey]);
        var file = File.ReadAllText(Path.Combine(_dir, "cache.jsonl"));
        Assert.Contains("\"id\":\"J04\"", file);
        Assert.DoesNotContain(SystemText, file);
    }

    /// <summary>允許記錄時，模型服務的 HTTP 錯誤（有狀態碼）寫進快取，重播丟出相同狀態碼；連線失敗（沒有狀態碼）不寫。</summary>
    [Fact]
    public async Task ServerError_IsRecordedOnlyWithStatusCode()
    {
        var failing = new CountingChatClient { Throw = new HttpRequestException("500", null, HttpStatusCode.InternalServerError) };
        var refused = new CountingChatClient { Throw = new HttpRequestException("connection refused") };

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(failing, file: "a.jsonl", recordServerErrors: true).GetResponseAsync(Messages("q"), Options));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(refused, file: "b.jsonl", recordServerErrors: true).GetResponseAsync(Messages("q"), Options));

        var replayed = await Assert.ThrowsAsync<HttpRequestException>(() => Create(null, file: "a.jsonl").GetResponseAsync(Messages("q"), Options));
        Assert.Equal(HttpStatusCode.InternalServerError, replayed.StatusCode);
        await Assert.ThrowsAsync<CacheMissException>(() => Create(null, file: "b.jsonl").GetResponseAsync(Messages("q"), Options));
    }
}
