using GeoNl2Sql.Core.Audit;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Audit;

/// <summary>
/// <see cref="UsageTrackingChatClient"/> 的離線測試（docs/m4-implementation-plan.md §5.2、§5.4）：
/// 並行請求的 token 各自累計不混入、串流與非串流都算、供應商沒回報用量時為 null 而不是 0。
/// </summary>
public class UsageTrackingTests
{
    /// <summary>
    /// 假的模型：每次呼叫回報的 token 由最後一則使用者訊息的數字決定（輸入＝N、輸出＝2N），
    /// 回報前先隨機讓出執行緒，製造並行請求交錯的機會。<c>reportUsage</c> 為 false 時不回報用量。
    /// </summary>
    private sealed class FakeModel(bool reportUsage = true) : IChatClient
    {
        private static UsageDetails Usage(IEnumerable<ChatMessage> messages)
        {
            var n = long.Parse(messages.Last(m => m.Role == ChatRole.User).Text);
            return new UsageDetails { InputTokenCount = n, OutputTokenCount = 2 * n };
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Random.Shared.Next(1, 8), cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "好")) { Usage = reportUsage ? Usage(messages) : null };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Random.Shared.Next(1, 8), cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "好");
            if (reportUsage) yield return new ChatResponseUpdate { Contents = [new UsageContent(Usage(messages))] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatMessage[] Ask(long n) => [new(ChatRole.User, n.ToString())];

    /// <summary>20 個並行請求，每個請求呼叫模型 3 次（一次串流）；各自的次數與 token 合計正確，彼此不混入。</summary>
    [Fact]
    public async Task ConcurrentRequests_AccumulateTheirOwnTotals()
    {
        var client = new UsageTrackingChatClient(new FakeModel());

        var tasks = Enumerable.Range(1, 20).Select(i => Task.Run(async () =>
        {
            var scope = UsageScope.Begin();
            await client.GetResponseAsync(Ask(i));
            await foreach (var _ in client.GetStreamingResponseAsync(Ask(i * 10))) { }
            await client.GetResponseAsync(Ask(i * 100));
            return (i, scope);
        })).ToList();

        foreach (var (i, scope) in await Task.WhenAll(tasks))
        {
            var input = i + i * 10L + i * 100L;
            Assert.Equal(3, scope.ModelCalls);
            Assert.Equal(input, scope.InputTokens);
            Assert.Equal(2 * input, scope.OutputTokens);
        }
    }

    /// <summary>供應商沒有回報用量：次數照算，token 是 null（不估算、不當成 0）。</summary>
    [Fact]
    public async Task NoUsageReported_TokensAreNull()
    {
        var client = new UsageTrackingChatClient(new FakeModel(reportUsage: false));
        var scope = UsageScope.Begin();

        await client.GetResponseAsync(Ask(5));
        await foreach (var _ in client.GetStreamingResponseAsync(Ask(5))) { }

        Assert.Equal(2, scope.ModelCalls);
        Assert.Null(scope.InputTokens);
        Assert.Null(scope.OutputTokens);
    }

    /// <summary>沒有開始範圍（例如評估程式直接用用戶端）：照常呼叫，不拋例外。</summary>
    [Fact]
    public async Task WithoutScope_JustPassesThrough()
    {
        var client = new UsageTrackingChatClient(new FakeModel());

        var response = await Task.Run(() => client.GetResponseAsync(Ask(5)));

        Assert.Equal("好", response.Text);
    }
}
