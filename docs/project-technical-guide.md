# 專案技術文件（四個專案）

| 項目 | 內容 |
|---|---|
| 讀者 | 想深入了解本方案技術內容的開發者 |
| 文件日期 | 2026-10-09（涵蓋模型工廠、護欄、NL2SQL 管線、空間查詢、Agent 工具迴圈與 Web 前端；PII、稽核尚未實作） |
| 範圍 | 方案內四個專案：`GeoNl2Sql.Core`、`GeoNl2Sql.Web`、`GeoNl2Sql.Tests`、`GeoNl2Sql.Eval` |
| 閱讀提醒 | 每個專案都分成「**現況**（已存在於程式碼）」與「**規劃**（尚未實作，來自 `feasibility-report.md`）」。尚未實作的部分不當成既成事實描述。 |

---

## 1. 整體結構

```
GeoNl2Sql.slnx
├── src/
│   ├── GeoNl2Sql.Core/      類別庫：模型工廠、護欄、唯讀執行、NL2SQL 管線、空間查詢、Agent 工具迴圈
│   └── GeoNl2Sql.Web/       ASP.NET Core MVC：Controller + Razor View + Leaflet + Tailwind CSS
├── tests/
│   └── GeoNl2Sql.Tests/     xUnit 單元測試（離線可跑）
└── eval/
    └── GeoNl2Sql.Eval/      主控台：hello-agent、評估、消融實驗
```

### 1.1 依賴方向

```
Web  ──▶ Core ◀── Tests ──▶ Web
              ▲
              └── Eval
```

- Web、Tests、Eval 都引用 `Core`；`Core` 不引用任何專案。Web 與 Eval 彼此不相依；Tests 另引用 Web，以測試控制器。
- 為何只有四個專案：2026-10-08 由六個合併而來（原 Data、Orchestration、Core 合為一個 Core）。依據 `CLAUDE.md` 的「Simplicity First」，單一個人專案沒有第二個消費者能共用資料層，拆專案只增加維護成本。邊界改以 **Core 內的資料夾**區分（見 §2.4），若日後真的需要獨立發佈再拆。

### 1.2 共通技術基線

| 項目 | 目前值 | 說明 |
|---|---|---|
| 目標框架 | `net9.0`（四個專案一致） | 目前以 .NET 9 為基準；升級到 .NET 10 列在 §7 |
| SDK 鎖定 | [global.json](../global.json)：`9.0.306`，`rollForward: latestFeature` | 升級框架時一併調整 |
| 語言設定 | `Nullable` 與 `ImplicitUsings` 皆啟用 | |
| 建置與測試 | `dotnet build GeoNl2Sql.slnx`、`dotnet test` | 0 警告 0 錯誤；全部 279 項測試通過，其中 60 項需要資料庫（標記 `Database`），其餘 219 項離線即可跑：`dotnet test --filter "Category!=Database"` |
| 版本管理 | 預設分支 `main`；commit 前可由本機 pre-commit hook 執行 gitleaks | hook 在 `.git/hooks`，不隨儲存庫散佈，clone 後需自行設定 |

---

## 2. GeoNl2Sql.Core

[src/GeoNl2Sql.Core/](../src/GeoNl2Sql.Core/)

### 2.1 角色

整個方案唯一的「共用邏輯」專案，所有 Agent 相關型別集中在此（風險 R6 的對策：Agent Framework 的型別不外洩到 Web 與 Eval，日後若 API 改版，改動範圍限於此處）。

### 2.2 使用套件

