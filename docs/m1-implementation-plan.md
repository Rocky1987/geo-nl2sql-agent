# M1 實作計畫：資料與準確率 spike

| 項目 | 內容 |
|---|---|
| 讀者 | 開發者（作者本人與日後的貢獻者） |
| 文件日期 | 2026-10-08 |
| 里程碑定位 | M1 是整個專案的決策點：**在蓋架構之前，先證明 LLM 在這份 schema 上的 NL2SQL 準確率夠用**（`feasibility-report.md` §5） |
| 預估工時 | 3–4 人日 |
| 狀態 | **M1 完成**：S1–S7 全部完成，A3 驗收一次達標，S6 改善迴圈未觸發。本文件把可行性報告的 M1 驗收條件拆成可執行的步驟 |

---

## 1. 目標與驗收

來自 `feasibility-report.md` §5 的 M1 驗收，逐條對應到本計畫的步驟：

| # | 驗收條件 | 對應步驟 |
|---|---|---|
| A1 | 合成資料可一鍵重建，筆數與 schema 固定（含 PII 欄位、空間欄位、幾個命名不佳的欄位） | S1、S2 |
| A2 | 30 題小標準集（單表、多表 JOIN、聚合、空間各類），**凍結後**才開始測 | S3 |
| A3 | 約 100 行腳本（不用框架）量測單次生成準確率：雲端 **≥ 60%**；允許一次修正後 **≥ 75%** | S4、S5 |
| A4 | 本機小模型只要求「有量到數字」，不設及格線 | S5 |
| A5 | 若雲端未達 60%：先改善 schema 描述、欄位註解與 few-shot 再重測，**不要**往下蓋架構 | S6 |

**M1 刻意不做**（避免範圍蔓延，R1）：AST 白名單、PII 遮蔽、稽核、Web 介面、AF 工具迴圈、自動重試機制。這些屬 M2 之後。M1 的腳本是丟棄式的 spike，唯一用途是量出數字。

### 1.1 卡關點：雲端數字需要 API key

A3 的 60%／75% 門檻必須用雲端模型（Claude Haiku 4.5）量測，而金鑰由作者自行建立、只放 user-secrets。因此：

- **S1–S4 與本機模型的 S5 不需要金鑰**，可立即進行。
- **雲端量測（S5 的雲端部分）等金鑰設好後補跑**，同時補完 M0 的雲端 hello-agent。2026-10-08 金鑰已設定，M0 雲端 hello-agent 驗證通過，S5b 已執行完成。
- 2026-10-08 S5b 結果：C1（plain）單次 25/30（83.3%）、C2（described）30/30（100%），一次修正後相同；A3 的 60%／75% 門檻一次達標，M1 驗收條件全數滿足，S6 改善迴圈未觸發。詳見 `feasibility-report.md` §7「M1 實測紀錄」。**M1 已完成，可啟動 M2。**

---

## 2. 目錄與新增檔案

只在既有專案內加檔，不新增專案：

```
db/
├── 01_schema.sql                    DDL（資料表、空間索引、spike 專用唯讀使用者）
└── schema-description.md            給模型看的 schema 文字（欄位註解、命名不佳欄位的說明）
eval/GeoNl2Sql.Eval/
├── Program.cs                       改為子命令分派：hello | seed | spike
├── Seed/                            Bogus 產生資料並寫入
├── Spike/                           spike 腳本（約 100 行）
├── Questions/questions.json         30 題標準集（含標準 SQL）
└── Results/                         每次執行的輸出（JSON），不進 git 或僅保留最終一份
```

**新增套件**（加在 Eval，不加在 Core；資料存取等到 M2 確定形狀後再移進 Core 的 `Data/`，避免為單次用途先抽象化）：

| 套件 | 用途 |
|---|---|
| `Microsoft.Data.SqlClient` | 連 SQL Server、執行 DDL 與查詢；原生支援 `geography` 型別 |
| `Bogus` | 固定種子的合成資料（見 §3.3） |

---

## 3. S1–S2：資料庫與合成資料

### 3.1 環境事實

