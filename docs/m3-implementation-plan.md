# M3 實作計畫：GIS + 地圖前端

| 項目 | 內容 |
|---|---|
| 讀者 | 開發者（作者本人與日後的貢獻者） |
| 文件日期 | 2026-10-09 |
| 里程碑定位 | 在 M2 的「自然語言 → 驗證 → 唯讀執行」之上，補上空間資料的輸出（GeoJSON）、確定性的空間運算（質心、緩衝區），以及能在地圖上看到結果的 Web 前端；同時讓 Microsoft Agent Framework 的工具迴圈真正派上用場（`m2-implementation-plan.md` §2.2） |
| 預估工時 | 約 7 人日（模組 6、11） |
| 完成後 | 不打 tag（`feasibility-report.md` §5 的 M3 只產出 Demo 素材；下一個 tag 是 M4 的 `v0.2.0`） |
| 狀態 | **進行中（2026-10-09）**：S1～S4 已完成並 push；S5（Web 前端）程式已完成，尚未 commit，待作者在瀏覽器確認並截圖 |

---

## 1. 目標與驗收

來自 `feasibility-report.md` §5 的 M3 驗收，逐條對應到本計畫的步驟：

| # | 驗收條件 | 對應步驟 |
|---|---|---|
| G1 | 輸出通過 RFC 7946 GeoJSON 結構驗證，可在 Leaflet 顯示 | S1、S5 |
| G2 | 質心（geometry 轉換法）與 NetTopologySuite 投影後參考值誤差 < 0.1%；**程式中不得以 `EnvelopeCenter()` 冒充質心** | S2 |
| G3 | 4326 ↔ 3826 往返誤差 < 0.5 公尺；緩衝區以公尺為單位且結果為有效幾何 | S2 |
| G4 | 空間查詢的執行計畫確認使用空間索引 | S3 |

本計畫自訂三條，避免 M3 把 M2 已建立的東西弄壞，或只靠「看起來能動」過關：

| # | 條件 | 理由 |
|---|---|---|
| G5 | M2 的準確率與安全邊界**不退步**：`PromptBuilder` 不變（既有 prompt 測試仍綠）、151 項離線測試仍綠、`pipeline --fake "{gold}"` 驗證器誤擋仍為 0 | M3 新增的是工具與前端，不應動到 NL2SQL 的提示詞；提示詞不動，30/30 的基線就不必為了回歸再花雲端費用 |
| G6 | 新增的工具**不讓模型自己寫空間 SQL 以外的 SQL**：質心與緩衝區是 C# 寫死、參數化的 SQL，模型只能給參數（行政區名稱、經緯度、半徑） | 與 M2 的原則一致：能由程式確定的事，不交給模型生成 |
| G7 | 代理程式的工具迴圈**有上限**，不會無限呼叫（延續 M2 B5 的精神） | 工具迴圈由模型決定何時再呼叫，上限必須由程式決定 |

**M3 刻意不做**（避免範圍蔓延，R1）：提示詞注入偵測、PII 遮蔽、角色與使用者驗證、稽核（M4）、60 題評估與消融（M5）、Docker 與 GitHub Actions（M6）、提升本機模型準確率（延到 v1.0.0 之後）、讓模型自行撰寫質心／緩衝區的 SQL（見 §2.2）、離線地圖底圖與多圖層管理。

---

## 2. 架構

### 2.1 現況與缺口

- `ReadOnlySqlExecutor` 把 `geography` 欄位讀成原始位元組（`byte[]`），結果只有欄位名稱，**沒有欄位型別**，所以下游分不出哪一欄是空間資料。
- `Web` 專案是 MVC 範本骨架（`HomeController` 與空白首頁），尚未參照任何功能。
- Core 已參照 `Microsoft.Agents.AI` 1.23.0，但還沒有任何程式使用它。
- 資料庫端：`District.Boundary`（多邊形）與 `BaseStation.Location`（點）皆為 `geography`、SRID 4326，兩者都有空間索引（`SIX_District_Boundary`、`SIX_BaseStation_Location`）。

### 2.2 質心與緩衝區為什麼做成工具，而不是教模型寫 SQL

質心要走「`geography` → WKB → `geometry` → `STCentroid()` → 轉回 `geography`」（可行性報告 §1.3 事實 6），寫法冗長又有陷阱（經緯度順序、`EnvelopeCenter()` 不是質心）。若把這些規則塞進 `schema-description.md`：

1. 會改動 M1／M2 凍結的提示詞，30/30 基線要重跑才能宣稱沒退步。
2. 3B 本機模型更容易寫錯；寫錯又難以察覺（`EnvelopeCenter()` 的結果看起來很像質心）。

所以 M3 把它做成**固定模板的工具**（G6）：程式寫死 SQL、參數化，模型只負責「選哪個工具、填什麼參數」。這同時是「多個工具、由模型自行選擇」的情境，正是 Agent Framework 工具迴圈划算的地方。

### 2.3 單次請求流程

