using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Eval.Attack;

/// <summary>
/// 記錄每一次送給模型的完整請求文字（系統指示、所有訊息、工具呼叫與工具結果），<b>只給評估程式與測試使用</b>
/// （docs/m4-implementation-plan.md §7.3）。攻擊測試用它檢查「模型請求中是否出現原始個資」（H3），涵蓋每一輪、含自我修正。
/// 用法：包在真實用戶端外面，每題開始前呼叫 <see cref="Clear"/>，題目結束後讀 <see cref="Requests"/>。
/// </summary>
/// <param name="innerClient">實際呼叫模型的用戶端。</param>
public sealed class RecordingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private readonly object _gate = new();
    private readonly List<string> _requests = [];

    /// <summary>自上次 <see cref="Clear"/> 以來每次模型請求的完整文字，依序排列。</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    /// <summary>清掉已記錄的請求。</summary>
    public void Clear()
    {
        lock (_gate) _requests.Clear();
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Record(list, options);
        return base.GetResponseAsync(list, options, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Record(list, options);
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
            yield return update;
    }

    /// <summary>把一次請求攤平成文字存起來。</summary>
    private void Record(List<ChatMessage> messages, ChatOptions? options)
    {
        var text = new StringBuilder();
        if (options?.Instructions is { } instructions) text.AppendLine(instructions);
        foreach (var message in messages)
        {
            text.Append(message.Role.Value).Append(": ");
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent t: text.AppendLine(t.Text); break;
                    case FunctionCallContent c: text.AppendLine($"{c.Name}({JsonSerializer.Serialize(c.Arguments)})"); break;
                    case FunctionResultContent r: text.AppendLine(r.Result?.ToString()); break;
                }
            }
        }
        lock (_gate) _requests.Add(text.ToString());
    }
}