| 套件 | 版本 | 用途 |
|---|---|---|
| `Microsoft.Agents.AI` | 1.23.0 | Microsoft Agent Framework（AF）：`AIAgent`、`ChatClientAgent`、中介層 |
| `Microsoft.Extensions.AI` | 10.10.0 | 模型抽象 `IChatClient`、工具抽象 `AIFunction`／`AIFunctionFactory` |
| `OllamaSharp` | 5.5.0 | 本機軌：`OllamaApiClient` 直接實作 `IChatClient` |
| `Anthropic` | 12.53.0 | 雲端軌：官方 SDK，經擴充方法 `AsIChatClient` 轉成 `IChatClient` |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.117.0 | T-SQL 解析成 AST，供 `SqlValidator` 做白名單驗證 |
| `Microsoft.Data.SqlClient` | 7.1.1 | 唯讀執行器的資料庫存取 |
| `NetTopologySuite` | 2.6.0 | 幾何型別與 WKT 解析 |
| `NetTopologySuite.IO.GeoJSON` | 4.0.0 | 組出 GeoJSON（以 Newtonsoft.Json 實作） |
| `NetTopologySuite.IO.SqlServerBytes` | 2.1.0 | 解析 `geography` 位元組（模型生成的 SQL 回傳的是位元組） |

### 2.3 現況：模型工廠（根目錄兩個檔案）

**[ModelOptions.cs](../src/GeoNl2Sql.Core/ModelOptions.cs)**：綁定設定檔 `Model` 區段的選項類別。

| 屬性 | 預設 | 說明 |
|---|---|---|
| `Provider` | `Ollama` | 列舉 `ModelProvider { Ollama, Anthropic }` |
| `ModelId` | `qwen2.5:3b` | 雲端軌例：`claude-haiku-4-5` |
| `OllamaEndpoint` | `http://localhost:11434` | |
| `ApiKey` | `null` | **只能**來自 user-secrets 或環境變數 `Model__ApiKey`，不得寫入任何檔案 |

**[ChatClientFactory.cs](../src/GeoNl2Sql.Core/ChatClientFactory.cs)**：`ChatClientFactory.Create(ModelOptions) → IChatClient`。

- `Ollama` → `new OllamaApiClient(new Uri(endpoint), modelId)`。
- `Anthropic` → `new AnthropicClient { ApiKey = ... }.AsIChatClient(modelId)`；`ApiKey` 為空時丟出 `InvalidOperationException` 並提示設定方式。
- 呼叫端只看得到 `IChatClient`，看不到供應商。這是「同一份程式碼僅改設定即可切換雲端／本機」（驗收 U2）的實作基礎。

### 2.4 現況：資料夾分工

**`Guardrails/`：兩層防線與錯誤消毒**

| 檔案 | 內容 |
|---|---|
| [SqlValidator.cs](../src/GeoNl2Sql.Core/Guardrails/SqlValidator.cs) | 第一層。以 `TSql160Parser` 解析，任何解析錯誤即拒絕；只接受單一批次、單一 `SELECT`；不得 `SELECT INTO`；資料表限白名單（或本陳述式內定義的 CTE）；不得使用 `OPENROWSET` 等外部來源；函式必須在白名單內。回傳是否通過與固定的拒絕原因 |
| [ReadOnlySqlExecutor.cs](../src/GeoNl2Sql.Core/Guardrails/ReadOnlySqlExecutor.cs) | 第二層的執行端。以唯讀登入 `geo_reader` 的連線執行；`QueryLimits` 預設逾時 10 秒、最多 1000 列（多讀 1 列以判斷是否截斷）；`geography`／`geometry` 欄位以位元組讀出 |
| [SqlErrorSanitizer.cs](../src/GeoNl2Sql.Core/Guardrails/SqlErrorSanitizer.cs) | 依資料庫錯誤號碼回固定訊息，回饋給模型的內容因此不含表名、欄位名或資料庫原文 |

兩層彼此獨立：關掉驗證器，`geo_reader` 仍無法寫入、無法 DDL、無法執行本資料庫的預存程序；權限定義在 [db/02_reader.sql](../db/02_reader.sql)。實測見 `feasibility-report.md` §8。

**`Nl2Sql/`：自然語言到結果的管線**