```
問題 ─▶ Agent（ChatClientAgent，工具迴圈，上限 §6.2）
          │
          ├─▶ query_database(question)          ─▶ 既有 Nl2SqlPipeline（M2，不變）
          ├─▶ get_district_centroid(name)       ─▶ 固定模板 SQL（參數化）─▶ 唯讀 login
          └─▶ buffer_around_point(lat, lon, m)  ─▶ 固定模板 SQL（參數化）─▶ 唯讀 login
                       │
                       ▼
        每個工具把「給模型看的文字摘要」回給模型，
        把「畫地圖用的 GeoJSON」放進本次請求的 MapResult（旁路，不經過模型）
                       │
                       ▼
        Web：回答文字 + 資料表 + 生成的 SQL + GeoJSON ─▶ Leaflet
```

**GeoJSON 走旁路，不回給模型**：一千列多邊形的 GeoJSON 會吃掉大量 token、模型也沒有理由看座標。模型只收到「共 N 筆、欄位為何、質心座標」這類摘要。

### 2.4 新增與修改的檔案

不新增專案（沿用 `project-technical-guide.md` §2.4 的資料夾分法）：

```
src/GeoNl2Sql.Core/
  Spatial/                       ← 新增
    GeographyReader.cs           位元組（模型 SQL）或 WKT（工具）→ NetTopologySuite 幾何
    GeoJsonBuilder.cs            幾何／查詢結果 → FeatureCollection
    GeoJsonValidator.cs          RFC 7946 結構檢查（測試與執行期共用）
    SpatialQueries.cs            質心、緩衝區的固定模板 SQL
  Agent/                         ← 新增
    MapResult.cs                 本次請求累積的 GeoJSON（旁路）
    GeoTools.cs                  三個工具函式（AIFunction 的來源）
    GeoAgent.cs                  組裝 ChatClientAgent、工具與迴圈上限
  Guardrails/ReadOnlySqlExecutor.cs  修改：結果加欄位型別；新增參數化執行
src/GeoNl2Sql.Web/
  Controllers/QueryController.cs ← 新增（JSON 端點）
  Controllers/HomeController.cs  修改：首頁
  Views/Home/Index.cshtml        修改：提問框、地圖、結果表
  wwwroot/js/map.js              新增：Leaflet 繪圖
tests/GeoNl2Sql.Tests/
  Spatial/、Agent/               ← 新增
eval/GeoNl2Sql.Eval/
  Questions/tools.json           ← 新增：工具選擇題組（S4，先凍結）
```

新增套件（版本於 S1 查核後記入 `feasibility-report.md` §8 之後的實測紀錄，**目前只是預期，尚未驗證**）：`NetTopologySuite`、`NetTopologySuite.IO.GeoJSON`（可行性報告已列）、`NetTopologySuite.IO.SqlServerBytes`（讀模型生成 SQL 回傳的 geography 二進位；固定模板的工具改用 `ToString()` 取 WKT，見 §3.1）；`ProjNet` 只放測試專案。

### 2.5 離線與資料庫測試的分界

延續 M2 B6：不需要資料庫與模型的測試（GeoJSON 組裝與驗證、工具迴圈上限、旁路邏輯）離線可跑；需要資料庫的標記 `Database`；工具選擇的量測屬於 `Eval` 子命令，不放進單元測試。

---

## 3. S1：空間資料讀取與 GeoJSON

### 3.1 兩條讀取路徑（先做前置驗證，再寫其餘程式）

M2 把 geography 當 `byte[]` 帶過去，沒有解析過。空間資料有兩個來源，讀法不同：

| 來源 | 讀法 | 理由 |
|---|---|---|
| **C# 固定模板的工具**（質心、緩衝區，S2） | SQL 端 `欄位.ToString()` 取得 WKT 文字，再用 NetTopologySuite 的 `WKTReader` 解析 | 2026-10-09 已在本機實測：`BaseStation.Location.ToString()` 回 `POINT (121.424174 24.942114)`，`District.Boundary.ToString()` 回 `POLYGON ((121.4 24.9, …))`，兩者與 `STAsText()` 相同，座標為經度在前。SQL 是我們自己寫的，加 `.ToString()` 沒有成本，也不用解析二進位 |
| **模型生成的 SQL**（`query_database`） | 執行器依欄位型別，把 `byte[]` 以 `NetTopologySuite.IO.SqlServerBytes` 解析 | 模型寫的是 `SELECT Location …`，執行器拿到的仍是位元組；要求模型一律加 `.ToString()` 會動到 M1／M2 凍結的 prompt（30/30 基線須重跑），且模型可能忘記加，所以不採用 |

用 30 行以內的臨時程式確認，結果記進實測紀錄：

1. （模型生成 SQL 的路徑）`NetTopologySuite.IO.SqlServerBytes` 能讀出 `District.Boundary` 與 `BaseStation.Location` 的位元組，且**座標順序**是 X＝經度、Y＝緯度（GeoJSON 要求經度在前）；解析結果須與同一列的 `ToString()` WKT 解析結果逐座標相同，這樣 `ToString()` 就是現成的對照基準。
2. 多邊形外環方向：SQL Server `geography` 的外環是逆時針（左手規則），RFC 7946 也要求外環逆時針，理論上相容。目前只看過一個行政區（`POLYGON ((121.4 24.9, 121.5 24.9, 121.5 25, 121.4 25, 121.4 24.9))`，逆時針）；**全部行政區**的方向於 S1 實測確認，不憑記憶。
3. 若 `SqlServerBytes` 在 .NET 9 不可用或結果不對，備案：模型生成 SQL 的路徑改在執行器端用 `SqlDataReader` 的 UDT 讀取；**不**引入需要原生元件的 `Microsoft.SqlServer.Types`。若連此備案也行不通，才重新評估是否要求模型加 `.ToString()`（需重跑 30 題基線）。決定時要寫理由。

