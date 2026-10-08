# GeoEnterprise-Agent 可行性報告（v2：作品集專案版）

| 項目 | 內容 |
|---|---|
| 專案定位 | 個人開發、公開 GitHub、以 README 與 Demo 影片作為「AI 應用工程師」求職履歷；情境仿照企業實務（電信／製造），但**不對接任何真實企業或客戶** |
| 需求來源 | `gemini-code-1791363428029.md` + 作者於 2026-10-07 的修正意見 |
| 報告日期 | 2026-10-07（v1 為企業交付視角，本版依作者意見全面改寫） |
| 評估基準 | 本機環境實測、NuGet 版本查核、公開文獻 |
| 整體判定 | **可行，且比 v1 的企業版更容易成功。** 拿掉客戶 schema、資安稽核、ISO 對應之後，剩下的風險主要是「做不完」與「評估數字可信度」，而不是技術。 |

---

## 0. 本版採納的決議（作者意見 → 對規劃的影響）

| # | 作者決議 | 本版處理 |
|---|---|---|
| D1 | .NET 9 太舊就升 10 | 採 **.NET 10 LTS / C# 14**；本機需安裝 .NET 10 SDK（現僅有 9.0.306） |
| D2 | 企業主流用 AF 就用 AF | 採 **Microsoft Agent Framework 1.x**（`Microsoft.Agents.AI` 1.23.0）；不使用 Semantic Kernel，也不必再切抽象縫 |
| D3 | Llama-3 8B 跑不動就降級；或花少量費用接雲端閉源模型錄 Demo | **雙軌保留**：雲端閉源模型為「主軌」（Demo／評估數據），本機小模型為「副軌」（證明可切換、誠實呈現落差）。兩者都經 `IChatClient` 接入 |
| D4 | 不必強調 ISO 27001 控制項 | **移除**控制項對應矩陣與 ISO 42001／EU AI Act 對應；保留「實作得出來、說得清楚、測得出數字」的實務防護 |
| D5 | 先流暢做完，再迭代加強 | 改為**里程碑制**：每個里程碑結束都是可公開的版本（打 tag），§5 |
| D6 | 公開 GitHub、README 當履歷 | 新增**公開儲存庫風險**（金鑰外洩、資料授權）與「README 要放的證據」清單（§4.3、§5） |

因 D3–D6 而**不再適用**的 v1 內容：客戶 schema 取得（原 U1/R6）、客戶資安審查否決（原 R8）、ISO 27001 範圍誤解（原 §1.5）、Gate 8 的「外部紅隊 + 客戶 Demo」、「Developer Edition 為前置條件」、「資安稽核／架構師評審組成」等。

---

## 1. 技術可行性

### 1.1 逐項判定

| 需求 | 判定 | 說明 |
|---|---|---|
| AF 編排 + 自訂工具（C#） | ✅ 可行 | `Microsoft.Agents.AI` 1.23.0（2026-09-29）、`Microsoft.Agents.AI.OpenAI` 1.23.0 均已發佈；AF 有三層中介層（ChatClient／Agent Run／Function Invocation），正好用來掛護欄與稽核 |
| 雙軌模型 | ✅ 介面可行 ⚠️ 品質不等價 | 經 `Microsoft.Extensions.AI`（10.10.0）的 `IChatClient` 抽換；本機用 `OllamaSharp` 5.5.0；雲端用該廠商 SDK（Anthropic 12.53.0）或 OpenAI 相容端點 |
| NL2SQL + 精簡 schema 注入 | ✅ 可行 | 作品集的 schema 自己設計，規模可控（10–15 張表），不需 schema 檢索 |
| SQL 破壞性指令阻擋 | ✅ 可做到 100% | 用 `Microsoft.SqlServer.TransactSql.ScriptDom` 180.117.0 解析 AST 做白名單，**不要用字串黑名單**；再搭配 DB 層唯讀 |
| 唯讀隔離 | ✅ 可行 | 獨立 login + 只授權 `ai` schema 的視圖 + `DENY` 寫入／EXEC |
| 自我修正 | ✅ 可行 | 有界重試（上限 2 次）；回饋給模型的錯誤訊息要消毒 |
| GIS（GeoJSON／質心／緩衝區） | ✅ 可行，有陷阱 | 見 §1.3：質心走 geometry 轉換、緩衝區用 `geography.STBuffer`；SQL Server 沒有 GeoJSON，用 `NetTopologySuite` 2.6.0 + `NetTopologySuite.IO.GeoJSON` 4.0.0 組裝，`ProjNet` 2.1.0 僅作驗證對照 |
| Prompt Injection 防禦 | ⚠️ 可緩解、不可根除 | 做成「多層防禦 + 攻擊語料庫 + 實測數字」，這正是履歷上最有說服力的呈現 |
| PII 角色化遮蔽 | ✅ 可行 | 遮蔽必須發生在**資料進入 LLM 之前**（§1.2） |
| 稽核軌跡 | ✅ 可行（選配） | 一般稽核表即可滿足作品集；想加分用 `LEDGER = ON`（本機 Express 實測可用） |
| Docker Compose | ⚠️ 部分調整 | 本機**未安裝 Docker／WSL2**；Ollama 建議**原生跑在 Windows 主機**，Compose 只編排 App + SQL Server（§1.4） |
| 評估管線 | ✅ 可行 | 資料與標準答案都由作者掌握，難度由 v1 的 L5 降為 L3 |
| Web 層 + 地圖 | ✅ 可行（新增建議） | 採 **ASP.NET Core MVC**（Controller + Razor View，符合作者既有技術線；2026-10-07 決議）；頁面以 Leaflet 顯示 GeoJSON，是 Demo 影片最有視覺說服力的部分。查詢以 Controller action 提供，另保留 JSON 端點供評估程式呼叫 |

