using GeoNl2Sql.Core;
using GeoNl2Sql.Eval.Hello;
using GeoNl2Sql.Eval.Seed;
using Microsoft.Extensions.Configuration;

// Eval 主程式：依 args[0] 分派到子命令。
// 用法：dotnet run --project eval/GeoNl2Sql.Eval -- <命令> [參數...]
//   hello [Provider] [ModelId]   M0 hello-agent。Provider：Ollama | Anthropic；ModelId：覆寫模型識別字串
//                                範例：-- hello                              使用 appsettings.json（預設 Ollama）
//                                      -- hello Anthropic claude-haiku-4-5   雲端軌，需先用 user-secrets 設定 Model:ApiKey
//   seed                         刪除並重建 GeoNl2SqlDemo，灌入合成資料（只動這個資料庫）
//   spike                        NL2SQL 準確率量測（S4 才實作）
// 設定來源優先序（後者覆蓋前者）：appsettings.json → user-secrets → 環境變數 → 命令列參數（僅 hello 的前兩個參數）。
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

switch (args.FirstOrDefault()?.ToLowerInvariant())
{
    case "hello":
        ModelProvider? provider = args.Length >= 2 && Enum.TryParse<ModelProvider>(args[1], true, out var p) ? p : null;
        await HelloCommand.RunAsync(config, provider, args.Length >= 3 ? args[2] : null);
        break;
    case "seed":
        await SeedCommand.RunAsync(config);
        break;
    case "spike":
        Console.WriteLine("spike 尚未實作（M1 的 S4 才會加入）。");
        return 1;
    default:
        Console.WriteLine("用法：dotnet run --project eval/GeoNl2Sql.Eval -- <hello|seed|spike> [參數...]");
        return 1;
}
return 0;
