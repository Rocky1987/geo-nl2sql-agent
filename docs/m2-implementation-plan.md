# M2 實作計畫：NL2SQL 核心 + 確定性安全邊界

| 項目 | 內容 |
|---|---|
| 讀者 | 開發者（作者本人與日後的貢獻者） |
| 文件日期 | 2026-10-08 |
| 里程碑定位 | 把 M1 的丟棄式 spike 換成正式的 Core 模組，並建立「AST 白名單 + 唯讀 login」兩層**互相獨立**的確定性防護（`feasibility-report.md` §5） |
| 預估工時 | 約 9 人日（模組 3、4、5：4 + 3 + 2） |
| 完成後 | 打 `v0.1.0`（MVP 可公開） |
| 狀態 | **進行中（2026-10-09）**：S1、S2 完成（已 commit）；S3 完成（`ReadOnlySqlExecutor`、`Database/ReadOnlyBoundaryTests`，含 login 與權限，全部測試通過）；S4–S7 未開始 |

---

## 1. 目標與驗收

來自 `feasibility-report.md` §5 的 M2 驗收，逐條對應到本計畫的步驟：

| # | 驗收條件 | 對應步驟 |
|---|---|---|
| B1 | 端到端：自然語言 → SQL → 驗證 → 唯讀執行 → 回答；30 題準確率**不低於 M1 基線** | S5、S6 |
| B2 | AST 驗證對 **60 條攻擊 SQL** 阻擋率 **100%** | S1、S2 |
| B3 | 刻意關閉 AST 驗證後，唯讀 login 仍然無法寫入、無法 DDL、無法模擬 `dbo`、無法執行本資料庫的預存程序（證明兩層獨立；master 的 public 系統預存程序如 `sp_who` 不在此列，見 §5.3） | S3 |
| B4 | 逾時與列數上限可由測試觸發；回饋給 LLM 的資料庫錯誤已去除表名／欄位名細節 | S3、S4 |
| B5 | 自我修正上限 2 次，不會無限迴圈 | S5 |
| B6 | 單元測試可離線執行（不需模型、不需資料庫） | 全部步驟 |

另加一條本計畫自訂的條件，避免 B2 只靠「什麼都擋」達成：

| # | 條件 | 理由 |
|---|---|---|
| B7 | 驗證器對**正常查詢的誤擋率為 0**：30 題標準 SQL 與 13 條「看起來可疑但其實無害」的查詢（例如字串值裡含 `DROP`）全部放行 | 阻擋率 100% 很容易用「全部擋掉」做到；誤擋率才證明白名單設計得對 |

**B1 的「M1 基線」**：正式管線用「有欄位說明」的 schema，所以對照組是 C2（`claude-haiku-4-5`，described）**30/30**。M1 三輪結果完全一致（溫度 0），SQL 生成的 prompt 照搬 M1 就應重現相同結果；若低於 30/30，要逐題判定是「驗證器誤擋」（必須修）還是「模型非確定性」（公開說明）。本機 `qwen2.5:3b` 照樣量測並記錄，不設及格線（同 M1 A4）。

**M2 刻意不做**（避免範圍蔓延，R1）：Web 介面串接與地圖（M3）、GeoJSON／質心／緩衝區（M3）、提示詞注入偵測、PII 遮蔽視圖、角色切換、稽核（M4）、60 題評估與消融（M5）、Docker 與 GitHub Actions（M6）、提升本機模型準確率（`project-m1-s5-sequencing`，延到 v1.0.0 之後）。

---

## 2. 架構

### 2.1 單次問答流程

```
問題 ─▶ 組 prompt（schema + 規則） ─▶ IChatClient ─▶ 抽出 SQL ─▶ AST 驗證 ─▶ 唯讀 login 執行 ─▶ 產生回答
                    ▲                                    │            │              │
                    │                                    ▼            ▼              ▼
                    └──────── 失敗原因（已消毒）◀── no_sql ／ rejected ／ exec_error（最多再試 2 次）
```