**S1 前置驗證結果（2026-10-09）**：`NetTopologySuite.IO.SqlServerBytes` 2.1.0 在 net9.0 可用；9 個行政區（多邊形）與 200 個基地台（點）的位元組解析結果與同一列 `ToString()` 的 WKT 解析結果**逐座標相同**（最大差 0）；X 皆為經度；9 個外環**全為逆時針**。備案（UDT 讀取、要求模型加 `.ToString()`）未啟用。另發現：`NetTopologySuite.IO.GeoJSON` 4.0.0 以 Newtonsoft.Json 實作，且 `GeoJsonWriter` 預設會**自行修正環方向**；本專案設為 `DoNotModify`，讓檢查器看到 SQL Server 的真實方向。

### 3.2 `ReadOnlySqlExecutor` 的調整

- `SqlQueryResult` 加一個**有預設值的**第四個參數 `ColumnTypes`（依 `GetDataTypeName`），既有 `new(columns, rows, truncated)` 的呼叫與測試不必改。
- 新增 `ExecuteAsync(string sql, IReadOnlyDictionary<string, object> parameters, …)`，供工具的固定模板使用；仍走 `geo_reader`、仍受逾時與列數上限約束。模型生成的 SQL 仍只走原本無參數的版本。

### 3.3 `GeoJsonBuilder`

- 輸入 `SqlQueryResult`；每個 geography 值產生一個 `Feature`，其餘欄位為 `properties`；結果是 `FeatureCollection`。
- 座標精度四捨五入到小數 6 位（約 0.1 公尺），G3 的 0.5 公尺門檻不受影響。
- 沒有任何空間欄位時回傳 `null`，前端就不畫地圖（不是錯誤）。

### 3.4 `GeoJsonValidator`（G1）

沒有現成、可信的 .NET RFC 7946 驗證器可直接當標準，所以自己寫**結構檢查**並明講範圍：型別名稱合法、`coordinates` 巢狀層數正確、經度 −180～180／緯度 −90～90、環至少 4 個位置且首尾相同、外環逆時針、`Feature` 有 `geometry` 與 `properties`。不宣稱是完整的規格符合性測試。

### 3.5 驗證（S1 的完成條件）

- `Spatial/GeoJsonBuilderTests`（離線）：手寫幾何的輸出通過 `GeoJsonValidator`；故意壞掉的輸入（環未閉合、順時針外環、經緯度對調超出範圍）被檢查器抓到。
- `Database/SpatialReadTests`：全部 `District` 與 `BaseStation` 經 `GeoJsonBuilder` 後 100% 通過檢查器。

---

## 4. S2：質心與緩衝區

### 4.1 質心（G2）

`SpatialQueries.DistrictCentroid`：依 `DistrictName` 參數查 `Boundary`，在 SQL 端以 geometry 轉換法算質心（`geography` → WKB → `geometry`（SRID 4326）→ `STCentroid()` → 轉回 `geography`）。回傳時對結果欄位加 `.ToString()` 取 WKT，由 `WKTReader` 解析（§3.1）。**這是平面（度數）質心**，小範圍行政區誤差可忽略，大範圍或高緯度會有偏差；文件與工具說明都要寫明。

**參考值**：用 NetTopologySuite 取該行政區邊界，**投影到 EPSG:3826（TWD97 / TM2）後**算質心，再轉回 4326（投影用 `ProjNet`，僅限測試專案）。

**「誤差 < 0.1%」的定義**（可行性報告沒有寫分母，這裡明定，待 §9.2 Q2 確認）：兩個質心的距離，除以該行政區外接框的對角線長度，全部行政區皆須 < 0.1%。另外同時記錄**絕對距離（公尺）**，因為小分母的相對誤差容易誤導。

另設一條**反面測試**：`SpatialQueries` 的原始碼文字不得出現 `EnvelopeCenter`（測試掃描 Core 原始碼檔案），把可行性報告「不得冒充」做成可執行的檢查；並附一個案例證明 `EnvelopeCenter()` 與真質心在不規則行政區確實不同，說明為何要擋。

### 4.2 緩衝區與投影往返（G3）

- `SpatialQueries.BufferAroundPoint(lat, lon, meters)`：`geography::Point(@lat, @lon, 4326).STBuffer(@meters)`（注意**緯度在前**），回傳多邊形（同樣以 `.ToString()` 取 WKT）與基地台點位（`Location.ToString()`），並列出落在其中的基地台數量與前 N 筆（`STIntersects`，可用空間索引，見 S3）。參數檢查：緯度 −90～90、經度 −180～180、半徑 > 0 且有上限（預設 50 公里，寫入設定），超出範圍直接拒絕，不送到資料庫。
- **緩衝區有效性**：`STIsValid() = 1`；面積與理論圓面積 πr² 的差距在容許範圍內（`STBuffer` 以多邊形逼近圓，容許值 S2 實測後定，記錄實測值，不先猜）。
- **以 NetTopologySuite 對照**：把圓心投影到 3826，用 NTS 在平面上做同半徑緩衝，與 SQL 端結果比較面積，兩者應在同一量級的小誤差內。
- **4326 ↔ 3826 往返**：全部基地台點位 4326 → 3826 → 4326，最大位移 < 0.5 公尺（用 `ProjNet`，測試專案）。這驗證的是對照基準的可信度，不是產品功能。