### 1.2 三個必須保留的架構修正（v1 發現，仍然有效）

1. **PII 遮蔽要在資料進入 LLM 之前。** 若只在「回傳給使用者」時遮蔽，原始個資已經在 prompt 裡送到雲端模型。做法：為不同角色建立**遮蔽視圖**，並對底層敏感欄位 `DENY SELECT`，讓 LLM 即使生成 `SELECT id_number` 也會在 DB 層被拒。Dynamic Data Masking 不是安全邊界（可被 `CAST`、`WHERE` 試探繞過），不要拿它當主防線。
2. **破壞性指令防護要靠「AST 白名單 + DB 權限」兩層，不是黑名單。** 黑名單可被註解插入、`CHAR()` 拼接、批次分隔等繞過。心態：LLM 是不可信的輸入來源，所有實質防護落在 DB 邊界，而非 prompt。
3. **不要宣稱「高並發低延遲」。** 一次 LLM 往返就是秒級，自我修正再翻倍。履歷上更站得住腳的說法是：**可預測的延遲上界、串流輸出、對高頻問題做快取**，並附上實測 p50／p95。

### 1.3 已於本機實測的 SQL Server 事實（SQL Server 2022 Express 16.0.1000.6）

| # | 事實 | 實測 | 對設計的影響 |
|---|---|---|---|
| 1 | `geography` 沒有 `STCentroid()` | 錯誤 6506 | 不能直接對 `geography` 算質心；**改走 geometry 轉換（見事實 6）** |
| 2 | `EnvelopeCenter()` 不是質心 | 對方形回傳 `POINT(121.5 25.5008…)`，是外接框中心 | 不規則行政區會有偏差，不可拿來假冒質心 |
| 3 | `geography` 不接受投影座標系（如 TWD97／EPSG:3826） | 錯誤 24204 | 儲存統一用 WGS84（4326）；需公尺單位的運算另行投影 |
| 4 | 沒有重投影函式 | `STAsText()` 原樣回傳 | 投影用 `ProjNet` 在應用層做 |
| 5 | `LEDGER = ON` 在 Express 可用 | 建表成功 | 稽核加分項可零成本採用 |
| 6 | **`geography` → `geometry`（WKB，SRID 4326）後 `STCentroid()`，再轉回 `geography`，可行**（使用者查證，已於本機實測） | 方形 `POLYGON(121.5~121.6, 25.0~25.1)` 得 `POINT (121.55 25.05)`；`EnvelopeCenter()` 同圖為 `25.050008…`（外接框中心，不同物） | 質心可在 SQL 端完成。注意：此為**平面（度數）質心**，小範圍（鄉鎮市區）誤差可忽略，大範圍或高緯度會有偏差；以 NetTopologySuite 投影後的結果當對照值驗證 |
| 7 | `geography` 的 `STBuffer(公尺)` 可直接使用 | 500 m 圓的面積 785,083 m²（理論 785,398，誤差約 0.04%，因圓以多邊形近似） | 緩衝區可直接在 SQL 端以公尺計算，不需自行投影 |

**本版資料儲存決策**：基地台點位與行政區界一律存 `geography`（SRID 4326），`STDistance`／`STIntersects` 在 SQL 端執行（單位公尺、可用空間索引）；質心用「`geography` → WKB → `geometry` → `STCentroid()` → 轉回 `geography`」，緩衝區用 `geography.STBuffer`（公尺），兩者都在 SQL 端完成；應用層的 NetTopologySuite 只負責 GeoJSON 組裝，以及「4326 → 3826 投影後計算」的**對照基準值**（驗收用）。`ProjNet` 降為測試用途。把這個陷阱與處理方式寫進 README，是很好的面試話題。

### 1.4 部署形態的務實調整

- **Ollama 跑在 Windows 主機，不放進 Compose。** Docker Desktop 的 GPU 直通在 Windows 上設定成本高，且本機只有 4 GB VRAM，收益很小。App 容器經 `host.docker.internal:11434` 連主機的 Ollama。若要滿足「三容器」的敘事，可另提供 `--profile ollama` 的 CPU 版 Compose 選項（標註為示範用途）。
- **開發初期可直接用本機 SQL Express**（空間與 ledger 皆已驗證可用），到 M6 才切換為 Compose 內的 SQL Server 容器，避免一開始就卡在 Docker 安裝。
- 所有 Compose 與 CI 設定僅使用**合成資料**，不需任何私密資產。

### 1.5 模型選擇

