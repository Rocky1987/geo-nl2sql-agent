using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace GeoNl2Sql.Orchestration;

/// <summary>Single place that turns configuration into an <see cref="IChatClient"/>, so callers never see the provider.</summary>
public static class ChatClientFactory
{
    public static IChatClient Create(ModelOptions options) => options.Provider switch
    {
        ModelProvider.Ollama => new OllamaApiClient(new Uri(options.OllamaEndpoint), options.ModelId),
        ModelProvider.Anthropic => CreateAnthropic(options),
        _ => throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown model provider."),
    };

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