- 本機 SQL Server 2022 Express 16.0.1000.6，執行個體 `.\SQLEXPRESS`，Windows 驗證可連（已實測）。
- 該執行個體已有使用者另一個資料庫 `FreeWayDB`，**本專案不得讀寫**。新建資料庫 `GeoNl2SqlDemo`。
- 設定分兩種情境（鍵名皆為 `ConnectionStrings:Demo`，後讀的來源覆蓋先讀的）：
  - **本機開發**：放 user-secrets，不進 git（Eval 的 `UserSecretsId` 已存在）：

    ```powershell
    dotnet user-secrets set "ConnectionStrings:Demo" "Server=.\SQLEXPRESS;Database=GeoNl2SqlDemo;Integrated Security=true;TrustServerCertificate=true" --project eval/GeoNl2Sql.Eval
    ```

  - **佈署／他人 clone 後**：`appsettings.json` 只放不含密碼的範本值（同上，Windows 驗證）。使用者依自己的環境改這個檔的 `ConnectionStrings:Demo`；若改用 SQL 驗證（含密碼），改走環境變數 `ConnectionStrings__Demo`，不寫進檔案。
- 建庫本身需要連 `master`，由 `seed` 子命令處理。API 金鑰在任何情境都不寫進 appsettings.json（見第 10 節）。

### 3.2 Schema（情境：電信基地台，全為合成資料）

| 資料表 | 約略筆數 | 主要欄位 | 設計意圖 |
|---|---|---|---|
| `District` | 9 | `DistrictId`、`DistrictName`、`Boundary geography`（多邊形）、`Population` | 行政區，用於空間 JOIN／`STIntersects` |
| `BaseStation` | 200 | `StationId`、`StationName`、`DistrictId`、`Location geography`（點）、`Band`、`Status`、`InstalledDate` | 空間查詢主體：附近、距離、落在哪個區 |
| `ServicePlan` | 6 | `PlanId`、`PlanName`、`MonthlyFee`、`DataCapGb` | 小型維度表 |
| `Customer` | 1,000 | `CustomerId`、`FullName`、`NationalId`、`Phone`、`Email`、`DistrictId`、`flg1` | **PII 欄位**（M4 遮蔽用）；`flg1` 為命名不佳欄位 |
| `Subscription` | 1,500 | `SubscriptionId`、`CustomerId`、`PlanId`、`StartDate`、`dt2` | 多表 JOIN；`dt2` 為命名不佳欄位（實為合約到期日） |
| `OutageEvent` | 300 | `OutageId`、`StationId`、`StartedAt`、`DurationMinutes`、`Cause` | 聚合與時間範圍題 |

**命名不佳欄位的意義**：真實企業 schema 常有 `flg1`、`dt2` 這類名稱。這是 A5「改善 schema 描述」手段的測試對象：先用「無描述」版本量一次，再用「有欄位說明」版本量一次，兩者的差異本身就是可公開的消融結果。

**要避免的設計**：欄位名稱不要事先把答案講明（如 `IsActiveCustomer`）；不要加 M1 用不到的表。

### 3.3 合成資料原則

- 以 `Bogus` 設定固定種子（例如 `Randomizer.Seed = new Random(20261008)`），**同一台機器連跑兩次，資料逐筆相同**。這是 A1 的驗證方式：兩次 `seed` 後比對各表筆數與一組內容雜湊。
- 行政區用合成多邊形：3×3 網格共 9 區，座標落在台灣北部範圍內的虛構區域，不使用真實開放資料界線，避免授權與「看起來像真實客戶資料」的疑慮。
- 身分證字號、電話、Email 皆為格式合法的隨機值，不對應任何真人。
- 基地台點位以區為單位在其邊界內隨機撒點，並保證每區至少 10 站，讓各區統計題不出現空組。

### 3.4 已知陷阱（寫進程式註解與 README）