| 檔案 | 內容 |
|---|---|
| [PromptBuilder.cs](../src/GeoNl2Sql.Core/Nl2Sql/PromptBuilder.cs) | 由 `db/schema-description.md` 組出 system prompt，有 `described`（含欄位說明）與 `plain` 兩種；輸出與 M1 實驗用的提示詞逐字相同，以快照測試鎖定，因此準確率可與 M1 比較 |
| [SqlExtractor.cs](../src/GeoNl2Sql.Core/Nl2Sql/SqlExtractor.cs) | 從模型回應取出 SQL：優先取 ```sql 區塊，否則用後備規則；抽不到回傳 null |
| [Nl2SqlPipeline.cs](../src/GeoNl2Sql.Core/Nl2Sql/Nl2SqlPipeline.cs) | 生成 → 抽取 → 驗證 → 執行；任一步失敗就把上次回應與消毒後的原因附加到對話再生成，**修正上限 2 次（最多生成 3 次）**；被驗證器拒絕的 SQL 絕不送到執行器；SQL 層面的失敗不丟例外而以結果回報，模型呼叫本身的例外往外傳。回答就是查詢結果本身，不另請模型摘要 |
| [DemoSchema.cs](../src/GeoNl2Sql.Core/Nl2Sql/DemoSchema.cs) | 示範資料庫允許查詢的 6 張表，提供給 `SqlValidator` |

組裝方式：`new Nl2SqlPipeline(client, new SqlValidator(DemoSchema.Tables), executor.ExecuteAsync, schemaMarkdown).AskAsync("問題")`。

**`Spatial/`：空間資料（SQL Server `geography`）**

| 檔案 | 內容 |
|---|---|
| [GeographyReader.cs](../src/GeoNl2Sql.Core/Spatial/GeographyReader.cs) | 把 `geography` 位元組（`SqlServerBytes`）或 WKT 文字解析成 NetTopologySuite 幾何 |
| [GeoJsonBuilder.cs](../src/GeoNl2Sql.Core/Spatial/GeoJsonBuilder.cs) | 查詢結果 → `FeatureCollection`：每個空間值一個 `Feature`，其餘欄位為 `properties`；座標四捨五入到小數 6 位；沒有空間欄位時回傳 `null`（前端就不畫地圖）。環方向不修正，保留 SQL Server 的真實方向 |
| [GeoJsonValidator.cs](../src/GeoNl2Sql.Core/Spatial/GeoJsonValidator.cs) | 自寫的 RFC 7946 **結構檢查**（型別、座標巢狀、經緯度範圍、環閉合、外環逆時針）；不是完整的規格符合性測試 |
| [SpatialQueries.cs](../src/GeoNl2Sql.Core/Spatial/SpatialQueries.cs) | 兩個寫死、參數化的 SQL：`DistrictCentroid`（`geography` → `geometry` → `STCentroid()`，平面質心）與 `BufferAroundPoint`（`STBuffer`，單位公尺，**緯度在前**）。參數超出範圍時在連線前拒絕。不得使用 `EnvelopeCenter`（測試掃描原始碼把關） |

**`Agent/`：工具迴圈**

| 檔案 | 內容 |
|---|---|
| [GeoAgent.cs](../src/GeoNl2Sql.Core/Agent/GeoAgent.cs) | 以 `ChatClientAgent` 加 `FunctionInvokingChatClient` 組成；`RunAsync` 回傳完整結果，`RunStreamingAsync` 逐步送出 `ToolStarted`、`AnswerDelta`、`Completed` 事件。工具呼叫輪數有上限（`MaxToolRounds`，預設 6），超過回固定訊息、不丟例外。每次請求建立新的 `GeoTools` 與 `MapResult`，並行請求不會混資料。`PromptVersion = "v2"`（涵蓋 Agent 指示與三個工具描述；v2 加了「回答只用一般文字、不要用表格或 markdown」一條） |
| [GeoTools.cs](../src/GeoNl2Sql.Core/Agent/GeoTools.cs) | 三個工具：`query_database`（呼叫 `Nl2SqlPipeline`）、`get_district_centroid`、`buffer_around_point`。參數不合法、找不到行政區、資料庫錯誤都回固定訊息；資料庫錯誤只回消毒後的文字 |
| [MapResult.cs](../src/GeoNl2Sql.Core/Agent/MapResult.cs) | 單次請求的地圖旁路（加鎖）：GeoJSON **不經過模型**，由 Web 直接取用 |

**規劃（尚未建立）**

| 項目 | 內容 | 對應里程碑 |
|---|---|---|
| AF 中介層 | ChatClient／Agent Run／Function Invocation 三層中介層掛載護欄與稽核 | M4 |
| 稽核與 PII | 遮蔽視圖、稽核表（可選 `LEDGER = ON`） | M4 |

### 2.5 技術特點

1. **以 `IChatClient` 為縫隙。** 供應商差異（認證、端點、訊息格式）全收在工廠內。
2. **設定與機密分離。** 一般設定放 `appsettings.json`，金鑰走 user-secrets／環境變數；公開儲存庫以 gitleaks（本機）與 GitHub Push protection（遠端）雙重把關。
3. **模型品質不等價是設計前提。** 介面可切換不代表行為相同，見 §5.3 的 tool calling 實測；本機 `qwen2.5:3b` 在多工具情境不穩定，Web 預設走雲端模型。
4. **能由程式確定的事不交給模型。** 質心與緩衝區是寫死的參數化 SQL，模型只給參數；GeoJSON 走旁路，不進模型的上下文；工具迴圈的輪數上限由程式決定。

---

## 3. GeoNl2Sql.Web

[src/GeoNl2Sql.Web/](../src/GeoNl2Sql.Web/)

### 3.1 角色

使用者介面：ASP.NET Core MVC（Controller + Razor View）。使用者用中文提問，頁面顯示逐步進度、逐段出現的回答、結果表與 Leaflet 地圖；同時提供 JSON 端點給評估與測試使用。

### 3.2 現況

| 項目 | 現況 |
|---|---|
| 專案型別 | `Microsoft.NET.Sdk.Web`，引用 `GeoNl2Sql.Core` |
| 啟動 | [Program.cs](../src/GeoNl2Sql.Web/Program.cs)：`GeoAgent`（含管線、執行器）註冊為單例；`AgentRunner`、`AgentStreamRunner` 兩個委派分別指向 `RunAsync`、`RunStreamingAsync`，所以控制器測試不需要模型與資料庫 |
| 端點 | `POST /query`（[QueryController](../src/GeoNl2Sql.Web/Controllers/QueryController.cs)）回傳完整 JSON：`success`、`answer`、`sql`、`columns`、`rows`、`truncated`、`geoJson`、`toolsCalled`、`error`，欄位名稱視為契約。`POST /query/stream`（[QueryStreamController](../src/GeoNl2Sql.Web/Controllers/QueryStreamController.cs)）以逐行 JSON（NDJSON）輸出事件：`step`（開始呼叫某工具）、`answer`（回答文字片段）、`result`（最後的完整結果）、`error`（固定訊息） |
| 輸入與失敗 | 問題為空或超過 500 字元回 400；失敗時只回固定訊息（工具輪數超過、查詢失敗、模型服務出錯），不回堆疊或資料庫原文，例外細節只寫伺服器日誌 |
| 前端 | [Views/Home/Index.cshtml](../src/GeoNl2Sql.Web/Views/Home/Index.cshtml) 與 [wwwroot/js/app.js](../src/GeoNl2Sql.Web/wwwroot/js/app.js)：用 `fetch` 讀串流，回答以打字機節奏顯示；表格欄位名稱轉成中文；伺服器來的文字一律以 `textContent` 寫入，不拼 `innerHTML` |
| 樣式 | Tailwind CSS，以官方獨立 CLI 編譯，不需 Node。輸入檔 `Styles/app.css`，編譯結果 `wwwroot/css/app.css` **入版控**（clone 後不用先編譯）；修改樣式後執行 `tools/build-css.ps1`（CLI 下載到被忽略的 `tools/bin/`） |
| 地圖 | Leaflet 1.9.4 以本地檔案放在 `wwwroot/lib/leaflet`，底圖為 OpenStreetMap 圖磚並標註出處 |
| 模型與機密 | `appsettings.json` 預設 `Anthropic`／`claude-haiku-4-5`，金鑰走 user-secrets（與 Eval、Tests 共用同一個 `UserSecretsId`，讀 `Model:ApiKey` 與 `ConnectionStrings:Reader`）；改用本機模型：`Model__Provider=Ollama`、`Model__ModelId=qwen2.5:3b` |
| 本機網址 | `http://localhost:5001`（http）；`https://localhost:7069`（https），見 `Properties/launchSettings.json` |

