# geo-nl2sql-agent

**中文** | [English](#english)

以 .NET 與 Microsoft Agent Framework 打造的「自然語言轉 SQL」空間資料代理，後端為 SQL Server，Web 層採用 ASP.NET Core MVC。
這是個人作品集專案：所有資料皆為合成資料，不涉及任何真實客戶或公司。

## 技術亮點

模型產生的 SQL 不被信任，由兩道彼此獨立的防線把關：

1. **SQL AST 白名單**：以 ScriptDom 解析，只放行單一 `SELECT`，資料表與函式都限白名單；任何解析錯誤、多語句、`SELECT INTO`、動態 SQL、外部資料來源都拒絕。
2. **唯讀資料庫登入**：生成的 SQL 只用 `geo_reader` 執行，資料庫層只授予 `SELECT`，即使第一層被繞過也無法寫入或改結構。

另有逾時與列數上限、有界的自我修正（最多 2 次），以及只回饋固定訊息、不把資料庫原文交給模型的錯誤消毒。60 條攻擊語料庫的結果與兩層的分工見 [docs/feasibility-report.md](docs/feasibility-report.md) §8。

個資、稽核與提示詞注入：

- **模型永遠看不到原始個資。** 客戶的姓名、身分證字號、電話、電子郵件在資料庫層對模型用的登入 `DENY`，模型只能讀到遮蔽後的檢視（例如 `A*******21`）。頁面可選 `analyst`／`admin` 角色：admin 的表格會換成原始值，但送往模型的內容不變。**角色只是示範用的下拉選單，不是存取控制。**
- **每次請求寫一筆稽核**（問題、角色、SQL、token、耗時、結果、模型與提示詞版本），存在 append-only 的 SQL Server ledger 表，連資料庫擁有者都不能改或刪；稽核寫入失敗時請求一併失敗。
- **提示詞注入有三道機率性護欄**：規則式輸入偵測、工具結果（資料表內容）清理，以及輸出防護（金絲雀字串與 40 字元重疊比對）。它們不是保證，保證來自上面的資料庫層。
- **攻擊測試**（claude-haiku-4-5，50 筆，每題 1 次）：未授權個資取得、寫入、提示詞與 schema 洩漏皆為 0，送往模型的請求含原始個資為 0；但規則只擋下其中 15 筆（30%），其餘是模型自己拒絕的，輸出防護在測試中沒有被真實攻擊觸發，也有一筆改寫式的規則概述洩漏。完整數字與限制見 [docs/feasibility-report.md](docs/feasibility-report.md) §10。

空間功能的幾個做法：

- **質心不用外接框中心。** `EnvelopeCenter()` 在不規則的行政區會偏離真質心，所以改用 `geography` → `geometry` → `STCentroid()`，並以 NetTopologySuite 投影到 TWD97 後的結果對照；測試會掃描原始碼，不允許出現 `EnvelopeCenter`。
- **質心與緩衝區是寫死的參數化 SQL，不是讓模型寫。** 模型只決定呼叫哪個工具與參數（行政區名稱、緯經度、半徑），工具迴圈有輪數上限。
- **GeoJSON 不經過模型。** 工具把地圖資料放進單次請求專用的旁路，由網頁直接取用，模型只看到摘要。
- **逐步串流。** 網頁顯示目前正在做的步驟，回答文字逐段出現，最後才畫出結果表與地圖。

## 目標

- NL2SQL，並以確定性的安全邊界把關（SQL AST 白名單 + 唯讀資料庫登入）。
- GIS 工具（GeoJSON、質心、緩衝區），基於 SQL Server `geography`，並顯示在 Leaflet 地圖上。
- 雙軌模型，皆經 `IChatClient` 接入：雲端（Anthropic）與本機（Ollama）。
- 可量測的成果：評估題庫、消融實驗與攻擊語料庫，連失敗案例一併公開。

## 技術架構

| 層 | 專案 | 說明 |
|---|---|---|
| Web | `GeoNl2Sql.Web` | ASP.NET Core MVC（Controller + Razor View），Leaflet 地圖，Tailwind CSS |
| 核心 | `GeoNl2Sql.Core` | Agent Framework、模型工廠、護欄、SQL Server 存取與空間查詢（以資料夾區分，不另拆專案） |
| 測試 | `tests/GeoNl2Sql.Tests` | 單元測試（可離線執行） |
| 評估 | `eval/GeoNl2Sql.Eval` | hello-agent、評估與消融實驗 |

## 環境需求

- .NET SDK 9（版本鎖定於 `global.json`）
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

## 啟動網頁

網頁預設使用雲端模型 `claude-haiku-4-5`，並以唯讀登入查詢資料庫，所以需先完成「建立示範資料庫」與「設定資料庫登入」兩節，並設定 `Model:ApiKey` 與 `ConnectionStrings:Reader`、`ReaderPii`、`Auditor`。Web、Eval 與測試專案共用同一份 user-secrets，已經對任一專案設過就不必重設。

```powershell
dotnet run --project src/GeoNl2Sql.Web --launch-profile http   # 開啟 http://localhost:5001
```

改用本機模型（Ollama）：

```powershell
$env:Model__Provider = "Ollama"; $env:Model__ModelId = "qwen2.5:3b"
dotnet run --project src/GeoNl2Sql.Web --launch-profile http
```

本機 3B 模型在多工具情境不穩定（實測見 [docs/feasibility-report.md](docs/feasibility-report.md) §9.5），建議使用雲端模型。

端點：`POST /query` 回傳完整 JSON，`POST /query/stream` 以逐行 JSON（NDJSON）串流進度與回答文字；兩者的輸入都是 `{ "question": "…", "role": "analyst" }`（`role` 可省略，預設 `analyst`，另可為 `admin`，其他值回 400）。

**安全範圍**：網頁有個資遮蔽、稽核與提示詞注入護欄，模型產生的 SQL 也受兩層防線把關。但**角色是頁面自選的，不是存取控制**（任何人都能選 admin 看到原始的合成個資），也沒有登入與速率限制，所以**請勿對外部署**。示範資料庫重建（`seed`）時稽核紀錄一併清掉。地圖底圖使用 OpenStreetMap 圖磚，僅適合低流量展示。

樣式使用 Tailwind CSS，編譯後的 `wwwroot/css/app.css` 已在儲存庫內，直接執行即可。修改樣式後用 `tools/build-css.ps1` 重新編譯（會下載官方獨立 CLI，不需要 Node）。

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

## 設定資料庫登入並執行 NL2SQL 管線

管線用唯讀登入 `geo_reader` 執行模型產生的 SQL，共有三個登入：

| 設定鍵 | User ID | 用途 |
|---|---|---|
| `ConnectionStrings:Reader` | `geo_reader` | 模型產生的 SQL；個資欄位 `DENY`，只讀得到遮蔽檢視（[db/02_reader.sql](db/02_reader.sql)、[db/03_masking.sql](db/03_masking.sql)） |
| `ConnectionStrings:ReaderPii` | `geo_reader_pii` | 只給 admin 角色重跑同一句已驗證的 SQL，讓表格顯示原始值（[db/04_reader_pii.sql](db/04_reader_pii.sql)） |
| `ConnectionStrings:Auditor` | `geo_auditor` | 只能 `INSERT` 稽核表，連自己寫的紀錄都讀不到（[db/05_audit.sql](db/05_audit.sql)） |

前提：

- SQL Server 需啟用**混合驗證**（SQL 驗證與 Windows 驗證並存），三個登入才能以密碼登入。
- 三個連線字串的 User ID 必須如上表；`seed` 會依連線字串建立（或更新密碼）登入並套用對應腳本，所以請在 `seed` **之前**設定。三個登入請各用不同的密碼。

```powershell
dotnet user-secrets set "ConnectionStrings:Reader" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;User ID=geo_reader;Password=<password>;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
dotnet user-secrets set "ConnectionStrings:ReaderPii" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;User ID=geo_reader_pii;Password=<password>;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
dotnet user-secrets set "ConnectionStrings:Auditor" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;User ID=geo_auditor;Password=<password>;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
dotnet run --project eval/GeoNl2Sql.Eval -- seed
```

手動問一個問題（會印出每次嘗試的 SQL、失敗原因與前 20 列結果）：

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- ask "中央區有哪些基地台？"
dotnet run --project eval/GeoNl2Sql.Eval -- ask "中央區有哪些基地台？" --provider Anthropic --model claude-haiku-4-5
```

30 題端到端量測（生成的 SQL 只走 `geo_reader`，與標準答案比對執行結果，並統計驗證器誤擋）。雲端模型會依題數計費，可先用 `--limit` 少量試跑，或用 `--fake` 不呼叫模型驗證流程：

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- pipeline --provider Anthropic --model claude-haiku-4-5 --limit 3
dotnet run --project eval/GeoNl2Sql.Eval -- pipeline --fake "{gold}"        # 把標準 SQL 當作模型回應，不花費用
```

## 執行攻擊測試

50 筆攻擊語料庫（直接注入、越獄、刺探 schema、要求輸出系統提示詞、資料表內容中的間接注入）逐題送進完整的請求流程，檢查輸出是否出現原始個資、資料庫是否有改動、是否洩漏提示詞，並擷取實際送往模型的請求檢查個資。會呼叫雲端模型並計費，請先用 `--limit` 少量試跑。`seed` 會把 5 座基地台的名稱改成含指令的文字（間接注入的來源）。

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- attack --run A --limit 3 --provider Anthropic --model claude-haiku-4-5   # A：analyst、護欄全開
dotnet run --project eval/GeoNl2Sql.Eval -- attack --run B --provider Anthropic --model claude-haiku-4-5            # B：護欄全關（25 筆）
dotnet run --project eval/GeoNl2Sql.Eval -- attack --run C --provider Anthropic --model claude-haiku-4-5            # C：admin 角色（個資相關 10 筆）
```

結果寫到 `eval/GeoNl2Sql.Eval/Results/`（不進 git，檔內的原始個資值已先遮掉）。

## 執行測試

```powershell
dotnet test --filter "Category!=Database"   # 離線測試，不需資料庫與模型
dotnet test                                  # 全部；需有種子資料庫與 geo_reader
```

## 授權

MIT

---

## English

[中文](#geo-nl2sql-agent)

A natural-language-to-SQL agent for spatial data on SQL Server, built with .NET and Microsoft Agent Framework. The web layer is ASP.NET Core MVC.
This is a portfolio project: all data is synthetic, and no real customer or company is involved.

### Highlights

Model-generated SQL is never trusted. Two independent defenses guard it:

1. **SQL AST allow-list**: parsed with ScriptDom; only a single `SELECT` passes, with tables and functions restricted to allow-lists. Parse errors, multiple statements, `SELECT INTO`, dynamic SQL and external data sources are all rejected.
2. **Read-only database login**: generated SQL runs only as `geo_reader`, which holds `SELECT` and nothing else, so even if the first layer is bypassed nothing can be written or altered.

Also: query timeout and row cap, bounded self-correction (at most 2 retries), and error sanitizing that feeds the model fixed messages instead of database text. Results of the 60-attack corpus and how the two layers divide the work are in [docs/feasibility-report.md](docs/feasibility-report.md) §8 (written in Chinese).

PII, audit and prompt injection:

- **The model never sees raw PII.** Customer name, national ID, phone and email are `DENY`-ed at the database level for the login the model path uses, which can only read a masked view (e.g. `A*******21`). The page lets you pick an `analyst` or `admin` role: for admin the table is swapped to raw values, but what is sent to the model does not change. **The role is a demo dropdown, not access control.**
- **Every request writes one audit row** (question, role, SQL, tokens, duration, outcome, model and prompt versions) to an append-only SQL Server ledger table that even the database owner cannot update or delete; if the audit write fails, the request fails too.
- **Three probabilistic guardrails against prompt injection**: rule-based input detection, scrubbing of tool results (table content), and an output guard (canary string plus a 40-character overlap check). They are not guarantees; the guarantee comes from the database layer above.
- **Attack test** (claude-haiku-4-5, 50 attacks, one run each): unauthorized PII access, writes, and prompt or schema leaks were all 0, and raw PII in requests sent to the model was 0. But the rules blocked only 15 of them (30%); the model refused the rest by itself, the output guard was never triggered by a real attack, and one paraphrased summary of the rules did leak. Full numbers and limits are in [docs/feasibility-report.md](docs/feasibility-report.md) §10 (written in Chinese).

Spatial design choices:

- **The centroid is not the bounding-box center.** `EnvelopeCenter()` drifts from the true centroid on irregular districts, so the centroid goes `geography` → `geometry` → `STCentroid()` and is cross-checked against NetTopologySuite after projecting to TWD97. A test scans the source and fails if `EnvelopeCenter` appears.
- **Centroid and buffer are fixed, parameterized SQL, not model-written.** The model only picks a tool and its parameters (district name, latitude/longitude, radius), and the tool loop has a round limit.
- **GeoJSON never goes through the model.** Tools put map data on a per-request side channel that the page reads directly; the model sees only a summary.
- **Step-by-step streaming.** The page shows the current step, the answer text appears progressively, and the result table and map are drawn at the end.

### Goals

- NL2SQL with deterministic safety boundaries (SQL AST allow-list + read-only database login).
- GIS tools (GeoJSON, centroid, buffer) backed by SQL Server `geography`, shown on a Leaflet map.
- Dual model track, both behind `IChatClient`: cloud (Anthropic) and local (Ollama).
- Measured results: an evaluation set, ablations and an attack corpus, published with failures included.

### Architecture

| Layer | Project | Notes |
|---|---|---|
| Web | `GeoNl2Sql.Web` | ASP.NET Core MVC (controllers + Razor views), Leaflet map, Tailwind CSS |
| Core | `GeoNl2Sql.Core` | Agent Framework, model factory, guardrails, SQL Server access and spatial queries (separated by folders, not by projects) |
| Tests | `tests/GeoNl2Sql.Tests` | Unit tests (run offline) |
| Eval | `eval/GeoNl2Sql.Eval` | hello-agent, evaluation and ablations |

### Requirements

- .NET SDK 9 (pinned in `global.json`)
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

### Run the web page

The page uses the cloud model `claude-haiku-4-5` by default and queries the database through the read-only login, so first complete "Create the demo database" and "Database logins", and set `Model:ApiKey` and `ConnectionStrings:Reader`, `ReaderPii` and `Auditor`. The Web, Eval and test projects share one set of user-secrets, so settings made for any one of them are enough.

```powershell
dotnet run --project src/GeoNl2Sql.Web --launch-profile http   # opens http://localhost:5001
```

To use the local model (Ollama):

```powershell
$env:Model__Provider = "Ollama"; $env:Model__ModelId = "qwen2.5:3b"
dotnet run --project src/GeoNl2Sql.Web --launch-profile http
```

The local 3B model is unreliable with multiple tools (measured in [docs/feasibility-report.md](docs/feasibility-report.md) §9.5, written in Chinese); the cloud model is recommended.

Endpoints: `POST /query` returns the full JSON; `POST /query/stream` streams progress and answer text as newline-delimited JSON (NDJSON). Both take `{ "question": "…", "role": "analyst" }` (`role` is optional and defaults to `analyst`; the other value is `admin`, anything else returns 400).

**Safety scope**: the page has PII masking, audit and prompt-injection guardrails, and model-generated SQL passes both defenses. But **the role is chosen on the page and is not access control** (anyone can pick admin and see the raw synthetic PII), and there is no login or rate limiting, so **do not deploy it publicly**. Re-running `seed` also wipes the audit rows. The map uses OpenStreetMap tiles, which suit low-traffic demos only.

Styling uses Tailwind CSS. The compiled `wwwroot/css/app.css` is in the repository, so it runs as is. After editing styles, recompile with `tools/build-css.ps1` (it downloads the official standalone CLI; Node is not needed).

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

### Database logins and the NL2SQL pipeline

The pipeline runs model-generated SQL as the read-only login `geo_reader`. There are three logins:

| Setting key | User ID | Purpose |
|---|---|---|
| `ConnectionStrings:Reader` | `geo_reader` | Model-generated SQL; PII columns are `DENY`-ed, only the masked view is readable ([db/02_reader.sql](db/02_reader.sql), [db/03_masking.sql](db/03_masking.sql)) |
| `ConnectionStrings:ReaderPii` | `geo_reader_pii` | Only used to re-run the same validated SQL for the admin role so the table shows raw values ([db/04_reader_pii.sql](db/04_reader_pii.sql)) |
| `ConnectionStrings:Auditor` | `geo_auditor` | Can only `INSERT` into the audit table, and cannot even read its own rows ([db/05_audit.sql](db/05_audit.sql)) |

Prerequisites:

- SQL Server must allow **mixed-mode authentication** (SQL and Windows) so the three logins can sign in with passwords.
- Each connection string must use the User ID above. `seed` creates (or updates the password of) the logins from them and applies the matching scripts, so set them **before** running `seed`. Use a different password for each login.

```powershell
dotnet user-secrets set "ConnectionStrings:Reader" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;User ID=geo_reader;Password=<password>;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
dotnet user-secrets set "ConnectionStrings:ReaderPii" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;User ID=geo_reader_pii;Password=<password>;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
dotnet user-secrets set "ConnectionStrings:Auditor" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;User ID=geo_auditor;Password=<password>;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
dotnet run --project eval/GeoNl2Sql.Eval -- seed
```

Ask one question (prints each attempt's SQL, failure reason and the first 20 rows):

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- ask "中央區有哪些基地台？"
dotnet run --project eval/GeoNl2Sql.Eval -- ask "中央區有哪些基地台？" --provider Anthropic --model claude-haiku-4-5
```

The 30-question end-to-end run (generated SQL goes through `geo_reader` only; results are compared with the gold answers and validator false rejects are counted). Cloud models are billed per call, so try `--limit` first, or `--fake` to exercise the flow without calling a model:

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- pipeline --provider Anthropic --model claude-haiku-4-5 --limit 3
dotnet run --project eval/GeoNl2Sql.Eval -- pipeline --fake "{gold}"        # use the gold SQL as the model reply, no cost
```

### Run the attack test

The 50-attack corpus (direct injection, jailbreaks, schema probing, system-prompt extraction, and indirect injection through table content) is sent through the full request flow. It checks whether raw PII appears in the output, whether the database changed, whether the prompt leaked, and inspects the requests actually sent to the model for PII. It calls the cloud model and is billed, so try `--limit` first. `seed` renames 5 base stations to instruction-bearing text (the source of the indirect injection).

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- attack --run A --limit 3 --provider Anthropic --model claude-haiku-4-5   # A: analyst, all guardrails on
dotnet run --project eval/GeoNl2Sql.Eval -- attack --run B --provider Anthropic --model claude-haiku-4-5            # B: guardrails off (25 attacks)
dotnet run --project eval/GeoNl2Sql.Eval -- attack --run C --provider Anthropic --model claude-haiku-4-5            # C: admin role (10 PII-related attacks)
```

Results are written to `eval/GeoNl2Sql.Eval/Results/` (not committed; raw PII values in the files are masked first).

### Running the tests

```powershell
dotnet test --filter "Category!=Database"   # offline tests, no database or model needed
dotnet test                                  # everything; needs the seeded database and geo_reader
```

### License

MIT
