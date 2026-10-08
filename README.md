# geo-nl2sql-agent

**中文** | [English](#english)

以 .NET 10 與 Microsoft Agent Framework 打造的「自然語言轉 SQL」空間資料代理，後端為 SQL Server，Web 層採用 ASP.NET Core MVC。
這是個人作品集專案：所有資料皆為合成資料，不涉及任何真實客戶或公司。

> **目前狀態：M0（環境與 hello-agent）**，開發中。計畫與里程碑請見 [docs/feasibility-report.md](docs/feasibility-report.md)。

## 目標

- NL2SQL，並以確定性的安全邊界把關（SQL AST 白名單 + 唯讀資料庫登入）。
- GIS 工具（GeoJSON、質心、緩衝區），基於 SQL Server `geography`。
- 雙軌模型，皆經 `IChatClient` 接入：雲端（Anthropic）與本機（Ollama）。
- 可量測的成果：評估題庫、消融實驗與攻擊語料庫，連失敗案例一併公開。

## 技術架構

| 層 | 專案 | 說明 |
|---|---|---|
| Web | `GeoNl2Sql.Web` | ASP.NET Core MVC（Controller + Razor View），Leaflet 地圖 |
| 核心 | `GeoNl2Sql.Core` | Agent Framework、模型工廠、護欄、SQL Server 存取與空間查詢（以資料夾區分，不另拆專案） |
| 測試 | `tests/GeoNl2Sql.Tests` | 單元測試（可離線執行） |
| 評估 | `eval/GeoNl2Sql.Eval` | hello-agent、評估與消融實驗 |

## 環境需求

- .NET SDK 10（版本鎖定於 `global.json`）
- 本機軌：Ollama 與 `qwen2.5:3b`
- 雲端軌：Anthropic API key

## 設定與金鑰

**切勿提交金鑰。** 設定來源後者覆蓋前者：`appsettings.json` → user-secrets → 環境變數。

- **API 金鑰**：本機開發用 user-secrets；佈署用環境變數 `Model__ApiKey`。任何情況都不要寫進 `appsettings.json`。
- **資料庫連線字串**（M1 起，鍵 `ConnectionStrings:Demo`）：`appsettings.json` 內是不含密碼的 Windows 驗證範本，請改成你的伺服器；本機想另外覆蓋可用 user-secrets；若使用含密碼的 SQL 驗證，請用環境變數 `ConnectionStrings__Demo`。

```powershell
dotnet user-secrets set "Model:ApiKey" "<your key>" --project eval/GeoNl2Sql.Eval
dotnet user-secrets set "ConnectionStrings:Demo" "<connection string>" --project eval/GeoNl2Sql.Eval
```

## 試跑 hello-agent

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- hello                              # 本機 Ollama
dotnet run --project eval/GeoNl2Sql.Eval -- hello Anthropic claude-haiku-4-5   # 雲端
```

## 建立示範資料庫

需先設定 `ConnectionStrings:Demo`（資料庫名稱必須是 `GeoNl2SqlDemo`）。此命令會刪除並重建該資料庫，不會動其他資料庫；固定種子，每次產生的資料都相同。

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- seed
```

## 執行 M1 準確率量測（spike）

丟棄式腳本，用途是量出 NL2SQL 準確率，不是正式功能（見 [docs/m1-implementation-plan.md](docs/m1-implementation-plan.md) §5）。需先 `seed` 建好資料庫。

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- spike                              # 本機模型、有欄位說明 schema、全部 30 題
dotnet run --project eval/GeoNl2Sql.Eval -- spike plain --limit 5              # 無欄位說明 schema，只跑前 5 題
dotnet run --project eval/GeoNl2Sql.Eval -- spike --provider Anthropic --model claude-haiku-4-5 --runs 3
```

結果寫到 `eval/GeoNl2Sql.Eval/Results/`（不進 git），每輪一份 JSON，含每題的生成 SQL、失敗類型與錯誤訊息。

## 授權

MIT

---

## English

[中文](#geo-nl2sql-agent)

A natural-language-to-SQL agent for spatial data on SQL Server, built with .NET 10 and Microsoft Agent Framework. The web layer is ASP.NET Core MVC.
This is a portfolio project: all data is synthetic, and no real customer or company is involved.

> **Status: M0 (environment and hello-agent)**, work in progress. See [docs/feasibility-report.md](docs/feasibility-report.md) for the plan and milestones (written in Chinese).

### Goals

- NL2SQL with deterministic safety boundaries (SQL AST allow-list + read-only database login).
- GIS tools (GeoJSON, centroid, buffer) backed by SQL Server `geography`.
- Dual model track, both behind `IChatClient`: cloud (Anthropic) and local (Ollama).
- Measured results: an evaluation set, ablations and an attack corpus, published with failures included.

### Architecture

| Layer | Project | Notes |
|---|---|---|
| Web | `GeoNl2Sql.Web` | ASP.NET Core MVC (controllers + Razor views), Leaflet map |
| Core | `GeoNl2Sql.Core` | Agent Framework, model factory, guardrails, SQL Server access and spatial queries (separated by folders, not by projects) |
| Tests | `tests/GeoNl2Sql.Tests` | Unit tests (run offline) |
| Eval | `eval/GeoNl2Sql.Eval` | hello-agent, evaluation and ablations |

### Requirements

- .NET SDK 10 (pinned in `global.json`)
- Local track: Ollama with `qwen2.5:3b`
- Cloud track: an Anthropic API key

### Configuration and secrets

**Never commit keys.** Sources are read in this order, later ones overriding earlier ones: `appsettings.json` → user-secrets → environment variables.

- **API key**: user-secrets for local development; the `Model__ApiKey` environment variable when deployed. Never put it in `appsettings.json`.
- **Database connection string** (from M1, key `ConnectionStrings:Demo`): `appsettings.json` holds a password-free Windows-auth template; edit it for your server. Override locally with user-secrets if you like. For SQL authentication (with a password) use the `ConnectionStrings__Demo` environment variable.

```powershell
dotnet user-secrets set "Model:ApiKey" "<your key>" --project eval/GeoNl2Sql.Eval
dotnet user-secrets set "ConnectionStrings:Demo" "<connection string>" --project eval/GeoNl2Sql.Eval
```

### Try the hello-agent

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- hello                              # local Ollama
dotnet run --project eval/GeoNl2Sql.Eval -- hello Anthropic claude-haiku-4-5   # cloud
```

### Create the demo database

Set `ConnectionStrings:Demo` first (the database name must be `GeoNl2SqlDemo`). This command drops and recreates that database only and never touches any other; the seed is fixed, so the generated data is identical every run.

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- seed
```

### Run the M1 accuracy spike

A throwaway script whose only job is to measure NL2SQL accuracy, not a product feature (see [docs/m1-implementation-plan.md](docs/m1-implementation-plan.md) §5, written in Chinese). Run `seed` first.

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- spike                              # local model, schema with field descriptions, all 30 questions
dotnet run --project eval/GeoNl2Sql.Eval -- spike plain --limit 5              # schema without field descriptions, first 5 questions only
dotnet run --project eval/GeoNl2Sql.Eval -- spike --provider Anthropic --model claude-haiku-4-5 --runs 3
```

Results are written to `eval/GeoNl2Sql.Eval/Results/` (not committed), one JSON file per run, with each question's generated SQL, failure type and error message.

### License

MIT
