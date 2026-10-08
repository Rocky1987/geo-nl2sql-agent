using System.ComponentModel;
using GeoNl2Sql.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace GeoNl2Sql.Eval.Hello;

/// <summary>
/// M0 hello-agent：同一段程式碼，供應商由設定決定（本機 Ollama 或雲端 Anthropic）。
/// 用來確認模型能呼叫工具；由 Program 的 <c>hello</c> 子命令呼叫。
/// </summary>
public static class HelloCommand
{
    /// <summary>
    /// 建立 hello-agent，詢問台北天氣並印出回答。
    /// </summary>
    /// <param name="config">已載入的設定；讀取其中的 <c>Model</c> 區段（見 <see cref="ModelOptions"/>）。</param>
    /// <param name="provider">命令列指定的供應商（Ollama 或 Anthropic）；null 表示沿用設定值。</param>
    /// <param name="modelId">命令列指定的模型識別字串；null 表示沿用設定值。</param>
    /// <exception cref="InvalidOperationException">選用 Anthropic 但未設定 <c>Model:ApiKey</c>。</exception>
    public static async Task RunAsync(IConfiguration config, ModelProvider? provider, string? modelId)
    {
        var options = config.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
        if (provider is { } p) options.Provider = p;
        if (modelId is not null) options.ModelId = modelId;

        using var chatClient = ChatClientFactory.Create(options);
        AIAgent agent = new ChatClientAgent(
            chatClient,
            instructions: "You are a helpful assistant. Use tools when they help.",
            name: "HelloAgent",
            tools: [AIFunctionFactory.Create(GetWeather, name: "GetWeather")]);

        Console.WriteLine($"Provider={options.Provider} Model={options.ModelId}");
        var response = await agent.RunAsync("What is the weather in Taipei?");
        Console.WriteLine(response.Text);
    }

    /// <summary>
    /// 示範用工具：模型可呼叫它取得天氣（固定回傳假資料）。
    /// </summary>
    /// <param name="city">城市名稱。</param>
    /// <returns>天氣描述字串。</returns>
    [Description("Get the current weather for a city.")]
    private static string GetWeather([Description("City name")] string city) =>
        $"The weather in {city} is sunny, 26°C.";
}
