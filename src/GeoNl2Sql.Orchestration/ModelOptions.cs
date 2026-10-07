namespace GeoNl2Sql.Orchestration;

public enum ModelProvider
{
    Ollama,
    Anthropic,
}

/// <summary>Bound from the "Model" configuration section. Secrets (ApiKey) come from user-secrets or env vars only.</summary>
public sealed class ModelOptions
{
    public const string SectionName = "Model";

    public ModelProvider Provider { get; set; } = ModelProvider.Ollama;

    public string ModelId { get; set; } = "qwen2.5-coder:3b";

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    public string? ApiKey { get; set; }
}