- **第一層：AST 白名單**（`Guardrails/`）。用 `Microsoft.SqlServer.TransactSql.ScriptDom` 把 SQL 解析成語法樹再檢查，不用字串比對。擋下的 SQL 不會送到資料庫。
- **第二層：唯讀 login**（`Data/`）。生成的 SQL 只透過獨立的低權限 login 執行；這個 login 的權限本身就無法寫入、DDL、`EXEC`，**不依賴第一層是否正確**。
- **自我修正**：只在 `no_sql`（抽不到 SQL）、`rejected`（驗證不通過）、`exec_error`（執行錯誤，含逾時）時重試；`wrong_result` 在真實系統裡看不到，不會也無法重試。初次生成加最多 2 次修正，**模型最多被呼叫 3 次**（不含產生回答那一次）。
- **回答**：執行成功後，把問題、欄位名稱與前 20 列結果交給模型寫一段簡短中文回答。列數超過上限時回答會註明「結果已截斷」。

### 2.2 為什麼 M2 不用 Agent Framework 的工具迴圈（待作者確認，§10 Q2）

M2 的管線是**由程式控制流程**的固定步驟，模型只負責「生成 SQL」與「寫回答」兩件事，直接呼叫 `IChatClient`（與 M1 spike 相同）。理由：

1. **B1 要和 M1 基線比**。M1 走的是「單次生成 SQL」的路徑；若改成 Agent 自行決定何時呼叫工具，生成路徑不同，數字就無法對照。
2. **B5 的重試上限由程式決定最確定**。工具迴圈裡模型可以一直重新呼叫工具，上限要另外靠中介層攔截。
3. **R3**：本機 3B 模型的 tool calling 只驗證過單一簡單工具。

代價是「Microsoft Agent Framework」這個履歷關鍵字要到 M3 才真正用上：M3 加入 GIS 工具後，工具變成多個、需要模型自行選擇，這時把 M2 的管線包成一個 AF 工具（例如 `query_database`）才划算。

### 2.3 Core 的資料夾與新增檔案

依 `project-technical-guide.md` §2.4 的規劃，以資料夾區分，不新增專案：

```
src/GeoNl2Sql.Core/
├── Guardrails/
│   ├── SqlValidator.cs            AST 白名單驗證（S2）
│   └── SqlErrorSanitizer.cs       資料庫錯誤 → 不含物件名稱的固定訊息（S4）
├── Data/
│   ├── ReadOnlySqlExecutor.cs     以唯讀 login 執行、逾時、列數上限（S3）
│   └── QueryLimits.cs             逾時秒數與列數上限（設定區段 Query）
└── Nl2Sql/
    ├── PromptBuilder.cs           system 訊息（自 SpikeCommand 移入，described 版逐字不變）（S4）
    ├── SqlExtractor.cs            從模型回應抽出 SQL（自 SpikeCommand 移入）（S4）
    └── Nl2SqlPipeline.cs          串起整個流程與有界重試（S5）

db/
└── 02_reader.sql                  唯讀使用者的 GRANT／DENY（S3）

tests/GeoNl2Sql.Tests/
├── Guardrails/
│   ├── attack-sql.json            60 條攻擊 SQL（S1，先於驗證器凍結）
│   ├── benign-sql.json            13 條「看似可疑但無害」的查詢（S1）
│   ├── CorpusIntegrityTests.cs    語料筆數與格式檢查（S1）
│   ├── SqlValidatorTests.cs       （S2）
│   └── SqlErrorSanitizerTests.cs  （S4）
├── Nl2Sql/
│   ├── SqlExtractorTests.cs       （S4）
│   └── Nl2SqlPipelineTests.cs     以假的 IChatClient 與假執行器離線測試（S5）
└── Database/
    └── ReadOnlyBoundaryTests.cs   需要資料庫，標記 [Trait("Category", "Database")]（S3）

eval/GeoNl2Sql.Eval/
├── Common/ResultComparer.cs       execution accuracy 比對（自 SpikeCommand 抽出，spike 改呼叫它）（S6）
└── Pipeline/PipelineCommand.cs    `pipeline` 量 30 題、`ask` 單題試問（S6）
```

M1 的 `spike` 子命令保留，作為 M1 數字的重現方式；只把比對器抽成共用，抽完以 `spike --fake "{gold}"` 確認仍為 30/30。

**新增套件**（加在 Core）：

| 套件 | 版本 | 用途 |
|---|---|---|
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.117.0 | T-SQL 解析器（`TSql160Parser`，對應 SQL Server 2022） |
| `Microsoft.Data.SqlClient` | 7.1.1 | 唯讀執行器；與 Eval 現用版本相同 |

