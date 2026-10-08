# 專案技術文件（四個專案）

| 項目 | 內容 |
|---|---|
| 讀者 | 想深入了解本方案技術內容的開發者 |
| 文件日期 | 2026-10-08（對應 commit `b7296be`，里程碑 M0） |
| 範圍 | 方案內四個專案：`GeoNl2Sql.Core`、`GeoNl2Sql.Web`、`GeoNl2Sql.Tests`、`GeoNl2Sql.Eval` |
| 閱讀提醒 | 每個專案都分成「**現況**（已存在於程式碼）」與「**規劃**（尚未實作，來自 `feasibility-report.md`）」。尚未實作的部分不當成既成事實描述。 |

---

## 1. 整體結構

```
GeoNl2Sql.slnx
├── src/
│   ├── GeoNl2Sql.Core/      類別庫：模型工廠、（規劃）Agent、護欄、資料存取、空間查詢
│   └── GeoNl2Sql.Web/       ASP.NET Core MVC：Controller + Razor View + Leaflet
├── tests/
│   └── GeoNl2Sql.Tests/     xUnit 單元測試（離線可跑）
└── eval/
    └── GeoNl2Sql.Eval/      主控台：hello-agent、評估、消融實驗
```

### 1.1 依賴方向

```
Web  ──▶ Core ◀── Tests
              ▲
              └── Eval
```

- 三個專案都只引用 `Core`；`Core` 不引用任何專案。Web、Tests、Eval 彼此不相依。
- 為何只有四個專案：2026-10-08 由六個合併而來（原 Data、Orchestration、Core 合為一個 Core）。依據 `CLAUDE.md` 的「Simplicity First」，單一個人專案沒有第二個消費者能共用資料層，拆專案只增加維護成本。邊界改以 **Core 內的資料夾**區分（見 §2.4），若日後真的需要獨立發佈再拆。

### 1.2 共通技術基線

| 項目 | 目前值 | 說明 |
|---|---|---|
| 目標框架 | `net9.0`（四個專案一致） | **暫時性降級**：Visual Studio 2022 17.14 無法載入 .NET 10 SDK 專案。安裝 VS 2026 後改回 `net10.0` |
| SDK 鎖定 | [global.json](../global.json)：`9.0.306`，`rollForward: latestFeature` | 同上，改回時一併改 `10.0.401` |
| 語言設定 | `Nullable` 與 `ImplicitUsings` 皆啟用 | |
| 建置指令 | `dotnet build GeoNl2Sql.slnx`、`dotnet test` | 現況：0 警告 0 錯誤，測試 1/1 通過（為空測試） |
| 版本管理 | 預設分支 `main`；commit 前由本機 pre-commit hook 執行 gitleaks | hook 在 `.git/hooks`，不隨儲存庫散佈，clone 後需自行重設 |

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

### 2.3 現況：兩個檔案

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

### 2.4 規劃：資料夾分工（尚未建立）

| 資料夾 | 內容 | 對應里程碑 |
|---|---|---|
| `Agent/` | `ChatClientAgent` 組裝、工具定義、AF 三層中介層（ChatClient／Agent Run／Function Invocation）掛載護欄與稽核 | M2、M4 |
| `Data/` | SQL Server 存取、`Microsoft.Data.SqlClient`、唯讀連線、schema 提供者 | M2 |
| `Guardrails/` | `Microsoft.SqlServer.TransactSql.ScriptDom` 的 AST 白名單驗證、有界重試（上限 2 次）、錯誤訊息消毒 | M2 |
| `Spatial/` | `geography` 查詢、`NetTopologySuite` 組 GeoJSON、質心與緩衝區 | M3 |
| 稽核與 PII | 遮蔽視圖、稽核表（可選 `LEDGER = ON`） | M4 |

這些套件尚未加入 csproj；實際加入時以各里程碑為準。

### 2.5 技術特點

1. **以 `IChatClient` 為縫隙。** 供應商差異（認證、端點、訊息格式）全收在工廠內。
2. **設定與機密分離。** 一般設定放 `appsettings.json`，金鑰走 user-secrets／環境變數；公開儲存庫以 gitleaks（本機）與 GitHub Push protection（遠端）雙重把關。
3. **模型品質不等價是設計前提。** 介面可切換不代表行為相同，見 §5.3 的 tool calling 實測。

---

## 3. GeoNl2Sql.Web

