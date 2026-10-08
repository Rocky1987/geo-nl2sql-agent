namespace GeoNl2Sql.Core;

/// <summary>模型供應商。決定 <see cref="ChatClientFactory"/> 建立哪一種 <c>IChatClient</c>。</summary>
public enum ModelProvider
{
    /// <summary>本機 Ollama 服務（需先啟動 Ollama 並拉取模型）。</summary>
    Ollama,

    /// <summary>Anthropic 雲端 API（需要 <see cref="ModelOptions.ApiKey"/>）。</summary>
    Anthropic,
}

/// <summary>
/// 模型連線設定，對應設定檔的 "Model" 區段。
/// 用法：<c>config.GetSection(ModelOptions.SectionName).Get&lt;ModelOptions&gt;() ?? new ModelOptions()</c>。
/// 金鑰（<see cref="ApiKey"/>）只能來自 dotnet user-secrets 或環境變數 <c>Model__ApiKey</c>，不可寫進 appsettings.json。
/// </summary>
public sealed class ModelOptions
{
    /// <summary>設定檔中的區段名稱（"Model"），供 <c>GetSection</c> 使用。</summary>
    public const string SectionName = "Model";

    /// <summary>使用的供應商，預設 <see cref="ModelProvider.Ollama"/>。</summary>
    public ModelProvider Provider { get; set; } = ModelProvider.Ollama;

    /// <summary>模型識別字串，例如 Ollama 的 "qwen2.5:3b" 或 Anthropic 的 "claude-haiku-4-5"。預設 "qwen2.5:3b"。</summary>
    public string ModelId { get; set; } = "qwen2.5:3b";

    /// <summary>Ollama 服務位址，僅 <see cref="ModelProvider.Ollama"/> 使用，預設 http://localhost:11434。</summary>
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>Anthropic API 金鑰，僅 <see cref="ModelProvider.Anthropic"/> 使用；未設定時為 null。</summary>
    public string? ApiKey { get; set; }
}