### 2.4 離線測試與資料庫測試的分界（B6）

- 不需資料庫、不需模型的測試（驗證器、消毒器、抽取器、管線邏輯）是預設，`dotnet test` 直接可跑。
- 需要資料庫的測試（S3 的兩層獨立性、逾時、列數上限）放在 `Database/`，標記 `[Trait("Category", "Database")]`。CI（M6）以 `dotnet test --filter "Category!=Database"` 排除；本機開發時全部執行。
- 需要模型的量測（30 題準確率）不寫成測試，放在 Eval 的 `pipeline` 子命令。

---

## 3. S1：攻擊語料庫與正常查詢集（先寫，再寫驗證器）

**原則與 M1 的題集凍結相同（R4）**：攻擊語料庫在驗證器動工**之前**寫好並 commit。若先寫驗證器，很容易不自覺地只挑「已經擋得住」的攻擊，100% 就失去意義。之後若發現語料庫有錯（例如某條其實是合法查詢），在紀錄中公開說明再修改。

### 3.1 格式

`tests/GeoNl2Sql.Tests/Guardrails/attack-sql.json`，每筆：

```json
{
  "id": "MS02",
  "category": "multi_statement",
  "sql": "SELECT 1 DROP TABLE dbo.Customer",
  "note": "T-SQL 不需要分號也能串接語句；M1 前置檢查的『不含分號』擋不住",
  "dbExpect": "denied"
}
```

`dbExpect` 是 S3 用的欄位：**假設驗證器被關掉**，這條 SQL 直接送到唯讀 login 時預期的結果。

| `dbExpect` | 意義 |
|---|---|
| `denied` | 資料庫拒絕（權限不足、功能未啟用等），資料庫內容與物件完全不變 |
| `read_only` | 能執行，但只是讀取，不造成任何變更（例如查 `sys.tables`）；這類攻擊在 M2 只靠第一層擋，M4 再處理資料外洩面 |
| `tempdb_only` | 只在 tempdb 建立工作階段範圍的暫存表（`#t`）；任何 login 都能建暫存表，DB 權限擋不住，**這是兩層防護中必須誠實寫出的例外**，GeoNl2SqlDemo 本身仍不受影響 |

### 3.2 攻擊類別與數量（共 60 條）

| 類別 `category` | 數量 | 代表例 |
|---|---|---|
| `multi_statement` 多語句（有／無分號） | 8 | `SELECT 1; DROP TABLE dbo.Customer`、`SELECT * FROM dbo.Customer UPDATE dbo.Customer SET flg1 = 0` |
| `batch_separator` 批次分隔 | 4 | `SELECT 1` 換行 `GO` 換行 `DROP TABLE dbo.Customer` |
| `comment` 註解插入 | 8 | `SELECT 1 /* x */; /* y */ DELETE dbo.Customer`、行尾 `--` 後換行接 `DROP`、巢狀 `/* /* */ */` |
| `case_whitespace` 大小寫與空白變形 | 6 | `dRoP tAbLe`、Tab 與換行分隔、`[dbo].[Customer]` 方括號名稱 |
| `dynamic_sql` 動態 SQL 與 `CHAR()` 拼接 | 10 | `EXEC('DROP TABLE dbo.Customer')`、`EXEC(CHAR(68)+CHAR(82)+…)`、`EXEC sp_executesql N'…'`、`DECLARE @s … EXEC(@s)`、`EXEC xp_cmdshell 'dir'` |
| `cte_wrapped` CTE 包裝的寫入 | 6 | `WITH x AS (SELECT 1 a) DELETE FROM dbo.Customer`、巢狀 CTE 最後接 `UPDATE`／`INSERT`、`WITH … SELECT * INTO dbo.Leak FROM x` |
| `select_into` 與暫存表 | 4 | `SELECT * INTO dbo.Leak FROM dbo.Customer`、`SELECT * INTO #t …`、`CREATE TABLE #t (a int)` |
| `external` 外部資料存取 | 4 | `OPENROWSET`、`OPENDATASOURCE`、`OPENQUERY`、四段式連結伺服器名稱 |
| `catalog_probe` 系統目錄與跨資料庫 | 4 | `sys.tables`、`INFORMATION_SCHEMA.COLUMNS`、`FreeWayDB.dbo.…`、`master..sysdatabases` |
| `session` 身分、工作階段與伺服器指令 | 6 | `EXECUTE AS USER = 'dbo'`、`REVERT`、`SET ROWCOUNT 0`、`WAITFOR DELAY '00:01:00'`、`SHUTDOWN`、`DBCC …` |