[src/GeoNl2Sql.Web/](../src/GeoNl2Sql.Web/)

### 3.1 角色

使用者介面：ASP.NET Core MVC（Controller + Razor View），規劃以 Leaflet 顯示 GeoJSON 地圖，並提供 JSON 端點給評估程式呼叫。

### 3.2 現況：尚是範本骨架

目前內容是 `dotnet new mvc` 的範本，**尚未接上任何業務邏輯**。

| 項目 | 現況 |
|---|---|
| 專案型別 | `Microsoft.NET.Sdk.Web`，引用 `GeoNl2Sql.Core` |
| 啟動 | [Program.cs](../src/GeoNl2Sql.Web/Program.cs)：`AddControllersWithViews()`、`UseHttpsRedirection`、`UseRouting`、`UseAuthorization`、`MapStaticAssets`、預設路由 `{controller=Home}/{action=Index}/{id?}` |
| Controller | [HomeController](../src/GeoNl2Sql.Web/Controllers/HomeController.cs)：`Index`、`Privacy`、`Error`（`Error` 帶 `ResponseCache`，不快取） |
| View | `Views/Home/Index`、`Privacy`、`Views/Shared/_Layout`、`Error`、驗證用 partial |
| 前端資源 | `wwwroot/lib` 內為範本附的 Bootstrap、jQuery、jQuery Validation（尚未使用 Leaflet） |
| 本機網址 | `http://localhost:5001`（http）；`https://localhost:7069`（https），見 `Properties/launchSettings.json` |

啟動方式：

```powershell
dotnet run --project src/GeoNl2Sql.Web --launch-profile http
```

### 3.3 規劃

- 問答頁：使用者輸入自然語言 → Controller action 呼叫 Core 的 Agent → 回傳結果表格與 GeoJSON。
- 地圖頁：Leaflet 疊圖（行政區多邊形、基地台點位、緩衝區）。
- 另保留 JSON 端點（供 Eval 以 HTTP 方式驗證端到端，M5 前決定是否需要）。
- 機密：金鑰一律走 user-secrets，Web 專案需要時另行設定自己的 `UserSecretsId`（現在尚無）。

### 3.4 技術特點

- 選 MVC 而非 Blazor／SPA：符合作者既有技術線，且 Razor 伺服器端渲染讓 Demo 部署簡單（2026-10-07 決議）。
- Web 只經 Core 取得能力，不直接碰模型 SDK 或資料庫。

---

## 4. GeoNl2Sql.Tests

[tests/GeoNl2Sql.Tests/](../tests/GeoNl2Sql.Tests/)

### 4.1 角色

可離線執行的單元測試：不連 SQL Server、不呼叫任何模型 API，讓 CI 與貢獻者隨時可跑。

### 4.2 使用套件

| 套件 | 版本 | 用途 |
|---|---|---|
| `xunit` | 2.9.3 | 測試框架（`Using Include="Xunit"` 全域匯入） |
| `xunit.runner.visualstudio` | 3.1.4 | 讓 VS／`dotnet test` 發現測試 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 測試主機 |
| `coverlet.collector` | 6.0.4 | 涵蓋率收集 |

引用專案：`GeoNl2Sql.Core`。

### 4.3 現況與規劃

- 現況：只有範本留下的空測試 `UnitTest1.Test1`，用來確認建置與測試管線可通。
- 規劃：M2 起放入 AST 白名單的案例表（含註解插入、`CHAR()` 拼接、批次分隔等繞過手法）、`ChatClientFactory` 設定驗證、PII 遮蔽規則。需連資料庫或模型的驗證歸 Eval，不放這裡。

---

## 5. GeoNl2Sql.Eval

[eval/GeoNl2Sql.Eval/](../eval/GeoNl2Sql.Eval/)

### 5.1 角色

可執行的主控台專案（`OutputType=Exe`），放 hello-agent，以及後續的評估、消融實驗與攻擊語料庫執行器。它是目前唯一真正「動起來」的專案。

### 5.2 使用套件

| 套件 | 版本 | 用途 |
|---|---|---|
| `Microsoft.Extensions.Configuration.Json` | 10.0.12 | 讀 `appsettings.json` |
| `Microsoft.Extensions.Configuration.UserSecrets` | 10.0.12 | 讀 user-secrets（`UserSecretsId` 已設定於 csproj） |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | 10.0.12 | 讀環境變數（如 `Model__ApiKey`） |
| `Microsoft.Extensions.Configuration.Binder` | 10.0.12 | `GetSection(...).Get<T>()` 綁定成 `ModelOptions` |