### 4.3 驗證（S2 的完成條件）

- `Database/CentroidTests`：全部行政區 G2 通過；`EnvelopeCenter` 反面測試通過。
- `Database/BufferTests`：多個半徑（例如 500、2000、10000 公尺）有效性與面積；非法參數被拒絕且未連線資料庫。
- `Spatial/ProjectionRoundTripTests`：G3 往返 < 0.5 公尺，並記錄最大值。

**S2 實測結果（2026-10-09）**：

- G2：9 個行政區的質心與 NTS 投影參考值相差皆為 0.20 公尺（外接框對角線約 15 公里，相對 0.0013%），遠小於 0.1%。注意：種子資料的行政區都是近似矩形，這個結果證明轉換法正確，但不代表不規則大區域也能這麼準；§4.1 的限制說明仍然有效。
- 緩衝區（圓心 25.0478, 121.5170）：半徑 500／2000／10000 公尺的面積都比 πr² 小 0.040%、比 NTS 投影緩衝小 0.030%，皆有效；測試容許值據此定為 0.2%。半徑 10 公里內有 74 座基地台。
- G3：4326 → 3826 → 4326 往返，台灣範圍格點最大位移 4.3 毫米；200 座基地台最大 4.2 毫米；皆遠小於 0.5 公尺。
- 反面測試：`SpatialQueries.cs` 不含 `EnvelopeCenter`；L 形範例中外接框中心 (5, 5) 與質心 (3.22, 3.22) 明顯不同。
- 緩衝區參數（緯度、經度、半徑，含 NaN／無限大）超出範圍時在連線前拒絕，已用不可連線的連線字串驗證。
- 測試分布：離線 `Spatial/SpatialOfflineTests`（往返、原始碼掃描、參數拒絕）；資料庫 `Database/SpatialQueriesTests`（質心、緩衝區、全部基地台往返）。

---

## 5. S3：空間索引的執行計畫（G4）

- 以**擁有者連線**（`Demo`）執行 `SET SHOWPLAN_XML ON` 取得估計計畫（`geo_reader` 沒有 `SHOWPLAN` 權限，且這個權限不應為了測試而授予）。
- 檢查對象：(a) S2 的兩個固定模板；(b) 標準集 30 題中的 7 題空間題的標準 SQL。判定方式：計畫 XML 中出現 `SIX_BaseStation_Location`／`SIX_District_Boundary` 的索引存取。
- **預先決定**：合成資料量小，最佳化工具可能選擇全表掃描而不用空間索引，這不代表索引無效。若發生：先如實記錄「自然計畫未使用」，再用索引提示（`WITH (INDEX(…))`，僅限測試中的探測查詢）證明索引**可被使用**並記錄兩者差異；不為了讓測試通過而改寫查詢，也不在產品的固定模板裡加提示。
- 另外確認 `STDistance` 的寫法：要用得到索引，需寫成 `col.STDistance(@p) <= @r`（或最近鄰的 `TOP … ORDER BY`）的形式；固定模板依此寫並在註解說明。
- 驗證：`Database/SpatialIndexPlanTests` 將每個查詢的「是否使用索引」寫入輸出，結論記進實測紀錄。

**S3 實測結果（2026-10-09）**：

| 查詢 | 自然計畫 | 加索引提示探測 |
|---|---|---|
| 固定模板 `StationSql`（緩衝區內基地台） | 未使用（200 筆，走主鍵掃描） | 可使用 `SIX_BaseStation_Location` |
| 固定模板 `CentroidSql`（依名稱查質心） | 未使用 | 無法產生計畫：只依名稱查詢，沒有空間篩選，索引不適用 |
| G01（圓內基地台數量，`STDistance <= 常數`） | 未使用 | 可使用 |
| G03（點落在哪個行政區，`STIntersects`） | 未使用 | 可使用 |
| G07（點所在行政區的客戶數，`STIntersects` 加連接） | 未使用 | 可使用 `SIX_District_Boundary` |
| G02（最近 5 座，`TOP … ORDER BY STDistance`） | 未使用 | 不適用：最近鄰要有 `WHERE col.STDistance(…) IS NOT NULL` 才能用索引，標準 SQL 沒有 |
| G04／G05（面積、人口密度） | 未使用 | 不適用：沒有空間篩選 |
| G06（基地台自我連接的 `STDistance`） | 未使用 | 不適用：距離比的是另一列的欄位，不是常數 |