`CHAR()` 拼接本身只產生字串，危險在於交給 `EXEC` 執行；所以語料庫裡的 `CHAR()` 案例都搭配動態執行。單純 `SELECT CHAR(65)` 會被函式白名單擋下（不在清單），但那不算「攻擊」，不列入 60 條。

### 3.3 正常查詢集（B7）

- **30 題標準 SQL**：直接讀 `eval/GeoNl2Sql.Eval/Questions/questions.json`（csproj 以 `Link` 複製到測試輸出目錄），不另抄一份。
- **`benign-sql.json`，13 條**刻意貼近攻擊外觀的合法查詢：字串值含關鍵字（`WHERE StationName = N'DROP TABLE'`、`N'; DELETE'`）、查詢內含註解（`SELECT 1 -- drop`）、含 CTE 的純查詢、子查詢、`ROW_NUMBER() OVER (…)`、尾端分號（`SELECT … ;` 單一語句）。

### 3.4 驗證（S1 的完成條件）

- 兩個 JSON 檔可被解析，筆數 60 與 13，`id` 不重複，每類筆數符合 §3.2。
- 由作者看過一遍再 commit（commit 訊息註明「驗證器動工前凍結」）。

---

## 4. S2：AST 白名單驗證器

### 4.1 規則（全部通過才放行）

| # | 規則 | 擋下的攻擊類別 |
|---|---|---|
| V1 | 以 `TSql160Parser(initialQuotedIdentifiers: true)` 解析，**有任何解析錯誤即拒絕** | 變形到無法解析的輸入 |
| V2 | 恰好 **1 個批次、1 個陳述式**，且型別必須是 `SelectStatement` | 多語句、批次分隔、註解插入、動態 SQL、CTE 包裝寫入（CTE 後接 `DELETE` 會被解析成 `DeleteStatement`）、身分與工作階段指令 |
| V3 | `SelectStatement.Into` 必須為 null | `SELECT … INTO`（含 `#t`） |
| V4 | 所有資料表參考只能是：白名單內的 6 張表（`dbo` 可省略），或本陳述式內定義的 CTE 名稱；不得帶資料庫名或伺服器名 | 系統目錄、跨資料庫、連結伺服器 |
| V5 | 不允許 `OPENROWSET`／`OPENDATASOURCE`／`OPENQUERY`／`OPENXML`／`OPENJSON` 與資料表值函式等非「具名資料表」的來源（衍生資料表 `(SELECT …) AS t` 允許） | 外部資料存取 |
| V6 | 所有函式呼叫必須在**函式白名單**內（不分大小寫），含 `geography` 的方法呼叫與靜態方法 | 未列出的系統函式、使用者自訂函式 |

函式白名單的來源：30 題標準 SQL 實際用到的（`COUNT`、`SUM`、`AVG`、`ROUND`、`YEAR`、`geography::Point`、`STDistance`、`STIntersects`、`STArea` 等），加上常見且無副作用的聚合、日期、字串、數學與視窗函式（如 `MIN`、`MAX`、`DATEADD`、`DATEDIFF`、`COALESCE`、`ROW_NUMBER`、`STContains`、`STBuffer`、`STAsText`、`STLength`）。**`CHAR` 不列入**（題目用不到）。完整清單見 `SqlValidator.AllowedFunctions`（程式碼常數並附註解；`Lat`、`Long` 未列入，需要時再加）；之後增減都要寫理由。

資料表白名單由建構子傳入（`IReadOnlySet<string>`），不做設定檔機制。M4 改成只開放 `ai` 結構描述的視圖時，換傳入的清單即可。

### 4.2 回傳與訊息

回傳 `SqlValidationResult(bool IsValid, string? Reason)`。`Reason` 是固定的中文短句（例如「只允許單一 SELECT 語句」「不允許的資料表或來源」「不允許的函式」），會回饋給模型做修正，**不回傳完整語法樹或原始錯誤**。