啟動方式：

```powershell
dotnet run --project src/GeoNl2Sql.Web --launch-profile http
```

### 3.3 安全範圍

Web 端點**沒有使用者驗證、角色、PII 遮蔽與稽核**（屬 M4），僅供本機展示，**不得直接對外部署**；查詢結果可能含合成的個資欄位。確定性的兩層防護（驗證器加唯讀 login）在 Web 路徑上照常生效。OpenStreetMap 圖磚僅適合低流量展示，公開部署需另換圖磚來源。

### 3.4 技術特點

- 選 MVC 而非 Blazor／SPA：符合作者既有技術線，且 Razor 伺服器端渲染讓 Demo 部署簡單（2026-10-07 決議）。
- Web 只經 Core 取得能力，不直接碰模型 SDK 或資料庫。
- 串流版與完整版共用同一套驗證與固定錯誤訊息；呼叫工具前模型說的旁白不算回答，會被清掉。

---

## 4. GeoNl2Sql.Tests

[tests/GeoNl2Sql.Tests/](../tests/GeoNl2Sql.Tests/)

### 4.1 角色

單元測試分兩類：離線測試不連 SQL Server、不呼叫任何模型 API，讓 CI 與貢獻者隨時可跑；少數邊界測試標記 `[Trait("Category", "Database")]`，需要本機有種子資料庫與 `geo_reader`（見 README），以 `--filter "Category!=Database"` 排除。