| 軌道 | 建議 | 理由與保留 |
|---|---|---|
| 雲端主軌 | 選一家的「最便宜且具 tool calling 的等級」 | Demo 與評估數據用；**設定每月預算上限**；費用以官方定價頁為準。單次完整評估估算：60 題 × 平均 2 次呼叫 × 約 4k 輸入 token ≈ 0.5M 輸入 token，重複 3 次約 1.5M，通常在數美元以內，開跑前請依定價頁自行換算 |
| 本機副軌 | `qwen2.5:3b`（約 1.9 GB；coder 版 tool calling 失敗，見 §5 U2，可完整放進 4 GB VRAM）為起點；`qwen2.5-coder:7b` 需部分卸載到 CPU，速度慢 | 這類小模型在 NL2SQL 的準確率與穩定的 tool calling 都**無法事先保證**（見 §6-1）；定位為「證明架構可切換 + 誠實呈現落差」，不是主要賣點 |

---

## 2. 難易度分級

**等級**：L1 樣板／L2 標準工程／L3 需設計決策／L4 需反覆實驗、品質無法事先保證／L5 取決於外部、工程無法解決。**本版專案已無 L5**（原 v1 的 L5 是客戶資料與領域專家，現已由作者自行掌握）。

| # | 模組 | 等級 | 人日 | 備註 |
|---|---|---|---|---|
| 1 | 專案骨架、.NET 10、DI、設定、AF hello-agent、雙軌 `IChatClient` | L2 | 2 | 含 M0 驗證 |
| 2 | 合成資料集 + schema 設計 + 種子程式 + `ai` 視圖 | L3 | 3 | 用固定亂數種子產生；故意放幾個命名不佳的欄位，讓評估有鑑別度 |
| 3 | NL2SQL 外掛（schema 注入、few-shot、生成） | **L4** | 4 | 提示工程無上界；含 1 人日準確率 spike |
| 4 | SQL AST 驗證 + 唯讀 login + 逾時／列數上限 | L3 | 3 | 唯一能做到 100% 的模組 |
| 5 | 自我修正迴圈（有界重試 + 錯誤消毒） | L3 | 2 | |
| 6 | GIS Plugin（GeoJSON、質心、緩衝區、重投影） | L3 | 4 | 含 §1.3 的陷阱 |
| 7 | 輸入／輸出護欄（注入偵測、金絲雀 token、結果內容間接注入處理） | **L4** | 3 | 開放式；做成語料庫 + 數字 |
| 8 | PII 遮蔽視圖 + 角色切換（analyst／admin） | L3 | 2 | |
| 9 | 稽核（每次請求一筆）+ OpenTelemetry 基本指標 | L2 | 2 | 想加分再加 `LEDGER = ON` |
| 10 | 評估管線 + 60 題標準集 + 消融實驗（有／無自我修正、雲端／本機） | L3 | 5 | 履歷最有價值的產出 |
| 11 | ASP.NET Core MVC（Controller + Razor View + Leaflet 地圖） | L2 | 3 | 專案為 `GeoNl2Sql.Web` |
| 12 | Dockerfile、Compose、GitHub Actions CI | L2 | 3 | |
| 13 | README、架構圖、攻擊與評估結果表、Demo 影片 | L2 | 4 | |
| | **合計** | | **40 人日** | 約 320 小時：每週 15 小時約 21 週；每週 25 小時約 13 週 |

**可公開的最小版本（MVP）**：模組 1–5、11（簡版 2 日）、10（簡版 2 日）、13（簡版 2 日）≈ **20 人日**，完成即可打 `v0.1.0` 公開。

**與直覺相反之處**：框架整合（AF）是最簡單的部分；擋掉 `DROP` 其實可以做到 100%；真正耗時且無上界的是**提示工程（模組 3）與護欄（模組 7）**，以及不會被計入工時的「反覆調整與重跑評估」。

---

## 3. 風險係數與未知數

**風險係數 = 發生機率 P（1–5）× 衝擊 I（1–5）**。≥15 動工前就要有對策；9–14 要有減緩措施；≤8 追蹤即可。