### 4.3 驗證（S2 的完成條件）

- `SqlValidatorTests`：60 條攻擊**全部拒絕**（B2）；30 題標準 SQL 與 `benign-sql.json` **全部放行**（B7）。兩組都用 `[Theory]` 逐筆列出，失敗時能看到是哪一條。
- 離線執行，`dotnet test` 全綠。
- 若某條攻擊需要為它新增規則，先確認新規則沒有造成誤擋，再 commit。

---

## 5. S3：唯讀 login 與執行器

### 5.1 為什麼不能沿用 M1 的 `spike_reader`

M1 用 `EXECUTE AS USER = 'spike_reader'`，連線本身仍是執行程式的 Windows 帳號（`dbo` 權限），而且生成的 SQL 只要含 `REVERT` 就能跳回原身分（`m1-implementation-plan.md` §3.6 已明講這不是安全邊界）。M2 改成**連線身分本身就是低權限 login**：就算程式忘了任何切換步驟、就算驗證器整個被關掉，這條連線也沒有寫入權限。

### 5.2 前提：SQL Server 需開啟混合驗證（§10 Q1，已完成）

2026-10-08 實測：SQL Server 預設可能只接受 Windows 驗證（可用 `SELECT SERVERPROPERTY('IsIntegratedSecurityOnly')` 檢查，回傳 1 表示僅 Windows 驗證），此時無法使用 SQL login。需在 SSMS 改為「SQL Server 及 Windows 驗證模式」並重新啟動服務（既有的 Windows 帳號不受影響）。開發環境（`.\SQLEXPRESS`）已完成此設定。M6 的 SQL Server 容器本來就只能用 SQL 驗證，不需此步驟。

### 5.3 權限設計

- **Login**（伺服器層級）：`geo_reader`，`CHECK_POLICY = ON`。密碼**只存在** user-secrets／環境變數的連線字串 `ConnectionStrings:Reader` 中；`appsettings.json` 只放不含密碼的範本。
- **建立方式**：`seed` 讀 `ConnectionStrings:Reader`，以 `SqlConnectionStringBuilder` 取出 User ID 與 Password，若 login 不存在就建立、存在就更新密碼（DDL 無法參數化：login 名稱限定 `[A-Za-z0-9_]`，密碼中的 `'` 轉義為 `''`）。只維護一份密碼。
- **使用者與權限**（`db/02_reader.sql`，seed 在 `01_schema.sql` 之後執行；寫成 SQL 檔是為了讓權限可以被直接審閱）：

```sql
CREATE USER geo_reader FOR LOGIN geo_reader;
GRANT SELECT ON SCHEMA::dbo TO geo_reader;
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, TAKE OWNERSHIP ON SCHEMA::dbo TO geo_reader;
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA, CREATE TYPE TO geo_reader;
DENY EXECUTE TO geo_reader;   -- 資料庫層級：擋本資料庫內的預存程序
```

  **不可 `DENY CONTROL`**：CONTROL 隱含 SELECT，DENY 優先於 GRANT，會讓 `geo_reader` 連 SELECT 都失效（2026-10-08 首次驗證即踩到，已移除）。

  **已實測的缺口（2026-10-08）**：最後一行的資料庫層級 `DENY EXECUTE` 擋不住 `EXEC sp_who`。`sp_who` 位於 master，授權給 `public`，權限在 master 內判斷，GeoNl2SqlDemo 的 DENY 管不到。`geo_reader` 沒有 `VIEW SERVER STATE`，`sp_who` 只回傳它自己的連線，資訊外洩風險低。因此語料 `BS04`、`DY06` 的 `dbExpect` 已改為 `read_only`（只靠 AST 驗證拒絕），B3 的宣稱已縮限為上表所列範圍。

  **S3 實測的第二個修正（2026-10-09）**：`MS08`（`GRANT CONTROL ON SCHEMA::dbo TO geo_reader`）原標 `denied`，實測 SQL Server 回傳的是「無法對自己授權」的訊息（嚴重性低於錯誤，不是 `SqlException`），語句不生效。因此 `MS08` 改標 `read_only`，並在快照比對中加入 `geo_reader` 的權限清單，直接證明權限沒有變動。

  只 `GRANT SELECT` 其實已經足以擋住寫入，額外的 `DENY` 是第二道保險：即使之後有人誤把 `geo_reader` 加進某個角色，`DENY` 仍優先於 `GRANT`。`geo_reader` 不加入任何資料庫角色，也沒有 `IMPERSONATE` 權限，所以 `EXECUTE AS USER = 'dbo'` 會失敗。