結論：
- 資料量小（9 個行政區、200 座基地台），最佳化工具在所有查詢都選擇掃描，**自然計畫沒有一個使用空間索引**，這是如實記錄的結果。
- 索引「可被使用」已證明：有空間篩選的查詢（`StationSql`、G01、G03、G07）加提示後計畫都走空間索引。沒有空間篩選的 4 類查詢，索引本來就不適用，不是索引失效。
- 標準 SQL 已凍結，不為了用到索引而改寫（例如幫 G02 補 `IS NOT NULL`）；產品的固定模板裡也沒有索引提示。
- 判定「可使用／不適用」的測試會自行斷言：適用清單與實測不符時測試失敗。

---

## 6. S4：Agent Framework 與三個工具

### 6.1 工具定義（G6）

| 工具 | 參數 | 行為 | 回給模型的內容 |
|---|---|---|---|
| `query_database` | `question`（字串） | 呼叫既有 `Nl2SqlPipeline`；成功時把結果轉 GeoJSON 放進 `MapResult` | 欄位名稱、列數、前 20 列、是否截斷；失敗時回固定的失敗原因 |
| `get_district_centroid` | `districtName` | `SpatialQueries.DistrictCentroid`；找不到行政區時回固定訊息，不丟例外 | 行政區名稱與質心經緯度；質心點放進 `MapResult` |
| `buffer_around_point` | `latitude`、`longitude`、`radiusMeters` | `SpatialQueries.BufferAroundPoint` | 圓內基地台數量；緩衝區多邊形與基地台放進 `MapResult` |

工具描述（給模型看的說明文字）用繁體中文寫明：何時該用、參數的單位與順序（**緯度在前、半徑單位為公尺**）。這些描述是提示詞的一部分，改動要記錄版本。

### 6.2 迴圈上限與錯誤處理（G7）

- 單次請求的工具呼叫輪數設上限（預先決定 **6 輪**，設定 `Agent:MaxToolRounds`，可調）；超過就停止並回「無法在限制內完成」。**實作語意**（離線測試實測）：`FunctionInvokingChatClient.MaximumIterationsPerRequest = N` 時，執行 N 輪工具呼叫後會再問模型一次，所以模型請求最多 N＋1 次；第 N＋1 次若仍要求呼叫工具，視為超限，`GeoAgentResult.HitLimit` 為 true、回固定訊息、不丟例外。
- `query_database` 內部仍是 M2 的 3 次生成上限，所以單次請求的模型呼叫最壞情況有明確上界：外層迴圈 6 輪，每輪若都是 `query_database` 最多再 3 次生成。
- 工具內的例外（資料庫逾時、參數非法）轉成固定訊息回給模型，**不把資料庫錯誤原文交給模型**（沿用 `SqlErrorSanitizer` 的原則）。
- `MapResult` 屬於單次請求，以 DI 的 scoped 生命週期或 `AsyncLocal` 隔離，避免兩個同時進行的請求互相混入對方的地圖資料。

### 6.3 工具選擇的量測（先凍結題組，再量）

- 新增 `eval/GeoNl2Sql.Eval/Questions/tools.json`，**12 題**：4 題一般資料查詢（應選 `query_database`）、4 題質心、4 題緩衝區。每題記錄預期工具與預期參數（行政區名稱完全相符；座標誤差 ≤ 0.001 度；半徑相等）。
- 先 commit、再執行量測（沿用 M1／M2 的凍結做法，R4）。這是小題組，**只作為工具迴圈能運作的證據，不是準確率宣稱**，實測紀錄要明講樣本小。
- 新增 `Eval` 子命令 `agent`（量測，印出每題選了哪個工具、參數、是否正確、呼叫次數與 token 用量，結果寫 JSON）。
- 雲端模型跑 1 輪，預估呼叫量為 12 題 × 約 2–4 次。本機 `qwen2.5:3b` 另量一輪**只記錄、不設及格線**（M0 只驗證過單一簡單工具的 tool calling，多工具未知）；若本機無法穩定呼叫工具，記錄為已知限制，Web 預設走雲端，不在 M3 內追求改善。

### 6.4 驗證（S4 的完成條件）

- `Agent/ToolLoopLimitTests`（離線，假的 `IChatClient`）：模型永遠要求呼叫工具時，恰好在上限輪數停止，不丟例外。
- `Agent/MapResultIsolationTests`（離線）：兩個並行請求的 `MapResult` 互不混淆。
- `Agent/GeoToolsTests`（離線，假的執行器）：非法參數被拒絕且不送到執行器；錯誤訊息固定。
- 量測：雲端 12 題的工具選擇結果寫入 `Results/`；每題不選錯工具為目標，若有錯，記錄代表案例與原因。