| ID | 風險 | P | I | 係數 | 減緩後 | 減緩措施 |
|---|---|---|---|---|---|---|
| R1 | **範圍蔓延、專案做不完**（個人專案最常見的死法） | 4 | 4 | **16** | 8 | 里程碑制，每個里程碑獨立可公開；MVP 先上線；§5 的「不做清單」；每階段都更新 README |
| R2 | 公開儲存庫洩漏 API 金鑰／連線字串 | 3 | 4 | **12** | 2 | 金鑰只放 `dotnet user-secrets` 與環境變數；`.env` 進 `.gitignore`；啟用 GitHub secret scanning 與 push protection；M0 即加入 pre-commit 掃描；Demo 影片錄製前檢查畫面 |
| R3 | 本機小模型準確率或 tool calling 不可用 | 4 | 2 | **8** | 4 | 定位為副軌；準確率照實呈現；若 tool calling 不穩，改用「生成 JSON／純文字 SQL，由程式解析」的降級模式。**已於 M1 實測確認：準確率明顯低於雲端（12–14/30），tool calling 另於 M0 以 `qwen2.5:3b` 驗證可用，見 §7** |
| R4 | 評估數字不可信（自建資料自己出題，易高估） | 3 | 4 | **12** | 6 | 標準集在看到模型輸出**之前**凍結；公布題目與失敗案例；附消融實驗；明講資料為合成、結論不外推到真實企業 |
| R5 | 雲端模型費用超支 | 2 | 2 | 4 | 2 | 設月預算上限與用量告警；評估結果快取（以題目 + 模型 + 提示詞版本為鍵）；開發階段用小樣本 |
| R6 | AF 1.x 迭代快（半年 23 個版本），API 微調造成重工 | 3 | 3 | 9 | 5 | 鎖定版本號；把 AF 型別集中在 `Core` 專案的 Agent 資料夾；升級前先跑評估回歸 |
| R7 | Prompt injection 在 Demo 或面試被當場突破 | 3 | 4 | 12 | 5 | 以 DB 層確定性控制兜底（突破也拿不到資料、寫不進去）；README 放上攻擊語料庫與阻擋率，並誠實列出已知繞過 |
| R8 | 環境卡關：.NET 10 SDK、Docker／WSL2 未安裝（使用者確認 Docker Desktop 自行安裝） | 3 | 2 | 6 | 2 | M0 先裝好並驗證；Docker 卡關時先以本機 SQL Express 開發，M6 再處理 |
| R9 | 空間查詢慢（空間索引未被使用） | 2 | 2 | 4 | 2 | 合成資料量控制在萬級；用執行計畫確認索引，必要時加 `WITH INDEX()` 提示 |
| R10 | 引用的開放資料（行政區界）授權不明 | 2 | 3 | 6 | 2 | 只用明確授權的政府開放資料並在 README 標註來源；不然改用程式生成的假行政區多邊形 |

**未知數（需實測才能收斂）**

| ID | 未知數 | 如何收斂 |
|---|---|---|
| U1 | AF 1.23.0 套件實際支援的目標框架是否涵蓋 .NET 10（本次 NuGet TFM 查詢沒有回傳結果，**未驗證**） | M0 在 .NET 10 專案 `dotnet add package` 並跑 hello-agent |
| U2 | 經 `IChatClient` 呼叫 Ollama 小模型時，AF 的 function calling 是否穩定 | M0 以單一工具實測 20 次，記錄成功率 |
| U3 | 雲端模型選哪一家／哪一級最划算（準確率對價格） | M1 對 30 題小標準集比較 2–3 個候選 |
| U4 | Anthropic SDK 是否原生提供 `IChatClient` 轉接 | M0 查套件文件；若無，改用 OpenAI 相容端點或自寫薄轉接層 |
| U5 | 可用的開放行政區界資料格式與授權 | M1 前確認；備案為合成多邊形 |

---

## 4. 前置資源

### 4.1 本機現況（已實測）

| 項目 | 現況 | 對應 |
|---|---|---|
| CPU／RAM | Ryzen 7 7700（8C/16T）／31.1 GB | ✅ 足夠 |
| GPU | GTX 1650，4 GB VRAM | 只能跑 3B 級；7B 需 CPU 卸載 |
| .NET | 僅 SDK 9.0.306（另有 8.0 與 6.0 runtime） | **需安裝 .NET 10 SDK** |
| SQL Server | 2022 Express RTM 16.0.1000.6，服務執行中；空間與 ledger 實測可用 | ✅ 開發期夠用；建議順手套用最新 CU |
| Docker／WSL2／Ollama | 皆未安裝 | M0 安裝 |
| Git／sqlcmd | 2.51.1／16.0.1000.6 | ✅ 專案目錄尚未 `git init` |

### 4.2 待準備清單

| 類別 | 項目 | 成本 |
|---|---|---|
| 軟體 | .NET 10 SDK、Docker Desktop（含 WSL2）、Ollama、VS Code 或 Visual Studio | 免費 |
| 帳號 | 雲端模型 API 帳號 + 月預算上限 | 少量測試費 |
| 帳號 | GitHub 公開儲存庫（啟用 secret scanning、Actions） | 免費 |
| 資料 | 合成資料（建議 `Bogus` 套件、固定種子）+ 行政區界（開放資料或合成） | 免費 |
| 工具 | 錄影與剪輯軟體（OBS 等） | 免費 |

### 4.3 公開儲存庫檢查清單

- [ ] 儲存庫內**沒有**任何真實個資、真實企業資料或內部文件；PII 欄位全為合成資料，身分證字號用演算法產生的假值，並於 README 註明
- [ ] `.gitignore`：`.env`、`appsettings.*.local.json`、`*.bak`、`*.mdf`／`*.ldf`、`secrets.json`
- [ ] 提交前掃描金鑰（如 `gitleaks` 作為 pre-commit 或 CI 步驟）
- [ ] 評估結果檔（JSON）可公開；其中不得夾帶金鑰、完整系統提示詞中的內部資訊
- [ ] 授權條款：選一個授權（如 MIT）並標註第三方資料來源與授權

---

## 5. 驗證順序與驗收標準

**原則**：每個里程碑結束就有一個可公開的版本與一組數字；最便宜、最致命的先測；**先上線一個陽春版，再逐步加強**（符合 D5）。

### 里程碑總覽