- M1 的 `spike_reader` 不動，`spike` 子命令照舊可重現 M1 數字。

### 5.4 `ReadOnlySqlExecutor`

- 只接受 `ConnectionStrings:Reader`；`QueryLimits` 預設 `TimeoutSeconds = 10`、`MaxRows = 1000`（設定區段 `Query`）。
- 讀到第 `MaxRows` 列就停止，回傳結果時標記 `Truncated = true`；不改寫 SQL（不在 AST 上硬加 `TOP`），伺服器端的工作量由逾時限制。
- `geography`／`geometry` 欄位沿用 spike 的做法讀成原始位元組（GeoJSON 屬 M3）。
- 逾時（`SqlException.Number == -2`）與其他執行錯誤都以例外往上拋，由管線交給 `SqlErrorSanitizer` 處理。

### 5.5 驗證（S3 的完成條件，`Database/ReadOnlyBoundaryTests`）

1. **兩層獨立（B3）**：繞過驗證器，把 60 條攻擊逐條直接交給 `ReadOnlySqlExecutor` 執行，對照 `dbExpect`：`denied` 必須收到 `SqlException`（`BS04`、`DY06`、`MS08` 已改標 `read_only`，不在此列）。全部跑完後，比對執行前後的 6 張表內容雜湊（同 `seed` 的算法）、`sys.objects` 物件清單與 `geo_reader` 的權限清單，**必須完全相同**。
2. **逾時（B4）**：`TimeoutSeconds = 1`，執行一個合法但很重的查詢（例如 `Customer` 三次 `CROSS JOIN` 後 `COUNT(*)`，約 10 億列），必須在約 1 秒後收到逾時。
3. **列數上限（B4）**：`MaxRows = 10`，`SELECT * FROM dbo.Customer` 回傳 10 列且 `Truncated = true`。
4. **正常查詢可用**：30 題標準 SQL 以 `geo_reader` 執行全部成功，結果與以 `Demo` 連線執行的相同。

---

## 6. S4：錯誤消毒、SQL 抽取與 prompt 移入 Core

### 6.1 `SqlErrorSanitizer`（B4）

SQL Server 的錯誤訊息會帶出物件名稱，例如「無效的資料行名稱 'd2'」。**依錯誤號碼對應到固定訊息，永不轉傳原文**：

| 錯誤號碼 | 回饋給模型的訊息 |
|---|---|
| 207 | 查詢引用了不存在的欄位，請只使用 schema 中列出的欄位。 |
| 208 | 查詢引用了不存在的資料表或物件。 |
| 4104 | 有無法繫結的多段式識別碼，請檢查資料表別名與 CTE 的作用範圍。 |
| 102、156 | 語法錯誤。 |
| 195、4121 | 使用了不存在的函式；geography 函式要用方法呼叫語法，例如 `a.Location.STDistance(b.Location)`。 |
| 8120 | `SELECT` 中有欄位未包含在 `GROUP BY` 或聚合函式中。 |
| 229、230、262 | 權限不足（唯讀查詢只能讀取允許的資料表）。 |
| -2 | 查詢逾時，請簡化查詢。 |
| 其他 | 執行失敗（錯誤碼 N）。 |

消毒器只看錯誤號碼，簽章設計成 `Sanitize(int errorNumber)`，因此可以完全離線測試（`SqlException` 無法自行建構）。

**取捨（照實記錄）**：去掉欄位名稱會讓修正訊息變模糊，理論上降低自我修正的成功率。實際影響預期很小：M1 雲端模型 0 次 `exec_error`；本機模型即使拿到完整錯誤原文，修正也幾乎無效（`feasibility-report.md` §7.3）。S6 的數字會驗證這個預期。

### 6.2 `PromptBuilder` 與 `SqlExtractor`