### 4.2 使用套件

| 套件 | 版本 | 用途 |
|---|---|---|
| `xunit` | 2.9.3 | 測試框架（`Using Include="Xunit"` 全域匯入） |
| `xunit.runner.visualstudio` | 3.1.4 | 讓 VS／`dotnet test` 發現測試 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 測試主機 |
| `coverlet.collector` | 6.0.4 | 涵蓋率收集 |

引用專案：`GeoNl2Sql.Core`。

### 4.3 現況與規劃

現況（共 279 項；離線 219 項、`Database` 60 項）：

| 位置 | 內容 |
|---|---|
| `Guardrails/attack-sql.json`、`benign-sql.json` | 60 條攻擊 SQL（含註解插入、大小寫變形、`CHAR()` 拼接、批次分隔、巢狀 CTE、`EXEC`、多語句）與 13 條「看似可疑但無害」的查詢；每條攻擊標有 `dbExpect`（資料庫層的預期結果） |
| `CorpusIntegrityTests` | 鎖住語料庫的筆數與各類別數，避免「阻擋率 100%」的分母悄悄縮水 |
| `SqlValidatorTests` | 60 條攻擊必須全部拒絕且附原因；正常查詢與 30 題標準 SQL 必須放行；另有只違反單一規則的合法語法案例 |
| `SqlErrorSanitizerTests` | 回饋訊息只依錯誤號碼，不含資料庫原文 |
| `Prompting/` | `PromptBuilder` 的輸出與已記錄的提示詞快照逐字相同；`SqlExtractor` 的抽取規則 |
| `Nl2Sql/Nl2SqlPipelineTests` | 以腳本化的假 `IChatClient` 與假執行器驗證有界重試：永遠失敗時恰好生成 3 次、被拒絕的 SQL 不送執行器、回饋不含資料庫原文、逾時與截斷的處理 |
| `Spatial/` | GeoJSON 組裝與結構檢查（含故意壞掉的輸入）、4326 ↔ 3826 往返（以 ProjNet 為對照）、`SpatialQueries` 原始碼不得含 `EnvelopeCenter`、緩衝區參數在連線前被拒絕 |
| `Agent/GeoAgentTests` | 以腳本化的假 `IChatClient` 驗證工具迴圈：輪數上限（1／3／6 輪都恰好停止）、模型收到的工具結果不含座標、20 個並行請求的地圖資料互不混入、非法參數被擋、串流事件順序與超限 |
| `Web/` | 兩個控制器以假的 Agent 測試序列化契約、問題長度上限、串流事件格式，以及失敗路徑不洩漏例外細節 |
| `Database/Spatial*Tests`、`GeoToolsDatabaseTests`（`Database`） | 全部行政區與基地台轉 GeoJSON 後通過檢查、質心與緩衝區對真實資料庫、空間索引的執行計畫探測 |
| `Database/ReadOnlyBoundaryTests`（`Database`） | 60 條攻擊繞過驗證器直送唯讀執行器，前後比對 6 張表、`sys.objects` 與 `geo_reader` 權限的雜湊；逾時、列數上限；30 題標準 SQL 以 `geo_reader` 執行結果與管理身分相同 |