```
M0 環境與 hello-agent (模組1)            ──► 公開空儲存庫 + README 骨架
 │
M1 資料與準確率 spike (模組2 + 3 的 1 日)  ──► 知道雲端／本機各能到多少
 │
M2 NL2SQL 核心 + 確定性安全邊界 (模組3–5) ──► v0.1.0（MVP 可公開）
 │
M3 GIS + 地圖前端 (模組6、11)             ──► Demo 影片素材
 │
M4 護欄、PII、稽核 (模組7–9)              ──► v0.2.0（資安章節與攻擊語料庫）
 │
M5 評估管線與數據 (模組10)                ──► README 的數字與圖表
 │
M6 容器化與 CI (模組12)                   ──► `docker compose up` 一鍵啟動
 │
M7 收尾：README、影片 (模組13)            ──► v1.0.0
```

### 各里程碑的驗收標準

**M0｜環境與 hello-agent（約 2 人日）**
- [x] .NET 10 SDK 安裝；`dotnet new` 建立專案，`Microsoft.Agents.AI` 1.23.0 可還原並建置（驗證 U1）
- [x] 一個含單一工具的 agent，**同一份程式碼**僅改設定即可分別呼叫雲端模型與本機 Ollama 模型（驗證 U2、U4）（本機軌與雲端軌皆已驗證）
- [x] 本機小模型 tool calling 20 次，記錄成功率；若 < 70%，採降級模式（見 R3）
- [x] 儲存庫已 `git init`、推上 GitHub，secret scanning 與 pre-commit 掃描已啟用

**M0 實測紀錄（2026-10-07）**
- U1：`Microsoft.Agents.AI` 1.23.0 在 .NET 10 與 .NET 9 皆可還原並建置，測試通過。
- U4：Anthropic SDK 12.53.0 提供 `AsIChatClient`，2026-10-08 以真實 API key 呼叫 `claude-haiku-4-5` 成功（`hello Anthropic claude-haiku-4-5`，回應內容正確、格式正常），U4 驗證完成。
- U2（本機軌）：`qwen2.5-coder:3b` 執行 hello-agent，模型沒有產生結構化 tool call，而是把工具呼叫以純文字印出（引數還多包一層，例如 `{"city": {"city": "Taipei"}}`），工具未被執行。分析與驗證：
  - 工具名稱 `_Main_g_GetWeather_0_0` 是 C# 區域函式的編譯器改名，屬本專案程式問題；已改為 `AIFunctionFactory.Create(GetWeather, name: "GetWeather")`。修正後 `qwen2.5-coder:3b` 連跑 3 次仍輸出純文字（名稱正確、引數仍巢狀），故名稱不是主因。
  - 換成非 coder 的 `qwen2.5:3b`（同為 1.9 GB 等級，同一份程式碼）：20 次中 20 次成功呼叫工具並回答，成功率 100%（以輸出含工具回傳值判定）。
  - 結論：本機軌改用 `qwen2.5:3b` 即可走原生 tool calling；`qwen2.5-coder:3b` 留待 M1 與 NL2SQL 準確率一併比較。R3 的純文字 SQL 降級模式仍保留為備案。此測試僅有單一簡單工具，不代表多工具與複雜引數下同樣穩定。
- 暫時性決定：因 Visual Studio 2022 17.14 無法載入 .NET 10 專案，目前專案暫降為 `net9.0`（`global.json` 鎖 9.0.306）；安裝 VS 2026 後再改回 `net10.0`。

**M1｜資料與準確率 spike（約 3–4 人日）★ 決定後續方向**
- [x] 合成資料可一鍵重建，筆數與 schema 固定（含 PII 欄位、空間欄位、幾個命名不佳的欄位）
- [x] 30 題小標準集（含單表、多表 JOIN、聚合、空間查詢各類）在**凍結後**才開始測（tag `m1-questions-frozen`）
- [x] 以約 165 行腳本（不用框架）量測單次生成準確率，雲端模型單次 **83.3%**（plain）／**100%**（described），一次修正後同為 **83.3%／100%**，兩者皆 ≥ 60%／≥ 75% 門檻
- [x] 本機小模型已量到數字（L1 12/30、L2 13/30、L3 14/30），不設及格線
- 雲端單次已達 60% 以上：**不需要**啟動 §6.3 的改善迴圈。詳見 §7「M1 實測紀錄」

**M2｜NL2SQL 核心 + 確定性安全邊界（約 9 人日）→ 打 `v0.1.0`**
- [ ] 端到端：自然語言 → SQL → 驗證 → 唯讀執行 → 回答，30 題準確率不低於 M1 基線
- [ ] **AST 驗證對 60 條攻擊 SQL（註解插入、大小寫變形、`CHAR()` 拼接、批次分隔、巢狀 CTE、`EXEC`、多語句）阻擋率 100%**
- [ ] 刻意關閉 AST 驗證後，唯讀 login 仍然無法寫入、無法 DDL、無法 `EXEC`（證明兩層獨立）
- [ ] 逾時與列數上限可由測試觸發；回饋給 LLM 的資料庫錯誤已去除表名／欄位名細節
- [ ] 自我修正上限 2 次，不會無限迴圈
- [ ] 單元測試於 CI 可離線執行（不需模型）

