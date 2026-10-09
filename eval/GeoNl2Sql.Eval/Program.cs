using GeoNl2Sql.Core;
using GeoNl2Sql.Eval.Hello;
using GeoNl2Sql.Eval.Pipeline;
using GeoNl2Sql.Eval.Seed;
using GeoNl2Sql.Eval.Spike;
using Microsoft.Extensions.Configuration;

// Eval 主程式：依 args[0] 分派到子命令。
// 用法：dotnet run --project eval/GeoNl2Sql.Eval -- <命令> [參數...]
//   hello [Provider] [ModelId]   M0 hello-agent。Provider：Ollama | Anthropic；ModelId：覆寫模型識別字串
//                                範例：-- hello                              使用 appsettings.json（預設 Ollama）
//                                      -- hello Anthropic claude-haiku-4-5   雲端軌，需先用 user-secrets 設定 Model:ApiKey
//   seed                         刪除並重建 GeoNl2SqlDemo，灌入合成資料（只動這個資料庫）
//   spike [plain|described] [--limit N] [--runs N] [--provider P] [--model M] [--fake 文字]
//                                NL2SQL 準確率量測，結果寫到 eval/GeoNl2Sql.Eval/Results/（參數說明見 SpikeCommand）
//                                範例：-- spike plain --limit 3                    本機模型、無描述 schema、前 3 題
//                                      -- spike --fake "{gold}"                   不呼叫模型，以標準 SQL 驗證比對器
//   pipeline [--limit N] [--runs N] [--provider P] [--model M] [--fake 文字]
//                                M2 端到端量測（驗證器 + 唯讀執行 + 最多 2 次修正），需要 ConnectionStrings:Reader
//                                範例：-- pipeline --limit 3                     前 3 題（先確認 token 用量）
//                                      -- pipeline --fake "{gold}"                不呼叫模型，驗證流程與驗證器誤擋
//   ask "問題" [--provider P] [--model M]
//                                手動問一題，印出 SQL、各次嘗試與前 20 列
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
        await SpikeCommand.RunAsync(config, args[1..]);
        break;
    case "pipeline":
        await PipelineCommand.RunAsync(config, args[1..]);
        break;
    case "ask":
        await AskCommand.RunAsync(config, args[1..]);
        break;
    default:
        Console.WriteLine("用法：dotnet run --project eval/GeoNl2Sql.Eval -- <hello|seed|spike|pipeline|ask> [參數...]");
        return 1;
}
return 0;