**S4 實作紀錄（2026-10-09）**：
- 新增 `Agent/MapResult`（單次請求的旁路，加鎖）、`Agent/GeoTools`（三個工具；每次請求一個新實例，持有該次的 `MapResult` 與呼叫紀錄）、`Agent/GeoAgent`（組 `ChatClientAgent` 與 `FunctionInvokingChatClient`；每次 `RunAsync` 建新工具集，所以同一個 Agent 並行處理請求不會混資料）。提示詞版本 `GeoAgent.PromptVersion = "v1"`（涵蓋 Agent 指示與三個工具描述）。
- 工具內預期的錯誤（參數不合法、找不到行政區、資料庫錯誤）回固定訊息；資料庫錯誤只回 `SqlErrorSanitizer` 消毒後的文字。非預期例外照常往外傳（`FunctionInvokingChatClient` 預設只給模型一則通用錯誤訊息，不含例外原文）。
- 離線測試 `Agent/GeoAgentTests`（11 項）：輪數上限（1／3／6 輪都恰好停止）、正常流程且模型收到的工具結果不含座標、20 個並行請求的地圖資料互不混入、非法緩衝區參數被擋且未連線、查詢失敗只回消毒原因、工具名稱與描述。資料庫測試 `Database/GeoToolsDatabaseTests`（3 項）：質心與緩衝區工具對真實資料庫，旁路的 GeoJSON 通過檢查。
- `eval/GeoNl2Sql.Eval/Questions/tools.json`（12 題）與 `agent` 子命令已完成；**題組在量測之前先 commit 凍結**。判定：選對工具＝第一個工具呼叫等於預期工具；參數正確＝質心行政區名稱完全相符，緩衝區緯經度誤差 ≤ 0.001 度、半徑相差 ≤ 0.5 公尺。

**雲端工具選擇量測（2026-10-09，claude-haiku-4-5，提示詞 v1，`tools.json` 於 `4bd7fb9` 凍結後執行，1 輪）**：
- 12 題**全部選對工具、參數也全對**（一般查詢 4／4 選 `query_database`，質心 4／4 行政區名稱完全相符，緩衝區 4／4 緯經度與公尺半徑正確，包含「3 公里」「1.5 公里」換算成 3000、1500 公尺）；沒有超過輪數上限，沒有模型錯誤。
- 成本：模型呼叫 28 次（先跑 `--limit 3` 的冒煙測試另有 9 次），輸入 71,403、輸出 2,308 tokens。每題呼叫 2～3 次：質心與緩衝區 2 次（選工具、讀結果後回答），一般查詢 3 次（選工具、`Nl2SqlPipeline` 生成 SQL、回答）。
- 地圖旁路正常：4 題一般查詢沒有空間欄位所以無地圖資料；質心題各 1 個點；緩衝區題為 1 個緩衝區加最近的基地台（上限 20，這份資料在半徑內最多 5 座）。
- **限制（明講）**：樣本只有 12 題、單一模型、單輪，題目措辭相當直接，**只證明工具迴圈與三個工具能運作，不是準確率宣稱**。結果檔寫在 `eval/GeoNl2Sql.Eval/Results/`（該資料夾不入版控）。

**本機模型觀察（2026-10-09，qwen2.5:3b，提示詞 v1，同一份凍結的 `tools.json`，1 輪；依 Q6 只記錄、不設及格線）**：
- 選對工具 8／12，工具與參數都對 7／12，沒有超過輪數上限；模型呼叫 24 次，輸入 20,353、輸出 1,105 tokens。
- 質心 4／4、緩衝區 3／4 選對且參數正確；緩衝區有 1 題把「1.5 公里」換算成 150 公尺（應為 1500）。
- **一般查詢 4 題全部沒有成功呼叫 `query_database`**：沒有任何有效的工具呼叫紀錄，其中 3 題的回答卻宣稱「資料庫查詢失敗」（實際上根本沒查詢，等於編造失敗原因），1 題把工具呼叫寫成一般文字。只有 1 輪、樣本小，無法判斷是模型能力還是這份工具描述不適合 3B 模型，也沒有為此調整提示詞。
- 已知限制：本機 3B 模型在多工具情境不穩定，尤其是會編造「查詢失敗」；Web 預設走雲端模型。不在 M3 內追求改善（與 §6.3 預先決定一致）。

---

## 7. S5：Web 前端（ASP.NET Core MVC + Leaflet + Tailwind）

### 7.1 JSON 端點

`POST /query`，內容 `{ "question": "…" }`，回傳：

```json
{
  "success": true,
  "answer": "…",
  "sql": "…",
  "columns": ["…"],
  "rows": [["…"]],
  "truncated": false,
  "geoJson": { "type": "FeatureCollection", "features": [] },
  "toolsCalled": ["query_database"],
  "error": null
}
```

- `geography` 以外的 `byte[]` 欄位一律轉成 `"<N bytes>"` 字串；`geography` 欄位轉為 GeoJSON，**不**把位元組直接序列化進 `rows`。
- 失敗時 `success=false`，`error` 為固定訊息（超過工具輪數、查詢失敗、模型服務出錯），不回傳堆疊追蹤、不回傳資料庫錯誤原文；模型服務出錯回 502，例外細節只寫入伺服器日誌。
- 這個端點同時給評估程式使用（可行性報告 §3 的「JSON 端點」），所以欄位名稱視為契約，不隨意更動。
- 問題長度設上限（預先決定 500 字元），超過回 400。

### 7.2 首頁

