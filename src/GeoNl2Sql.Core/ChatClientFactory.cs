using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace GeoNl2Sql.Core;

/// <summary>
/// 將設定轉成 <see cref="IChatClient"/> 的唯一入口，呼叫端不需知道底層供應商。
/// 用法：<c>using var client = ChatClientFactory.Create(options);</c>
/// </summary>
public static class ChatClientFactory
{
    /// <summary>依 <see cref="ModelOptions.Provider"/> 建立對應的聊天用戶端。</summary>
    /// <param name="options">模型設定；Ollama 需 <see cref="ModelOptions.OllamaEndpoint"/> 與 <see cref="ModelOptions.ModelId"/>，Anthropic 另需 <see cref="ModelOptions.ApiKey"/>。</param>
    /// <returns>實作 <see cref="IChatClient"/> 的用戶端；呼叫端負責 Dispose。</returns>
    /// <exception cref="InvalidOperationException">選 Anthropic 但 ApiKey 為空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">Provider 不是已知的列舉值。</exception>
    /// <exception cref="UriFormatException">OllamaEndpoint 不是有效的 URI。</exception>
    public static IChatClient Create(ModelOptions options) => options.Provider switch
    {
        ModelProvider.Ollama => new OllamaApiClient(new Uri(options.OllamaEndpoint), options.ModelId),
        ModelProvider.Anthropic => CreateAnthropic(options),
        _ => throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown model provider."),
    };

    /// <summary>建立 Anthropic 用戶端，並以 <c>AsIChatClient</c> 轉成共用的 <see cref="IChatClient"/>。</summary>
    /// <param name="options">需包含非空的 <see cref="ModelOptions.ApiKey"/> 與 <see cref="ModelOptions.ModelId"/>。</param>
    /// <exception cref="InvalidOperationException">ApiKey 為空或空白。</exception>
    private static IChatClient CreateAnthropic(ModelOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "Model:ApiKey is not set. Use `dotnet user-secrets set Model:ApiKey <key>` or the Model__ApiKey environment variable.");
        }

        return new AnthropicClient { ApiKey = options.ApiKey }.AsIChatClient(options.ModelId);
    }
}