| 陷阱 | 說明與處理 |
|---|---|
| `geography` 多邊形環的方向 | `geography` 要求外環為逆時針（左手規則）；方向錯會得到「地球上除了這塊之外的全部」，面積與交集全錯。建多邊形後以 `ReorientObject()` 或檢查 `STArea()` 是否合理 |
| WKT 座標順序 | WKT 是 `POINT(經度 緯度)`；但 `geography::Point(緯度, 經度, srid)` 的參數順序是**緯度在前**。兩者混用是最常見的錯誤 |
| 無 `STCentroid()` | `geography` 沒有質心函式，M3 才處理（可行性報告 §1.3 事實 1、6）；M1 的題目**不出質心題** |
| 空間索引 | `geography` 空間索引需要資料表有叢集主鍵；索引在 seed 之後建立也可，M1 資料量小不影響準確率量測 |

### 3.5 `seed` 子命令行為

1. 連 `master`，若 `GeoNl2SqlDemo` 存在則 `DROP`（僅限此名稱）後重建。
2. 執行 `db/01_schema.sql`（以 `GO` 切批）。（M2 起，若設定了 `ConnectionStrings:Reader`，seed 還會建立或更新 `geo_reader` login 並執行 `db/02_reader.sql`；未設定則略過，M1 流程不受影響。見 `m2-implementation-plan.md` §5.3。）
3. 以 Bogus 產生資料並寫入：含 `geography` 的兩張表（`District`、`BaseStation`）用 `geography::STGeomFromText(@wkt, 4326)` 的參數化 INSERT，其餘四張表用 `SqlBulkCopy`。
   - **實作時的決定（S2）**：原計畫是先驗證 `SqlBulkCopy` 能否寫入 `geography`，失敗再退回參數化 INSERT。因為 `Microsoft.Data.SqlClient` 沒有 `geography` 的 .NET 型別，S2 直接採用備案；資料量小（9 + 200 列），效能沒有影響。
4. 印出各表筆數、內容雜湊（SHA-256 前 16 碼）與 `geography` 有效性檢查。連跑兩次，雜湊應完全相同（A1）。
5. 安全限制：連線字串的資料庫名稱不是 `GeoNl2SqlDemo` 時，直接拒絕執行。

### 3.6 spike 專用唯讀執行身分

模型生成的 SQL 必須在受限身分下執行，即使 M1 只是 spike：

```sql
CREATE USER spike_reader WITHOUT LOGIN;
GRANT SELECT ON SCHEMA::dbo TO spike_reader;
```

執行每題時：`EXECUTE AS USER = 'spike_reader'; <生成的 SQL>; REVERT;`。

這不需要額外密碼。**限制必須明講**：生成的 SQL 若含 `REVERT` 可跳出限制，所以 spike 另做兩道便宜的前置檢查：只接受以 `SELECT` 或 `WITH` 開頭的單一批次、執行逾時 10 秒、結果列數上限 1,000。完整的 AST 白名單 + 獨立唯讀 login 屬 M2，**M1 的這套防護不得視為安全邊界**。

**實作時的決定（S4）**：`EXECUTE AS ...; SQL; REVERT;` 放在同一批次執行時，若生成的 SQL 本身出錯，`REVERT` 不會執行到，身分會殘留在那條連線上；之後若連線被回收重用，下一次查詢可能仍是 spike_reader 身分，不易察覺。改成每題開一條不進連線池的連線（`Pooling=false`），先執行 `EXECUTE AS USER = 'spike_reader';` 再執行生成的 SQL，連線關閉即結束身分切換，不依賴 `REVERT` 一定會跑到。前置檢查也多加了關鍵字黑名單（`INSERT`、`UPDATE`、`DELETE`、`DROP`、`ALTER`、`CREATE`、`EXEC`、`REVERT`、`GRANT`、`INTO` 等），因為 T-SQL 不需要分號也能串接多個語句，只檢查開頭與分號擋不住（例如 `SELECT 1 REVERT DROP TABLE ...`）。這些仍然只是前置的便宜過濾，不是安全邊界。

---

## 4. S3：30 題標準集

### 4.1 結構

`Questions/questions.json`，每題：

```json
{
  "id": "J03",
  "category": "join",
  "question": "列出訂閱『5G 吃到飽』方案且住在信義區的客戶姓名。",
  "goldSql": "SELECT c.FullName FROM ...",
  "ordered": false
}
```

- `ordered`：標準 SQL 有 `ORDER BY` 且順序是題意一部分時為 `true`，否則比較結果集時忽略列順序。
- 題目以中文為主（目標使用情境），保留少量英文題觀察語言差異（約 3 題）。