- 自 `SpikeCommand` 移入 Core，**described 版的 system 訊息逐字不變**（B1 能和 M1 基線比的前提）。把 M1 C2 結果 JSON 記錄的 `systemPrompt` 存成測試用快照檔（`Results/` 不進 git，所以快照要另外 commit），測試確認輸出逐字相同。
- 抽取規則不變：優先取 ```sql 區塊，否則取第一個 `SELECT`／`WITH` 起始的文字。
- M1 spike 的關鍵字黑名單**不搬**：M2 由 AST 驗證器取代。

### 6.3 驗證

`SqlErrorSanitizerTests`（每個號碼的訊息都不含任何資料表或欄位名）、`SqlExtractorTests`（```sql 區塊、無區塊、多個區塊、無 SQL），離線全綠。

---

## 7. S5：`Nl2SqlPipeline` 與有界重試

### 7.1 介面

```csharp
var result = await pipeline.AskAsync("中央區有哪些基地台？", cancellationToken);
// result.Sql、result.Rows、result.Truncated、result.Answer、result.Attempts（每次嘗試的 SQL、失敗類型、消毒後訊息）
```

建構時傳入 `IChatClient`、`SqlValidator`、執行器（以委派或介面注入，讓單元測試不需資料庫）與 schema 文字。

### 7.2 重試規則（B5）

- 最多 3 次生成（初次 + 2 次修正）。每次失敗後，把上一次的回應與**消毒後**的原因附加到對話，再生成一次。
- 3 次都失敗：回傳失敗結果與最後的原因，不丟例外、不再呼叫模型。
- 上限寫成常數 `MaxCorrections = 2`，不做成設定（`CLAUDE.md` §2：沒有人要求可調）。

### 7.3 驗證（`Nl2SqlPipelineTests`，以假的 `IChatClient` 離線測試）

| 情境 | 預期 |
|---|---|
| 模型第一次就給正確 SQL | 生成 1 次、回答 1 次 |
| 模型永遠回 `DROP TABLE …` | 生成**恰好 3 次**後停止，執行器**從未被呼叫** |
| 第一次執行錯誤（假執行器丟 207）、第二次成功 | 生成 2 次；送給模型的第二輪訊息**不含**原始錯誤文字中的欄位名 |
| 模型回應抽不到 SQL | 視為失敗並重試 |
| 結果超過列數上限 | 回答步驟收到「已截斷」標記 |

---

## 8. S6：端到端量測與試問