**M3｜GIS + 地圖前端（約 7 人日）**
- [ ] 輸出通過 RFC 7946 GeoJSON 結構驗證，可在 Leaflet 顯示
- [ ] 質心（geometry 轉換法）與 NetTopologySuite 投影後參考值誤差 < 0.1%；**程式中不得以 `EnvelopeCenter()` 冒充質心**
- [ ] 4326 ↔ 3826 往返誤差 < 0.5 公尺；緩衝區以公尺為單位且結果為有效幾何
- [ ] 空間查詢的執行計畫確認使用空間索引

**M4｜護欄、PII、稽核（約 7 人日）→ 打 `v0.2.0`**
- [ ] 攻擊語料庫 50 筆（直接注入、越獄、刺探 schema、要求輸出系統提示詞、**資料表內容中的間接注入**）；分別報告「護欄層阻擋率」與「端到端未洩漏率」
- [ ] 端到端要求：**未授權資料取得 0 次、寫入成功 0 次、完整 schema 洩漏 0 次**（確定性控制）；護欄層阻擋率照實公布，不設虛高門檻
- [ ] analyst 角色查詢敏感欄位在 **DB 層被拒絕**；抓取實際送往模型的 request，原始 PII 出現 0 筆（含自我修正的第 2、3 輪）
- [ ] 每次請求寫一筆稽核：使用者／角色、問題、生成的 SQL、token 用量、耗時、結果摘要、模型與提示詞版本；稽核寫入失敗時請求一併失敗

**M5｜評估管線與數據（約 5 人日）**
- [ ] 60 題標準集（含難度與類別標籤）；評分以**執行結果比對**為主，不只比對 SQL 文字
- [ ] 消融實驗：{雲端, 本機} × {有, 無自我修正}，每格重複 3 次並報告變異
- [ ] 結果以 JSON 存入儲存庫，README 以表格與圖呈現，並**列出代表性失敗案例與原因**
- [ ] 一鍵指令重現評估（模型呼叫結果可快取）

**M6｜容器化與 CI（約 3 人日）**
- [ ] `docker compose up` 後 App + SQL Server 可用，並自動建庫與灌入合成資料
- [ ] App 容器可連主機 Ollama；文件說明 GPU 直通的取捨
- [ ] GitHub Actions：建置、單元測試、AST 驗證攻擊集、離線護欄測試全部通過；需要真實模型的評估不放在 CI

**M7｜收尾（約 4 人日）→ 打 `v1.0.0`**
- [ ] README 含：架構圖、安全分層圖、評估結果表、攻擊結果表、已知限制、如何本機重現、費用說明
- [ ] Demo 影片 3–5 分鐘：正常查詢 → 地圖 → 角色切換遮蔽 → 一次注入被擋 → 一次自我修正救回 → **一次誠實展示的失敗**
- [ ] 錄影前檢查畫面無金鑰、無真實資料

### 不做清單（防止 R1）

ISO 控制項對應矩陣、外部紅隊、多租戶、負載測試到 50 併發、向量檢索 schema、微調模型、自製前端框架。這些可以在 v1.0.0 之後依興趣再加。

---

## 6. 評估後沒把握的三件事

**1. 本機 3B 級小模型在 NL2SQL 能到多少準確率，以及 AF 下 tool calling 是否穩定。**
**M0 已解決 tool calling 的不確定性**：`qwen2.5-coder:3b` 的 tool calling 失敗（純文字輸出，見 §5 U2），換成 `qwen2.5:3b`（同為 1.9 GB 等級）後 20 次全部成功，目前本機軌已固定使用 `qwen2.5:3b`。**NL2SQL 準確率已由 M1 實測取代推測**（見 §7）：三組本機結果落在 12–14/30（40–47%），遠低於雲端的 83–100%；直覺「3B 在多表 JOIN 與空間函式上會吃力」得到證實，且主因是語法／方言層級的錯誤（CTE 作用域、幻覺欄位名、T-SQL 方言、geography 方法呼叫語法），不是 schema 描述或題目設計的問題。本機軌的定位確定為「證明架構可切換＋誠實呈現落差」，不是準確率賣點。

**2. AF 1.x 在 .NET 10 上的實際 API 細節與套件相容性。**
我確認了套件與版本存在（`Microsoft.Agents.AI` 1.23.0、`.OpenAI` 1.23.0、`Microsoft.Extensions.AI` 10.10.0），也查到 AF 有三層中介層，但**沒有實際跑過**，而且目標框架查詢沒有拿到結果。我不確定的有：中介層攔截函式呼叫的確切寫法、Anthropic SDK 是否能直接當 `IChatClient`、AF 對 .NET 10 的支援是否有未預期的限制。網路上另有第三方護欄套件（如 AgentGuard）可用，但我傾向自己實作以便展示能力，這是取捨，不是已驗證的最佳解。這一項風險不高，但寫程式前應先做 M0。