### 4.2 分類與題數

| 類別 | 題數 | 例子 |
|---|---|---|
| 單表（`single`） | 8 | 條件篩選、`TOP N`、`DISTINCT`、日期範圍 |
| 多表 JOIN（`join`） | 8 | 2–3 表 JOIN、`LEFT JOIN` 找「沒有…的客戶」 |
| 聚合（`aggregate`） | 7 | `GROUP BY`、`HAVING`、每區基地台數、平均停機分鐘 |
| 空間（`spatial`） | 7 | `STDistance` 距離內、`STIntersects` 落在哪區、最近 N 站、各區面積 |

另要求：每類至少 1 題用到命名不佳欄位（`flg1`、`dt2`）；難度由易到難分佈，不要全是簡單題（否則數字虛高，R4）。

### 4.3 出題與凍結規則（防止評估數字自欺，R4）

1. 先寫題目與標準 SQL，**在看到任何模型輸出之前**完成。
2. 標準 SQL 逐題實際執行，確認回傳非空且合理；結果為空的題目要重寫（空結果會讓錯誤 SQL 也「答對」）。
3. 凍結：`git tag m1-questions-frozen`，並把 `questions.json` 的 SHA-256 記入結果檔。凍結後**不得為了提高準確率修改題目或標準答案**。若發現標準答案本身錯誤，必須在紀錄中公開說明並重新凍結。
4. 凍結後才允許執行任何 spike。

---

## 5. S4：spike 腳本

### 5.1 單題流程

```
載入 schema 文字 ─▶ 組 prompt ─▶ IChatClient.GetResponseAsync ─▶ 擷取 SQL ─▶
前置檢查 ─▶ 以 spike_reader 執行 ─▶ 與標準答案結果集比對 ─▶ 寫入結果
```

- 模型呼叫：`ChatClientFactory.Create(options)` 取得 `IChatClient`，直接呼叫 `GetResponseAsync`，**不用 AF Agent、不用工具**——這同時繞開本機 coder 版 tool calling 失敗的問題，也讓雲端與本機走完全相同路徑，比較才公平。
- 溫度設 0（`ChatOptions.Temperature = 0`）；本機模型仍可能有微小非確定性，所以 §5.4 規定重複次數。

### 5.2 Prompt 設計

固定的 system 訊息，內容：

1. 角色與輸出規則：「只輸出一個 T-SQL `SELECT` 語句，放在 ```sql 區塊內，不要解釋」。
2. 方言提醒：T-SQL（`TOP` 而非 `LIMIT`）、空間函式為 SQL Server `geography` 的方法呼叫語法（`a.STDistance(b)`）、距離單位為公尺。
3. Schema 文字（兩個版本，見 §6）。
4. 無 few-shot（基線版）。few-shot 只在 A5 補救時才加。

使用者訊息就是題目本身。

### 5.3 比對方法（execution accuracy）

- 執行標準 SQL 與生成 SQL，各得結果集。
- **正確** ＝ 欄位數相同，且列的多重集合相同（`ordered=false` 時忽略順序，`true` 時順序也須相同）。欄位別名不同不算錯；數值以容差 1e-6 比較浮點數；`NULL` 與 `NULL` 視為相等。
- 兩者都丟錯、或生成 SQL 無法執行，皆算失敗；失敗類型記錄為：`no_sql`（沒抽出 SQL）、`precheck`（前置檢查擋下）、`exec_error`（執行錯誤，附錯誤訊息）、`wrong_result`（執行成功但結果不同）。
- 不用 LLM 當裁判，不用字串比對 SQL。

### 5.4 量測指標與重複

| 指標 | 定義 |
|---|---|
| **單次準確率**（A3 的 60%） | 每題只生成一次，正確題數／30 |
| **一次修正後準確率**（A3 的 75%） | 單次失敗且屬 `exec_error` 的題目，把**錯誤訊息**（不含結果資料）回饋給模型再生成一次；`wrong_result` 不重試，因為真實系統看不到標準答案 |
| 分類準確率 | 四個類別各自的單次準確率，用來判斷弱點在哪（預期空間類最低） |

每組（模型×schema 版本）**重複 3 輪**取平均與範圍，避免單次運氣。每次執行寫出一份 JSON 到 `Results/`，含：模型、schema 版本、題集 SHA-256、每題的生成 SQL、失敗類型與錯誤訊息、耗時、Token 用量（雲端）。

### 5.5 程式規模

約 100 行：載入題目、迴圈、呼叫模型、抽出 SQL（正規表示式取 ```sql 區塊，抓不到則取第一個 `SELECT`／`WITH` 起始的文字）、`SqlCommand` 執行、比對、寫 JSON。不拆介面、不做 DI。若超過 300 行，先檢查是否過度設計（`CLAUDE.md` §2）。