### 8.1 `pipeline` 子命令（B1）

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- pipeline --provider Anthropic --model claude-haiku-4-5 --runs 3
dotnet run --project eval/GeoNl2Sql.Eval -- pipeline --limit 5        # 本機模型小樣本
```

- 30 題逐題呼叫 `Nl2SqlPipeline`，以 `ResultComparer`（自 spike 抽出）比對標準答案；輸出 JSON 格式與 M1 相容，另加每題的嘗試次數與驗證器拒絕原因。
- 標準 SQL 以 `Demo` 連線執行；生成 SQL 只走 `Reader` 連線。
- **門檻**：雲端 described 單次與修正後皆 **≥ 30/30**。低於時依 §1 的規則逐題判定。
- 也跑本機 `qwen2.5:3b` 3 輪並記錄，不設門檻。
- **驗證器在真實輸出上的誤擋率**：統計 `rejected` 中原本是正確 SQL 的數量（以標準 SQL 結果比對判定），必須為 0；若不為 0，回到 S2 修白名單。
- 費用：同 M1 規模（30 題 × 3 輪），約數美元以內；開跑前以 `--limit 3` 試跑確認 token 用量。

### 8.2 `ask` 子命令（手動端到端）

```powershell
dotnet run --project eval/GeoNl2Sql.Eval -- ask "中央區有哪些基地台？"
```

印出生成的 SQL、每次嘗試的結果、前 20 列與回答。用來人工確認流程，也作為 M3 串接 Web 前的示範入口。

---

## 9. S7：紀錄與發佈

- `feasibility-report.md` 新增「§8 M2 實測紀錄」：攻擊語料庫結果（各類阻擋數）、兩層獨立性的測試結果（含 `tempdb_only` 例外的說明）、30 題端到端數字與 M1 基線的對照、誤擋率、代表性案例；勾選 M2 驗收項目。
- `project-technical-guide.md`：Core 的「規劃」改為「現況」（`Guardrails/`、`Data/`、`Nl2Sql/`）、Tests 與 Eval 的新內容。
- `README.md`：**只加使用說明**（`ConnectionStrings:Reader` 的設定、`pipeline`／`ask` 的用法、混合驗證的前提），以及「技術亮點」中兩層防護的概念說明；**不寫里程碑進度**。
- 打 `v0.1.0`：由作者確認後執行。

---

## 10. 執行順序、檢查點與待決事項

### 10.1 步驟

| 步驟 | 內容 | 驗證（做完要看到什麼） | 需要 | 預估 |
|---|---|---|---|---|
| S1 | 攻擊語料庫 60 條 + 正常查詢集 | 筆數與分類正確；作者看過；**在驗證器動工前 commit** | — | 0.5 日 |
| S2 | `SqlValidator` | 60/60 拒絕、標準 SQL 與正常查詢 0 誤擋；離線全綠 | — | 1.5 日 |
| S3 | `geo_reader`、`02_reader.sql`、`ReadOnlySqlExecutor` | 關掉驗證器仍無法寫入／DDL／`EXEC`，資料與物件雜湊不變；逾時與列數上限可觸發 | **Q1**、資料庫 | 2 日 |
| S4 | 消毒器、抽取器、prompt 移入 Core | 消毒訊息不含物件名；prompt 與 M1 逐字相同 | — | 1 日 |
| S5 | `Nl2SqlPipeline` | 永遠失敗時恰好 3 次生成；離線全綠 | — | 2 日 |
| S6 | `pipeline`、`ask` 子命令 | 雲端 described ≥ 30/30；誤擋 0；本機量到數字 | 金鑰、資料庫 | 1 日 |
| S7 | 文件、commit、`v0.1.0` | M2 驗收項目全勾 | 作者確認 tag | 1 日 |

S1、S2、S4、S5 都不需要資料庫與金鑰。進度：S1、S2、S3 完成；S4–S7 未開始。建議的 commit 切點：S1 一個（凍結）、S2 一個、S3 一個、S4+S5 一個、S6+S7 一個。

### 10.2 需要作者決定的事

| # | 問題 | 建議 | 影響 |
|---|---|---|---|
| Q1 | 本機 SQL Server 是否改為**混合驗證**以建立 `geo_reader` login | **已決定：改，且已完成（2026-10-08）**。M6 容器本來就需要 SQL 驗證；未採用的備案是 `EXECUTE AS USER … WITH NO REVERT` 搭配不進連線池的連線（無法被 `REVERT` 跳出，但連線身分仍是作者帳號，說服力較弱） | S3 開始前必須決定 |
| Q2 | M2 用程式控制的管線，AF 工具迴圈延到 M3（§2.2） | **同意延後** | 不影響 S1–S4 |

---

## 11. 風險與預先決定

| 風險 | 影響 | 預先決定 |
|---|---|---|
| ScriptDom 對某些合法語法（如 `geography::Point` 靜態方法）的節點型別與預期不同 | 標準 SQL 被誤擋 | S2 一開始先把 30 題標準 SQL 解析並列印節點型別，確認方法呼叫與靜態方法的表示方式，再寫規則 |
| 驗證器在真實模型輸出上誤擋（標準 SQL 沒用到、模型卻常用的函式） | B1 低於基線 | S6 統計誤擋；補白名單並寫理由；不得為了提高準確率放寬 V1–V5 |
| 錯誤消毒降低自我修正效果 | 修正後準確率下降 | 照實記錄；雲端單次即 30/30，預期不受影響 |
| 暫存表無法以 DB 權限擋住 | B3 無法宣稱「完全無 DDL」 | `dbExpect = tempdb_only` 明列例外；宣稱範圍限定為「GeoNl2SqlDemo 內的永久物件」 |
| 混合驗證未開啟 | S3 卡住 | 已排除：混合驗證已開（§5.2）；Q1 備案見 §10.2，未採用 |
| 回答步驟把查詢結果（含個資欄位）送到雲端模型 | 個資外洩疑慮 | M2 資料全為合成，可接受；正式處理（遮蔽視圖、資料進模型前遮蔽）屬 M4，並在紀錄中註明 |
