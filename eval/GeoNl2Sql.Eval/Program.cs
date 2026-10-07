using System.ComponentModel;
using GeoNl2Sql.Orchestration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// M0 hello-agent: same code, provider chosen by configuration.
//   dotnet run --project eval/GeoNl2Sql.Eval -- hello            (uses appsettings.json: Ollama by default)
//   dotnet run --project eval/GeoNl2Sql.Eval -- hello Anthropic claude-haiku-4-5
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = config.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
if (args.Length >= 2 && Enum.TryParse<ModelProvider>(args[1], true, out var provider)) options.Provider = provider;
if (args.Length >= 3) options.ModelId = args[2];

[Description("Get the current weather for a city.")]
static string GetWeather([Description("City name")] string city) => $"The weather in {city} is sunny, 26°C.";

using var chatClient = ChatClientFactory.Create(options);
AIAgent agent = new ChatClientAgent(
    chatClient,
    instructions: "You are a helpful assistant. Use tools when they help.",
    name: "HelloAgent",
    tools: [AIFunctionFactory.Create(GetWeather)]);

Console.WriteLine($"Provider={options.Provider} Model={options.ModelId}");
var response = await agent.RunAsync("What is the weather in Taipei?");
Console.WriteLine(response.Text);