- 單頁：上方提問框；左側是 Leaflet 地圖，右側是「回答、生成的 SQL、結果表」。
- `geoJson` 非空才顯示地圖並 `fitBounds`；點位以圓點、多邊形以半透明填色；點擊要素顯示 `properties`。
- 底圖用 OpenStreetMap 圖磚並顯示出處（Attribution）。圖磚服務有使用政策，僅供本機展示等低流量情境；M3 不處理離線底圖。
- 樣式用 Tailwind CSS（2026-10-09 作者決定）：以官方獨立 CLI 編譯，不需要 Node；`Styles/app.css` 是輸入檔，編譯結果 `wwwroot/css/app.css` **入版控**（clone 後不需先編譯），重新編譯用 `tools/build-css.ps1`（CLI 下載到被忽略的 `tools/bin/`）。元件（輸入框、按鈕、卡片、表格、標籤）以 `@apply` 手寫，配色與圓角沿用 shadcn/ui 的語彙；**不使用 React 與 shadcn/ui 本身**（需要另開 Node 建置鏈，超出展示頁的必要範圍）。範本附的 Bootstrap、jQuery 已移除。
- Leaflet 以**本地檔案**放在 `wwwroot/lib`（不依賴 CDN；M6 的容器也不需對外連線載入腳本）。
- 結果表以純文字／`textContent` 寫入，**不用 `innerHTML` 拼接資料**，避免資料表內容中的字串變成頁面腳本。
- 範例問題按鈕 3–4 個（含一題質心、一題緩衝區），供 Demo 影片使用。

### 7.3 安全範圍（必須在 README／技術文件說明）

M3 的 Web 端點**沒有使用者驗證、角色、PII 遮蔽與稽核**，這些屬 M4。因此 M3 的 Web 僅供本機展示，不得直接對外部署；查詢結果可能含合成的個資欄位（`Customer`），Demo 影片避開這類題目。確定性的兩層防護（驗證器 + 唯讀 login）在 Web 路徑上照常生效。

### 7.4 設定

連線字串與金鑰沿用 M2 的 user-secrets／環境變數；`Web` 專案與 Eval、測試專案**共用同一個 `UserSecretsId`**（連線字串與金鑰只需設定一次），讀 `ConnectionStrings:Reader`、`Model` 區段，不新增新的機密格式。**不得**把金鑰寫進 `appsettings.json`。

### 7.5 驗證（S5 的完成條件）

- `dotnet run --project src/GeoNl2Sql.Web` 後，在瀏覽器問「質心」「緩衝區」「一般資料」三種題目，各自看到預期的地圖或資料表（作者手動確認，並截圖作 Demo 素材；截圖不放含機器路徑的畫面）。
- 離線測試（`tests/.../Web/QueryControllerTests.cs`，9 項）：控制器以假的代理程式測試序列化契約、問題長度上限、失敗路徑不洩漏例外細節。
- 以 `GeoJsonValidator` 檢查端點回傳的 `geoJson` 通過（G1 的端對端版本）。

### 7.6 實作紀錄（2026-10-09）

- `POST /query`（`Controllers/QueryController.cs`）以 `AgentRunner` 委派呼叫 `GeoAgent`，所以測試不需要模型與資料庫；`GeoAgent`、管線、執行器都是單例（每次請求在 `GeoAgent.RunAsync` 內建立新的 `GeoTools` 與 `MapResult`，已有並行測試）。
- 串流：新增 `POST /query/stream`（`Controllers/QueryStreamController.cs`），回應為逐行 JSON（NDJSON）：`step`（開始呼叫某工具）、`answer`（回答文字片段）、`result`（最後的完整 `QueryResponse`）、`error`（固定訊息）。Core 的 `GeoAgent.RunStreamingAsync` 送出 `ToolStarted`、`AnswerDelta`、`Completed` 三種事件；呼叫工具前的旁白不算回答，會被清掉。`POST /query` 維持原契約（評估與測試使用）。前端只用串流端點：等待時顯示「正在查詢資料庫…」等進度，回答文字逐段出現，結束時再畫 SQL、表格與地圖。
- 預設模型：`appsettings.json` 改為雲端（`Anthropic`／`claude-haiku-4-5`），與 §6 的「Web 預設走雲端」一致；本機模型用 `Model__Provider=Ollama`、`Model__ModelId=qwen2.5:3b` 覆寫。
- 離線測試 219 項全綠（原 205 + 9 + 串流 5）。
- 以 `claude-haiku-4-5` 對執行中的網站各問一題（每題一次請求）：質心 → `get_district_centroid`，地圖 1 個質心點；緩衝區 → `buffer_around_point`，地圖 1 個緩衝區加 5 座基地台；3500MHz 基地台 → `query_database`，結果表 47 列、空間欄位為固定文字、地圖 47 個點。三者 `success=true`，`geoJson` 皆可由前端繪製。空白問題回 400。
- 瀏覽器操作（點選範例、地圖彈出視窗）與截圖由作者確認。

---

## 8. S6：紀錄與整理

- `feasibility-report.md` 新增「§9 M3 實測紀錄」：套件版本與讀取結果（S1 前置驗證）、質心誤差（最大相對與絕對）、往返誤差最大值、緩衝區有效性與面積、空間索引計畫結論（含自然計畫是否使用索引）、工具選擇量測（樣本小的聲明）、已知限制；勾選 M3 驗收項目。
- `project-technical-guide.md`：`Spatial/`、`Agent/` 由規劃改為現況；Web 的啟動方式與端點；測試與 Eval 子命令的新內容；§7 限制補上 M3 Web 的安全範圍。
- `README.md`：**只加使用說明與技術亮點**（質心的 geometry 轉換法與 `EnvelopeCenter` 陷阱、GeoJSON 旁路設計、Web 的啟動方式與截圖），**不寫里程碑進度**。
- `docs/` 內不放含作者機器細節的內容（絕對路徑、登入帳號）。
- 不打 tag。