**實作時的決定（S4）**：原預估的 100 行偏樂觀，實際約 165 行（不含空行與純註解）。超出的部分對應到計畫本身的要求，不是額外功能：execution accuracy 比對與前置檢查（§5.3）份量本來就不小；plain／described 兩版 schema 文字由同一段邏輯從 `schema-description.md` 自動產生，以保證兩版除說明外文字完全相同（§6.1）；另加了 `--fake` 選項（不呼叫模型、直接用固定文字當回應）供本步驟驗收測試使用（§8 的「刻意餵一個含 `DROP` 的假回應」）。門檻因此由 150 行上調為 300 行。

---

## 6. S5–S6：量測與改善迴圈

### 6.1 實驗矩陣

| 組別 | 模型 | Schema 版本 | 需要金鑰 | 備註 |
|---|---|---|---|---|
| L1 | Ollama `qwen2.5:3b` | 無描述（純 DDL） | 否 | 本機基線 |
| L2 | Ollama `qwen2.5:3b` | 有欄位說明 | 否 | |
| L3 | Ollama `qwen2.5-coder:3b` | 有欄位說明 | 否 | 此處 coder 版不需要 tool calling，才適合拿來和標準版比 NL2SQL 準確率 |
| C1 | `claude-haiku-4-5` | 無描述 | **是** | **決定 A3 的主要數字** |
| C2 | `claude-haiku-4-5` | 有欄位說明 | **是** | |

Schema 版本的差異只在命名不佳欄位與表的說明，**其餘文字完全相同**，才能把差異歸因於描述。

### 6.2 決策表（A5）

| C 組單次準確率 | 動作 |
|---|---|
| ≥ 60% 且一次修正後 ≥ 75% | M1 通過，可啟動 M2 |
| 40–60% | 依 §6.3 改善後重測，最多兩輪 |
| < 40% | 先檢查是否是 schema 描述、標準答案或比對器的問題；仍然如此再回頭評估整體方向（這是 M1 存在的理由） |

### 6.3 改善手段（依序嘗試，每次只改一項並重測）

1. 補強欄位說明與命名不佳欄位的語意。
2. 在 prompt 加入該 schema 的空間函式用法範例（不要放題集裡的題目）。
3. 加入少量 few-shot；**few-shot 範例不得取自 30 題標準集**，否則是洩題。若需要範例，另寫不在題集內的範例題。
4. 每一輪改動與重測數字都保留，最後公開「改前／改後」。

### 6.4 成本與時間控管

- 雲端費用以官方定價頁為準。粗估：30 題 × 每題約 2–3k 輸入 token × 3 輪 × 2 個 schema 版本，約 0.5M 輸入 token 等級，Haiku 級別為少量美元。開跑前先以 3 題試跑量出實際 Token 用量，再乘算全量，**超過自設預算不要硬跑**。
- 本機 3B 生成較慢（GTX 1650），可先用 `--limit 5` 之類的參數小樣本確認流程再全量跑。
- Anthropic 主控台設定月預算上限（R5）。

---

## 7. S7：結果紀錄

M1 結束時更新 `feasibility-report.md`，新增「M1 實測紀錄」，包含：