需連模型的驗證歸 Eval，不放這裡。測試專案另引用 `ProjNET` 2.1.0 與 `GeoNl2Sql.Web`。尚待補的測試：PII 遮蔽規則（M4）。`UnitTest1` 是範本留下的空測試。

---

## 5. GeoNl2Sql.Eval

[eval/GeoNl2Sql.Eval/](../eval/GeoNl2Sql.Eval/)

### 5.1 角色

可執行的主控台專案（`OutputType=Exe`），以子命令分派：`hello`（hello-agent）、`seed`（建示範資料庫）、`spike`（M1 準確率量測）、`pipeline`（M2 端到端量測）、`ask`（手動問一題）、`agent`（M3 工具選擇量測）。它是目前唯一真正「動起來」的專案；後續的消融實驗與攻擊語料庫執行器也放這裡。

### 5.2 使用套件

| 套件 | 版本 | 用途 |
|---|---|---|
| `Microsoft.Extensions.Configuration.Json` | 10.0.12 | 讀 `appsettings.json` |
| `Microsoft.Extensions.Configuration.UserSecrets` | 10.0.12 | 讀 user-secrets（`UserSecretsId` 已設定於 csproj） |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | 10.0.12 | 讀環境變數（如 `Model__ApiKey`） |
| `Microsoft.Extensions.Configuration.Binder` | 10.0.12 | `GetSection(...).Get<T>()` 綁定成 `ModelOptions` |

AF、`Microsoft.Extensions.AI`、`OllamaSharp`、`Anthropic` 經專案引用由 `Core` 傳遞進來，Eval 自己不重複宣告。

### 5.3 現況：hello-agent（[HelloCommand.cs](../eval/GeoNl2Sql.Eval/Hello/HelloCommand.cs)）

用最小的程式驗證「同一份程式碼、僅改設定即可切換模型」與「工具能被呼叫」。

**執行流程與呼叫的方法**