AF、`Microsoft.Extensions.AI`、`OllamaSharp`、`Anthropic` 經專案引用由 `Core` 傳遞進來，Eval 自己不重複宣告。

### 5.3 現況：hello-agent（[Program.cs](../eval/GeoNl2Sql.Eval/Program.cs)）

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

註：目前 `args[0]`（`hello`）並未被判讀，Program 不論第一個參數為何都執行 hello-agent；第二、三個參數才有作用。M1 加入 `seed`、`spike` 等子命令時會改成真正的子命令分派。

### 5.4 M0 實測結論（本機 tool calling）

| 模型 | 結果 | 備註 |
|---|---|---|
| `qwen2.5-coder:3b` | 失敗：把工具呼叫以純文字 JSON 印出，工具未執行（共觀察 6 次） | 引數還多包一層，如 `{"city": {"city": "Taipei"}}`。`ollama show` 顯示該模型宣告支援 tools，故非 Ollama 設定問題 |
| `qwen2.5:3b` | 20/20 成功呼叫工具並回答 | 判定方式為輸出含工具回傳值（「26°C」）；僅單一簡單工具 |
| 雲端 `claude-haiku-4-5` | **尚未量測** | 待使用者設好 user-secrets 金鑰 |

**兩個容易踩的坑**

1. **頂層陳述式裡的區域函式會被編譯器改名。** 直接 `AIFunctionFactory.Create(GetWeather)` 會讓工具名稱變成 `_Main_g_GetWeather_0_0`，模型看到毫無語意的名稱。一律傳 `name:` 明確命名，或改用具名類別的方法。（改名後 coder 版仍失敗，所以名稱只是次要問題，不是主因。）
2. **模型標示支援 tools 不等於可用。** coder 特化版對工具協定的遵循度低於標準對話版。`qwen2.5:3b` 通過的僅是單一簡單工具；多工具、複雜引數是否穩定要到 M2 之後才知道，因此 R3 的「純文字 SQL 由程式解析」降級模式仍保留。

### 5.5 規劃

- M1：資料庫建置（`seed`）、30 題標準集、準確率 spike 腳本，見 [m1-implementation-plan.md](m1-implementation-plan.md)。
- M5：評估管線、消融實驗、攻擊語料庫（結果快取以「題目＋模型＋提示詞版本」為鍵）。

---

## 6. 設定與機密處理

| 層級 | 位置 | 內容 |
|---|---|---|
| 一般設定 | `appsettings.json`（進 git） | 供應商、模型 ID、Ollama 端點、（M1 起）無密碼的資料庫連線字串範本 |
| 本機開發機密 | user-secrets | `Model:ApiKey`；`ConnectionStrings:Demo`（覆蓋 appsettings.json 的範本值） |
| 佈署機密 | 環境變數 | `Model__ApiKey`；含密碼的連線字串用 `ConnectionStrings__Demo` |
| 提交前 | 本機 pre-commit：gitleaks 8.30.1 | 掃描 staged 變更 |
| 推送時 | GitHub Secret scanning + Push protection | 已由作者確認啟用 |

設定金鑰（由使用者自行建立並貼入，不經過 AI 助理，也不寫入任何檔案）：

```powershell
dotnet user-secrets set "Model:ApiKey" "<your key>" --project eval/GeoNl2Sql.Eval
```

---

## 7. 已知限制與待辦（截至本文件日期）

| 項目 | 狀態 |
|---|---|
| 目標框架暫為 net9.0 | 待安裝 VS 2026 後改回 net10.0 與 global.json 10.0.401，並同步 README、docs、記憶檔 |
| 雲端軌 hello-agent | 待使用者設定金鑰後補測，並更新 M0 紀錄 |
| 殘留空資料夾 `src/GeoNl2Sql.Orchestration` | 被程式占用刪不掉，不在 git 內；關閉 VS 後手動刪除 |
| Web 仍為範本 | M2 起接 Core |
| `Tests` 僅有空測試 | M2 起補 |
| `docs/tool calling失敗的原因.docx` | Gemini 的分析文件，已隨 `b7296be` 進入公開儲存庫；若不想公開需另行移出追蹤 |