- 五個實驗組的單次／一次修正後準確率與分類準確率（表格）。
- 題集 SHA-256 與凍結 tag。
- **失敗案例分析**：至少列出代表性的 `wrong_result` 與 `exec_error`，附生成 SQL、原因。作品集的賣點是連失敗一併公開（R4）。
- schema 描述有無的差異。
- 對 R3（本機小模型是否可用）與 §6-1（3B 準確率的推測）的回答：實測數字取代推測。
- 勾選 M1 驗收項目。README 不寫里程碑進度（README 是給公開讀者看的用途、內容、亮點與安裝執行說明），進度只記在 docs。

---

## 8. 執行順序與檢查點

| 步驟 | 內容 | 驗證（做完要看到什麼） | 需要金鑰 | 狀態 |
|---|---|---|---|---|
| S1 | `db/01_schema.sql`、`seed` 建庫骨架 | `GeoNl2SqlDemo` 建立成功；`spike_reader` 存在；`FreeWayDB` 沒被動到 | 否 | 完成 |
| S2 | Bogus 資料與寫入 | 各表筆數符合 §3.2；**連跑兩次雜湊相同**；`geography` 欄位 `STArea()`／`STIsValid()` 合理 | 否 | 完成 |
| S3 | 30 題與標準 SQL | 標準 SQL 全部可執行且非空；打上凍結 tag | 否 | 完成（tag `m1-questions-frozen`） |
| S4 | spike 腳本 | 以 3 題煙霧測試：能抽出 SQL、執行、比對、寫 JSON；刻意餵一個含 `DROP` 的假回應，確認被擋下 | 否 | 完成 |
| S5a | 本機 L1–L3 全量 3 輪 | 取得三組數字 | 否 | 完成：12/30、13/30、14/30 |
| S5b | 雲端 C1、C2 全量 3 輪 | 取得 A3 數字 | **是** | 完成：25/30（83.3%）、30/30（100%） |
| S6 | 視需要的改善迴圈 | 符合 §6.2 決策表 | 視情況 | **未觸發**（C1 單次 83.3% ≥ 60% 一次達標） |
| S7 | 寫入紀錄、commit、更新 README | 驗收項目全勾 | 否 | 完成（commit 18223bd） |

建議的 commit 切點：S1+S2 一個、S3（含凍結 tag）一個、S4 一個、S5 的結果一個。S3 的凍結必須早於 S5 的任何一次模型呼叫。

---

## 9. 風險與預先決定

| 風險 | 影響 | 預先決定 |
|---|---|---|
| `geography` 欄位寫入遇到 `Microsoft.Data.SqlClient` 型別限制 | S2 卡住 | 退路：以參數化 SQL 搭配 `geography::STGeomFromText(@wkt, 4326)` 逐列或小批次插入，資料量小可接受 |
| 標準答案本身有錯，使準確率被低估 | 數字不可信 | S3 逐題人工檢視標準 SQL 與結果；發現錯誤公開說明並重新凍結 |
| 題目偏簡單，數字虛高 | 履歷說服力下降 | 按 §4.2 控制難度分佈，空間類與 JOIN 類保留較難題 |
| 本機 3B 空間類題目幾乎全錯 | 數字很低 | 照實呈現；本機軌定位本來就是「證明可切換並誠實呈現落差」 |
| 雲端未達 60% | 方向需調整 | 依 §6.2、§6.3 處理，不往 M2 推進 |
| 模型回應含多個 SQL 或夾雜解釋 | 抽取失敗被誤判為模型錯 | `no_sql` 單獨分類統計，並人工抽看，必要時調整抽取規則（規則變更要在紀錄中說明） |
| 生成的 SQL 有副作用 | 破壞資料庫 | §3.6 的唯讀身分與前置檢查；`seed` 可隨時一鍵重建，資料毀損不致命 |

---

## 10. 開始前需作者決定或提供（M1 已執行完畢，以下為當時的前置項目）

1. **確認方案**：資料庫名稱 `GeoNl2SqlDemo`、六張表的範圍、行政區用合成 3×3 網格（若想改用真實開放資料，需先確認授權）。
2. **Anthropic API 金鑰**：由作者自行在 Console 建立並設定 user-secrets；S5b 前需要（已設定）。不要貼給 AI 助理，也不要寫進任何檔案。
3. **Anthropic 月預算上限**：建議先設定再跑 S5b。