**3. 評估數字的可信度，以及它對招募方的實際說服力。**
資料、題目、標準答案都由同一人（作者）產生，容易不自覺地把題目出得「模型答得對」，導致分數偏高。我提出的對策（先凍結題目、公布失敗案例、消融實驗、明講合成資料不外推）能降低風險，但**無法保證招募方如何看待這類數字**，也無法預測哪一種呈現（準確率表、攻擊語料庫、架構圖、影片）最能打動他們。這是對求職市場的判斷而非技術問題，我沒有可靠資料。建議做完 M2 就先公開，並向實際在業界的人要回饋，再決定後續投入哪一塊。

---

## 7. M1 實測紀錄（2026-10-08）

題集：`eval/GeoNl2Sql.Eval/Questions/questions.json`，SHA-256 `b7fac9c7085454716eefba5ac88e75d27bf4a18230846b2b4561623ee9bdf766`，凍結於 tag `m1-questions-frozen`。每組重複 3 輪，單次準確率三輪完全一致（溫度 0），故不另列範圍。

### 7.1 五組實驗結果

| 組 | 模型 | Schema | 單次正確 | 一次修正後 | single (8) | join (8) | aggregate (7) | spatial (7) |
|---|---|---|---|---|---|---|---|---|
| L1 | Ollama `qwen2.5:3b` | 無描述 | 12/30 (40%) | 12/30 (40%) | 2 | 5 | 3 | 2 |
| L2 | Ollama `qwen2.5:3b` | 有描述 | 13/30 (43%) | 13/30 (43%) | 4 | 5 | 3 | 1 |
| L3 | Ollama `qwen2.5-coder:3b` | 有描述 | 14/30 (47%) | 14/30 (47%) | 6 | 4 | 3 | 1 |
| C1 | `claude-haiku-4-5` | 無描述 | **25/30 (83.3%)** | 25/30 (83.3%) | 6 | 6 | 7 | 6 |
| C2 | `claude-haiku-4-5` | 有描述 | **30/30 (100%)** | 30/30 (100%) | 8 | 8 | 7 | 7 |

**對照 §5 的 A3 驗收**：C1（決定 A3 的主要數字）單次 83.3% ≥ 60%，一次修正後 83.3% ≥ 75%，**一次達標，M1 通過**；§6.2／§6.3 的改善迴圈（S6）**不需啟動**。

### 7.2 失敗類型分佈（三輪一致）

| 組 | ok | wrong_result | exec_error | model_error | precheck |
|---|---|---|---|---|---|
| L1（plain） | 12 | 6 | 11 | 1 | 0 |
| L2（described） | 13 | 7 | 9 | 0 | 1 |
| L3（described） | 14 | 7 | 8 | 0 | 1 |
| C1（plain） | 25 | 5 | 0 | 0 | 0 |
| C2（described） | 30 | 0 | 0 | 0 | 0 |

本機模型的失敗以 `exec_error`（SQL 執行階段錯誤）為主；雲端模型全程 **0 次 `exec_error`**，失敗只出現在 `wrong_result`（語意理解錯誤，SQL 能執行但結果不對）。這組對比直接回答 §6-1 的推測：3B 模型的瓶頸主要在語法／方言掌握，不是空間查詢或 schema 本身的問題。

### 7.3 失敗案例分析

**本機（L1，plain，取自 r3）**——代表性 `exec_error`，重試後幾乎不會被修正：

- **S05（CTE 作用域）**：CTE `EligibleSubscriptions` 未在外層正確暴露 `SubscriptionId`，模型卻以 `s.SubscriptionId` 參照外層，錯誤「無法繫結多重部分識別碼 "s.SubscriptionId"」；重試產生幾乎相同的錯誤 SQL。
- **A05（欄位名幻覺）**：schema 的實際欄位是 `dt2`，模型兩次（含重試）都寫成 `SUB.d2`，錯誤「無效的資料行名稱 'd2'」重複 4 次，從未被修正。
- **A04／G02／G05（T-SQL 方言）**：`OFFSET...FETCH` 語法用錯，例如「FETCH 陳述式中的選項 NEXT 使用方式無效」。
- **G04／G05（geography 語法）**：把 geography 方法當自由函式呼叫（如 `StArea(...)`、`STDistance(...)`），錯誤「不是可辨識的 內建函數名稱」，儘管 system prompt 已明確要求方法呼叫語法（`a.STDistance(b)`）。
- 10 個 `exec_error` 案例中，重試只讓 1 題（G02）換了失敗類型（exec_error → precheck，仍不正確），**沒有任何一題被重試真正修正**——對 3B 模型而言，單次修正機制幾乎無效，有時甚至重新生成逐字相同的錯誤 SQL（如 A05）。

**雲端（C1，plain）**——5 個 `wrong_result` 全部與**未描述的欄位語意**有關，而非能力不足：

- **S01**：DB 實際 `Status = 'Maintenance'`（英文列舉值），模型猜成中文「維護中」。
- **S04／J07／G07**：`flg1` 無描述，模型自行猜測「`flg1 = 1` 表示政府機關」；但標準答案是 `flg1 = 2` 才是政府機關、`flg1 = 1` 是企業客戶（見 J04 標準 SQL），猜錯方向。
- **J04**：模型把「企業客戶」直接理解成 `PlanName LIKE '%企業%'`，但完全漏掉 `flg1 = 1` 這個條件，導致把「訂閱企業方案的任何客戶」誤當成「企業客戶」。
- **G07**：另外把 `STIntersects` 誤用為 `STContains`，但主因仍是 `flg1` 猜錯。