| 步驟 | 程式 | 說明 |
|---|---|---|
| 1 | `new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build()` | 後加入的來源覆蓋先加入的；機密因此可覆蓋檔案值 |
| 2 | `config.GetSection("Model").Get<ModelOptions>()` | 綁定選項 |
| 3 | 命令列覆寫：`args[1]` 解析為 `ModelProvider`、`args[2]` 為 `ModelId` | 例：`hello Anthropic claude-haiku-4-5` |
| 4 | `AIFunctionFactory.Create(GetWeather, name: "GetWeather")` | 把 C# 方法包成 `AIFunction`；方法與參數上的 `[Description]` 會進入工具 schema |
| 5 | `ChatClientFactory.Create(options)` | 取得 `IChatClient` |
| 6 | `new ChatClientAgent(chatClient, instructions:, name:, tools:)` | AF 的 Agent，宣告型別為 `AIAgent` |
| 7 | `await agent.RunAsync("What is the weather in Taipei?")`，印出 `response.Text` | Agent 內部處理「模型要求呼叫工具 → 執行 → 把結果回傳模型」的迴圈 |

**設定來源**：[appsettings.json](../eval/GeoNl2Sql.Eval/appsettings.json) 預設為 `Ollama` + `qwen2.5:3b`。csproj 以 `CopyToOutputDirectory=PreserveNewest` 複製到輸出目錄。