---

## 9. 執行順序、檢查點與待決事項

### 9.1 步驟

| 步驟 | 內容 | 驗證（做完要看到什麼） | 需要 | 預估 |
|---|---|---|---|---|
| S1 | 前置驗證、`SqlQueryResult` 加欄位型別、`GeoJsonBuilder`／`GeoJsonValidator` | 全部行政區與基地台轉出的 GeoJSON 100% 通過檢查器；既有 151 項離線測試仍綠 | 資料庫 | 1 日 |
| S2 | 質心、緩衝區、參數化執行、對照測試 | G2、G3 全過；`EnvelopeCenter` 反面測試過 | 資料庫 | 1.5 日 |
| S3 | 執行計畫檢查 | 每個查詢的索引使用結論已記錄 | 資料庫 | 0.5 日 |
| S4 | 三個工具、`GeoAgent`、迴圈上限、`tools.json` 與 `agent` 子命令 | 離線測試全綠；雲端 12 題結果已記錄 | 金鑰、資料庫 | 2 日 |
| S5 | Web 端點、首頁、Leaflet | 三種題目在瀏覽器可見；契約測試綠 | 金鑰、資料庫 | 1.5 日 |
| S6 | 文件與整理 | M3 驗收項目全勾；G5 回歸確認 | — | 0.5 日 |

S1 的前置驗證決定後續走哪條讀取路徑，**先做**。建議的 commit 切點：S1 一個、S2＋S3 一個、`tools.json` 凍結單獨一個（在 S4 量測之前）、S4 一個、S5 一個、S6 一個。

### 9.2 需要作者決定的事

| # | 問題 | 建議 | 影響 |
|---|---|---|---|
| Q1 | Agent Framework 工具迴圈在 M3 啟用，工具限三個（§6.1） | **同意**：這是 M2 計畫 Q2 預告的時間點；工具再多就超出 M3 範圍 | S4 |
| Q2 | 質心「誤差 < 0.1%」的分母：外接框對角線長度（§4.1），同時記錄絕對公尺 | **採用**：可行性報告未寫分母，需要明定才可驗證 | S2 的測試門檻 |
| Q3 | Leaflet 以本地檔案放入儲存庫，而非 CDN | **本地**：容器與離線展示較穩；代價是儲存庫多一份第三方檔案（授權檔一併放入） | S5 |
| Q4 | 圖磚使用 OpenStreetMap 並標註出處，僅限本機展示 | **同意**；若日後公開部署需改用自架或付費圖磚 | S5 |
| Q5 | M3 不打 tag | **同意**：可行性報告 §5 只在 M2、M4 打 tag | S6 |
| Q6 | 本機 `qwen2.5:3b` 的工具選擇只量不評 | **同意**：與 M1／M2 對本機模型的處理一致 | S4 |

**Q1～Q6 作者皆已確認（2026-10-09），照上表「建議」欄執行。**

---

## 10. 風險與預先決定

| 風險 | 影響 | 預先決定 |
|---|---|---|
| `NetTopologySuite.IO.SqlServerBytes` 在 .NET 9 不可用，或座標順序／環方向與預期不同 | S1 卡住，後續全部延後 | S1 一開始就做前置驗證；工具路徑已用 `ToString()` 繞開；模型 SQL 路徑的備案見 §3.1 第 3 點，不引入需要原生元件的 `Microsoft.SqlServer.Types` |
| 質心在行政區邊界不規則時與投影後參考值的差距超過門檻 | G2 不過 | 照實記錄實測值與哪個行政區；平面質心與投影後質心本來就不是同一個量，門檻若不合理，在實測紀錄說明並與作者討論，不偷偷放寬 |
| 合成資料量小，最佳化工具不用空間索引 | G4 宣稱不成立 | §5：記錄自然計畫，再以探測提示證明可被使用；兩者分開寫 |
| 本機 3B 模型無法穩定呼叫多個工具 | 本機軌 Demo 不可用 | 只記錄；Web 預設走雲端；R3 的純文字降級備案不在 M3 內實作 |
| 模型對質心／緩衝區的參數填錯（經緯度對調、半徑單位） | 地圖顯示錯誤位置 | 工具說明寫明順序與單位；參數範圍檢查；`tools.json` 的預期參數涵蓋這類題目 |
| 工具迴圈無上限或模型反覆呼叫 | 費用與延遲失控 | §6.2 的輪數上限與離線測試；費用控管沿用 R5 |
| Web 端點被誤當成可對外部署 | 無驗證、無遮蔽的資料外洩 | §7.3：文件明講僅限本機；M4 補驗證、角色、遮蔽、稽核 |
| 資料表內容渲染成頁面腳本 | 前端注入 | §7.2：一律以 `textContent` 寫入，並有測試覆蓋含 `<script>` 的欄位值 |
