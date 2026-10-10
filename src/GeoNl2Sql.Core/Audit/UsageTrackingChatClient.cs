using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Core.Audit;

/// <summary>
/// 單次請求內所有模型呼叫的累計（次數與 token）。用 <see cref="Begin"/> 在請求開始時建立，
/// 經 <see cref="AsyncLocal{T}"/> 傳到該請求之下的所有非同步呼叫，所以並行請求各自累計、互不混入。
/// </summary>
public sealed class UsageScope
{
    private static readonly AsyncLocal<UsageScope?> CurrentScope = new();

    private int _calls;
    private long _input;
    private long _output;
    private int _reported;

    /// <summary>目前非同步流程所屬的範圍；沒有開始過為 null。</summary>
    public static UsageScope? Current => CurrentScope.Value;

    /// <summary>
    /// 建立新範圍並設為目前流程的範圍。必須在請求最外層的同步段落呼叫（不要在 async 迭代器內），
    /// 這樣範圍才會留在呼叫端的執行內容，之後每次繼續執行都看得到。
    /// </summary>
    public static UsageScope Begin()
    {
        var scope = new UsageScope();
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>模型呼叫次數。</summary>
    public int ModelCalls => Volatile.Read(ref _calls);

    /// <summary>輸入 token 合計；沒有任何一次呼叫回報用量時為 null。</summary>
    public long? InputTokens => Volatile.Read(ref _reported) == 0 ? null : Interlocked.Read(ref _input);

    /// <summary>輸出 token 合計；沒有任何一次呼叫回報用量時為 null。</summary>
    public long? OutputTokens => Volatile.Read(ref _reported) == 0 ? null : Interlocked.Read(ref _output);

    /// <summary>記錄一次模型呼叫。</summary>
    internal void AddCall() => Interlocked.Increment(ref _calls);

    /// <summary>累計一次呼叫回報的用量；兩個欄位都沒有值就不算「有回報」。</summary>
    /// <param name="usage">供應商回報的用量；可為 null。</param>
    internal void AddUsage(UsageDetails? usage)
    {
        if (usage is null || (usage.InputTokenCount is null && usage.OutputTokenCount is null)) return;
        Interlocked.Add(ref _input, usage.InputTokenCount ?? 0);
        Interlocked.Add(ref _output, usage.OutputTokenCount ?? 0);
        Volatile.Write(ref _reported, 1);
    }
}

/// <summary>
/// 包在共用 <see cref="IChatClient"/> 外面的中介層：把每次模型呼叫的次數與 token 累計到目前的 <see cref="UsageScope"/>
/// （docs/m4-implementation-plan.md §5.2）。Agent 與 NL2SQL 管線共用同一個用戶端，所以兩者都會被算到。
/// 目前沒有範圍時（例如評估程式）不累計。供應商沒回報用量（串流常見）就不估算，欄位維持 null。
/// 用法：<c>new UsageTrackingChatClient(ChatClientFactory.Create(options))</c>。
/// </summary>
/// <param name="innerClient">實際呼叫模型的用戶端。</param>
public sealed class UsageTrackingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scope = UsageScope.Current;
        scope?.AddCall();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        scope?.AddUsage(response.Usage);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = UsageScope.Current;
        scope?.AddCall();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.Contents)
                if (content is UsageContent usage) scope?.AddUsage(usage.Details);
            yield return update;
        }
    }
}