**執行**

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- hello                              # 本機 Ollama
dotnet run --project eval/GeoNl2Sql.Eval -- hello Anthropic claude-haiku-4-5   # 雲端（需先設 key）
dotnet run --project eval/GeoNl2Sql.Eval -- hello Ollama qwen2.5-coder:3b      # 重現 coder 版失敗
```

註：上面是 `hello` 子命令的流程（程式在 `Hello/HelloCommand.cs`）；`Program.cs` 現在只負責讀設定並依第一個參數分派子命令。

### 5.4 M0 實測結論（本機 tool calling）

| 模型 | 結果 | 備註 |
|---|---|---|
| `qwen2.5-coder:3b` | 失敗：把工具呼叫以純文字 JSON 印出，工具未執行（共觀察 6 次） | 引數還多包一層，如 `{"city": {"city": "Taipei"}}`。`ollama show` 顯示該模型宣告支援 tools，故非 Ollama 設定問題 |
| `qwen2.5:3b` | 20/20 成功呼叫工具並回答 | 判定方式為輸出含工具回傳值（「26°C」）；僅單一簡單工具 |
| 雲端 `claude-haiku-4-5` | 成功呼叫工具並回答 | `hello Anthropic claude-haiku-4-5`，回應內容正確 |

**兩個容易踩的坑**

1. **頂層陳述式裡的區域函式會被編譯器改名。** 直接 `AIFunctionFactory.Create(GetWeather)` 會讓工具名稱變成 `_Main_g_GetWeather_0_0`，模型看到毫無語意的名稱。一律傳 `name:` 明確命名，或改用具名類別的方法。（改名後 coder 版仍失敗，所以名稱只是次要問題，不是主因。）
2. **模型標示支援 tools 不等於可用。** coder 特化版對工具協定的遵循度低於標準對話版。`qwen2.5:3b` 通過的僅是單一簡單工具；多工具、複雜引數是否穩定要到 M2 之後才知道，因此 R3 的「純文字 SQL 由程式解析」降級模式仍保留。

### 5.5 現況：資料庫、準確率量測與端到端管線

| 子命令 | 內容 |
|---|---|
| `seed` | 以固定亂數重建示範資料庫 `GeoNl2SqlDemo`（執行 `db/01_schema.sql` 與 `db/02_reader.sql`，並依 `ConnectionStrings:Reader` 建立 `geo_reader`）；每次資料都相同 |
| `spike` | M1 的丟棄式準確率量測，30 題標準集見 [m1-implementation-plan.md](m1-implementation-plan.md) |
| `pipeline` | M2 端到端量測：逐題呼叫 `Nl2SqlPipeline`，與標準答案以**執行結果**比對（`Common/ResultComparer.cs`），並統計「驗證器誤擋」（被拒絕的 SQL 改以 `geo_reader` 執行後與標準答案相同）。標準 SQL 走 `Demo` 連線，生成的 SQL 只走 `Reader` 連線。選項 `--limit`、`--runs`、`--provider`、`--model`，另有 `--fake` 以固定回應不呼叫模型，驗證流程不花費用。結果寫到 `Results/`（不進 git），並印出模型呼叫次數與 token 用量 |
| `ask "問題"` | 手動問一題，印出每次嘗試的失敗類型、SQL、欄位、列數、是否截斷與前 20 列 |
| `agent` | M3 工具選擇量測：對凍結的 12 題（`Questions/tools.json`）逐題跑 `GeoAgent`，印出選了哪個工具、參數是否正確、呼叫次數與 token 用量，結果寫 JSON 到 `Results/`。選項同 `pipeline`（`--limit`、`--provider`、`--model`） |

量測結果見 `feasibility-report.md` §7（M1）、§8（M2）與 §9（M3）。

### 5.6 規劃

- M5：評估管線、消融實驗、攻擊語料庫（結果快取以「題目＋模型＋提示詞版本」為鍵）。

---

## 6. 設定與機密處理

| 層級 | 位置 | 內容 |
|---|---|---|
| 一般設定 | `appsettings.json`（進 git） | 供應商、模型 ID、Ollama 端點、（M1 起）無密碼的資料庫連線字串範本 |
| 本機開發機密 | user-secrets | `Model:ApiKey`；`ConnectionStrings:Demo`（覆蓋 appsettings.json 的範本值）；`ConnectionStrings:Reader`（`geo_reader` 的 SQL 驗證連線，含密碼；Eval 與 Tests 共用同一個 `UserSecretsId`） |
| 佈署機密 | 環境變數 | `Model__ApiKey`；含密碼的連線字串用 `ConnectionStrings__Demo`、`ConnectionStrings__Reader` |
| 提交前 | 本機 pre-commit：gitleaks 8.30.1 | 掃描 staged 變更 |
| 推送時 | GitHub Secret scanning + Push protection | 儲存庫已啟用 |

設定金鑰（自行建立並貼入，不寫入任何檔案）：

```powershell
dotnet user-secrets set "Model:ApiKey" "<your key>" --project eval/GeoNl2Sql.Eval
```

---

## 7. 已知限制與待辦

| 項目 | 狀態 |
|---|---|
| 目標框架為 net9.0 | 之後升級到 .NET 10 時，一併調整四個專案的 `TargetFramework`、`global.json`、README 與相關文件 |
| 本機模型的端到端準確率 | 未在 M2 量測；M1 的數字（40–47%）顯示瓶頸在語法層級，見 `feasibility-report.md` §7 |
| 模型拒答含 `SELECT` 字樣時的失敗訊息 | 安全失敗，但訊息是「SQL 語法無法解析」而非「模型拒絕」，見 `feasibility-report.md` §8.5 |
| 函式白名單可能偏嚴 | 目前標準題與正常查詢集無誤擋；日後放寬須逐項記錄理由，且不放寬單一 `SELECT`、資料表白名單等基本規則 |
| Web 沒有驗證、角色、PII 遮蔽與稽核 | 僅供本機展示，不得對外部署；M4 補上，見 §3.3 |
| 空間索引在自然計畫中未被使用 | 資料量小，最佳化工具選擇掃描；索引可被使用，見 `feasibility-report.md` §9.4 |
| 質心是平面（度數）質心 | 小範圍行政區誤差可忽略；不規則大區域或高緯度會有偏差，見 `feasibility-report.md` §9.2 |
| 本機 `qwen2.5:3b` 在多工具情境不穩定 | 一般查詢會編造「查詢失敗」；Web 預設走雲端，M3 未嘗試改善，見 `feasibility-report.md` §9.5 |
| 持續整合 | 尚無 GitHub Actions 工作流程（離線測試已可在無資料庫環境執行） |
| `docs/tool calling失敗的原因.docx` | M0 排查本機模型 tool calling 失敗時的分析文件 |