C2（described）補上 `flg1`、`Status`、`dt2` 等欄位的語意說明後，這 5 題與其餘題目一樣**全部答對（30/30）**，證實雲端模型的唯一短板就是「命名不佳欄位缺描述」，而非 SQL 生成能力。

### 7.4 schema 描述消融效果

| 模型 | 無描述 | 有描述 | 差異 |
|---|---|---|---|
| 本機 `qwen2.5:3b`（L1→L2） | 12/30 | 13/30 | +1 題（+3.3pp） |
| 雲端 `claude-haiku-4-5`（C1→C2） | 25/30 | 30/30 | **+5 題（+16.7pp）** |

欄位說明對雲端模型的影響遠大於本機模型：本機模型的失敗以語法層級的 `exec_error` 為主，欄位說明無法修正語法錯誤，所以提升有限；雲端模型的語法已經穩定（0 次 `exec_error`），唯一短板正好是「命名不佳欄位的語意」，欄位說明剛好打中這個短板，所以收益顯著。這也是本專案刻意保留 `flg1`／`dt2` 這類命名不佳欄位的設計目的（§3.2）得到的驗證。

### 7.5 對 R3 與 §6-1 的回答

- **R3（本機小模型是否可用）**：可用於「證明架構可切換」的示範軌道，**不可用於生產等級的準確率**（40–47% 且自我修正機制對 `exec_error` 幾乎無效）。這是誠實呈現，不是技術缺陷——定位本就如此（§1.5）。
- **§6-1（3B 準確率推測）**：推測「3B 在多表 JOIN 與空間函式上會吃力」部分正確，但實測顯示更根本的瓶頸是**語法與方言層級**（CTE 作用域、欄位名幻覺、`OFFSET/FETCH`、geography 方法呼叫語法），這些錯誤分散在全部四個類別，不只集中在 JOIN 與空間題。

### 7.6 (a)(b)(c) 改善手段是否需要引入

討論中暫緩的三個改善手段（few-shot 範例、避免巢狀 CTE、更豐富的重試回饋）**不需要為了通過 M1 門檻而引入**——C1 單次 83.3% 已一次達標，§6.2 的改善迴圈只在 40–60% 區間才觸發。

若目的改為「提升本機 3B 模型的準確率」，這三個手段的預期效益有限，因為本機模型的主要失敗模式是語法／方言層級的 `exec_error`（§7.2、§7.3），而這三個手段主要處理的是語意理解與欄位猜測問題（對雲端模型的 `wrong_result` 較對症，對本機模型的 `exec_error` 較不對症）。本機軌的定位本來就是「證明可切換＋誠實呈現落差」而非準確率賣點（§1.5、R3），因此**不建議在 M1／M2 投入時間改善本機模型準確率**；若日後有興趣，屬於 v1.0.0 之後的可選加強項（不做清單之外的「依興趣再加」）。

---

## 附錄：資料來源與實測依據

- [.NET 8 and .NET 9 will reach end of support on November 10, 2026 — Microsoft Dev Blogs](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- [Migrate a Semantic Kernel App to Microsoft Agent Framework 1.0](https://startdebugging.net/2026/07/migrate-a-semantic-kernel-app-to-microsoft-agent-framework-1-0/)
- [Microsoft Agent Framework middleware（.NET）](https://www.mintlify.com/microsoft/agent-framework/dotnet/middleware)
- [Introducing AgentGuard — declarative guardrails for .NET AI agents](https://strathweb.com/2026/03/introducing-agentguard-declarative-guardrails-for-dotnet-ai-agents)
- [Experimenting with Self-Hosted LLMs for Text-to-SQL — nilenso](https://blog.nilenso.com/blog/2025/05/27/experimenting-with-self-hosted-llms-for-text-to-sql/)
- [Best Ollama models for 4GB VRAM](https://localaimaster.com/vram/best-ollama-models-4gb-vram)
- [BEAVER: An Enterprise Benchmark for Text-to-SQL](https://arxiv.org/html/2409.02038v3)
- [Text-to-SQL Benchmarks are Broken: Annotation Errors — CIDR 2026](https://www.vldb.org/cidrdb/papers/2026/p5-jin.pdf)
- NuGet 版本查核（2026-10-07）：`Microsoft.Agents.AI` 1.23.0、`Microsoft.Agents.AI.OpenAI` 1.23.0、`Microsoft.Extensions.AI` 10.10.0、`Microsoft.Extensions.AI.OpenAI` 10.10.1、`OllamaSharp` 5.5.0、`Anthropic` 12.53.0、`Microsoft.SqlServer.TransactSql.ScriptDom` 180.117.0、`NetTopologySuite` 2.6.0、`NetTopologySuite.IO.GeoJSON` 4.0.0、`ProjNet` 2.1.0、`Microsoft.Data.SqlClient` 7.1.1（**目標框架相容性未驗證**）
- 本機實測：SQL Server 2022 Express 16.0.1000.6（錯誤 6506、24204；`LEDGER = ON` 可用）；硬體與已安裝軟體清單
