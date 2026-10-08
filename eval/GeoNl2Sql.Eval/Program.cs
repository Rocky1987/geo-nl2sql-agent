using System.ComponentModel;
using GeoNl2Sql.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// M0 hello-agent：同一段程式碼，供應商由設定決定。
// 用法：dotnet run --project eval/GeoNl2Sql.Eval -- hello [Provider] [ModelId]
//   args[0] "hello"    目前不會被判讀，任何值都會執行 hello-agent
//   args[1] Provider   Ollama | Anthropic（可省略，預設取 appsettings.json）
//   args[2] ModelId    覆寫模型識別字串（可省略）
//   範例：-- hello                              使用 appsettings.json（預設 Ollama）
//         -- hello Anthropic claude-haiku-4-5   雲端軌，需先用 user-secrets 設定 Model:ApiKey
// 設定來源優先序（後者覆蓋前者）：appsettings.json → user-secrets → 環境變數 → 命令列參數。
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = config.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
if (args.Length >= 2 && Enum.TryParse<ModelProvider>(args[1], true, out var provider)) options.Provider = provider;
if (args.Length >= 3) options.ModelId = args[2];

// 示範用工具：模型可呼叫它取得天氣（固定回傳假資料）。
// 參數 city：城市名稱；回傳：天氣描述字串。
// 註冊時必須以 name: 指定工具名稱，否則頂層陳述式的區域函式會被編譯器改名（_Main_g_GetWeather_0_0）。
[Description("Get the current weather for a city.")]
static string GetWeather([Description("City name")] string city) => $"The weather in {city} is sunny, 26°C.";

using var chatClient = ChatClientFactory.Create(options);
AIAgent agent = new ChatClientAgent(
    chatClient,
    instructions: "You are a helpful assistant. Use tools when they help.",
    name: "HelloAgent",
    tools: [AIFunctionFactory.Create(GetWeather, name: "GetWeather")]);

Console.WriteLine($"Provider={options.Provider} Model={options.ModelId}");
var response = await agent.RunAsync("What is the weather in Taipei?");
Console.WriteLine(response.Text);
