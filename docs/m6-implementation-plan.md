# M6 實作計畫：容器化與 CI

| 項目 | 內容 |
|---|---|
| 讀者 | 開發者（作者本人與日後的貢獻者） |
| 文件日期 | 2026-10-12 |
| 里程碑定位 | 讓沒有 SQL Server 與 .NET 的人以 `docker compose up` 啟動網頁與資料庫；每次 push 自動跑測試，並以 `eval --replay` 確認公布的評估數字仍可逐題重現 |
| 預估工時 | 約 3 人日（模組 12） |
| 完成後 | README 的「快速開始（Docker）」與 CI 徽章；建議打 `v0.4.0`（§8.2 Q5） |
| 狀態 | **進行中（2026-10-13）**：Q1～Q6 採建議值，各步驟開始前再向作者確認；S1、S2、S3 已完成（PR #1 的 CI 三個 job 全綠） |

---

## 1. 目標與驗收

來自 `feasibility-report.md` §5 的 M6 驗收：

| # | 驗收條件 | 對應步驟 |
|---|---|---|
| K1 | `docker compose up` 後 App + SQL Server 可用，並自動建庫與灌入合成資料 | S2 |
| K2 | 服務拆分：`sqlserver`（`MSSQL_PID=Express`、`ACCEPT_EULA=Y`、healthcheck）→ `seed`（一次性，完成即退出）→ `web`（等 seed 成功完成） | S2 |
| K3 | `.env.example` 列出密碼欄位並註明複雜度規則；`.env` 不 commit | S2 |
| K4 | 連線字串與金鑰走環境變數；本機開發路徑（user-secrets）不受影響 | S2 |
| K5 | seed 的管理連線可由環境變數改為 SA 帳密（**已確認不需改程式**，§2.1） | S2 |
| K6 | Ollama 做成 compose 的選用 profile，預設不啟動 | S2 |
| K7 | README 快速開始：`cp .env.example .env` → 填密碼 → `docker compose up` → 開瀏覽器；註明 SQL Server 映像只有 amd64 | S4 |
| K8 | 不附資料檔：seed 於容器啟動時以固定種子產生資料，內容雜湊可重現 | S2 |
| K9 | App 容器可連主機的 Ollama；文件說明 GPU 直通的取捨 | S2、S4 |
| K10 | GitHub Actions：建置、單元測試、AST 驗證攻擊集、離線護欄測試全部通過；需要真實模型的評估不放在 CI | S3 |

本計畫自訂四條：

| # | 條件 | 理由 |
|---|---|---|
| K11 | **容器跑的是同一個東西**：對容器內的資料庫跑資料庫測試 159 項全過、`eval --replay` 的 6 份結果與公布的逐題相同、seed 的 6 張表雜湊與本機相同 | 「起得來」不等於「結果相同」。§2.2 的三個差異都會讓容器的結果悄悄偏離 M5 的數字 |
| K12 | **CI 不用任何 secret、不呼叫模型**：資料庫密碼每次執行時隨機產生；`eval --replay` 只讀快取 | 公開儲存庫的 PR 可能來自他人；不放 secret 就沒有外洩可能，也不產生費用 |
| K13 | **最小權限延伸到容器**：只有 `sqlserver` 與 `seed` 拿到 SA 密碼，`web` 只拿三個受限登入；`web` 以非 root 執行；連接埠只綁 `127.0.0.1`；映像內沒有密碼與金鑰 | 這是資安主題的作品，部署設定本身也會被檢視 |
| K14 | **不動受測物**：`GeoNl2Sql.Core` 與 Web 的行為不改。S1 若發現只在 Linux 出現的錯誤，先寫測試重現再修，並在紀錄中揭露；修正後 system 訊息必須與 M5 相同（`eval --replay` 會檢查） | 與 M5 的 K6 相同：邊做邊改，就不知道數字屬於哪個版本 |

**M6 刻意不做**（R1）：

- 推映像到 registry（GHCR）、Kubernetes／Helm、雲端部署、HTTPS 反向代理（README 已寫明「請勿對外部署」）。
- 在 CI 呼叫真實模型、排程跑 `eval --fresh`。
- CodeQL、Dependabot、CI 內的 gitleaks（已有 pre-commit 的 gitleaks 與 GitHub secret scanning）。
- 改回 `net10.0`（另案；容器沿用 9.0 映像）。
- SQL Server 的 arm64 方案；Ollama 的 GPU 直通只寫文件、不放進預設設定。
- 資料與稽核紀錄跨重啟保留（Q2）。
- CI 的建置快取與加速。

---

## 2. 現況與設計

### 2.1 已確認的事實（2026-10-12 實測）

- **seed 的管理連線**：`SeedCommand` 把 `ConnectionStrings:Demo` 的資料庫換成 master，用來重建資料庫與建立三個 login（`SeedCommand.cs:43`）；Eval 已載入環境變數來源。容器內只要把 `ConnectionStrings__Demo` 設成 SA 帳密即可，**不需改程式**（K5）。
- **Web 只需要三個受限登入**：只讀 `Reader`、`ReaderPii`、`Auditor`，不需要 `Demo`。
- **沒有金鑰也能啟動**：Web 選 Anthropic 但沒有金鑰時，啟動不會失敗；模型用戶端是延遲建立的單例，第一次提問才回錯誤。所以 `docker compose up` 後一定看得到頁面。
- **測試**：離線 408 項、資料庫 159 項，共 567 項，本機全部通過。Web 的測試直接建立 Controller，不經 `WebApplicationFactory`，所以離線測試不需要任何連線字串。
- **本機 SQL Server**：2022 RTM（16.0.1000.6）Express，伺服器與資料庫的定序都是 `Chinese_Taiwan_Stroke_CI_AS`。
- **Docker**：29.8.2，linux/amd64。
- **`.gitignore`**：`.env.*` 會連 `.env.example` 一起忽略，需加例外 `!.env.example`。

### 2.2 會讓容器、CI 或全新 clone 結果不同的三個差異

| 差異 | 實測 | 影響 | 處理 |
|---|---|---|---|
| **換行** | 在 Windows（`core.autocrlf=true`）全新 clone 時，`PromptBuilder.cs` 取出為 CRLF。C# 原始字串常值會保留原始碼的換行，所以 system 訊息多出 `\r`。結果是離線測試 2 項失敗（`PromptBuilderTests` 的兩個快照）。作者目前的工作目錄是 LF，所以全過 | 快取鍵含完整 system 訊息，`eval --replay` 會整批找不到快取；網頁送給模型的提示詞也與量測時不同。**v0.3.0 的重現說明在 Windows 全新 clone 上不成立**；Linux／macOS 不受影響 | S1：加 `.gitattributes`（`* text=auto eol=lf`）。已在暫存 clone 實測：加上後沒有 CRLF 檔，離線 408 項全過 |
| **定序** | SQL Server Linux 映像的預設定序是 `SQL_Latin1_General_CP1_CI_AS`。快取裡有不帶 `N` 的中文字串，例如 `WHERE d.DistrictName = '中央區'`：雲端每輪 8 個回應，本機每輪 5 個 | 本機定序用 950 字碼頁，這種字串能正確比對；Latin1 會把它變成 `'???'`，查無資料，這些題由對變錯。網頁的回答也會一樣錯 | compose 與 CI 都設 `MSSQL_COLLATION=Chinese_Taiwan_Stroke_CI_AS`；README 寫明評估數字依賴這個定序（是否實測差異見 Q4） |
| **SQL Server 版本** | 本機是 2022 RTM；Linux 映像有各 CU 版本 | 最佳化器可能不同：`SpatialIndexPlanTests`（M3：自然計畫不用空間索引）與資料庫耗時可能改變 | 固定映像標籤（Q1），以 K11 檢查；有差異就照實記錄，不為了變綠而改測試 |

### 2.3 Compose

```
sqlserver ──healthy──► seed（重建資料庫、建 login、灌資料，結束代碼 0）──completed──► web
                                                         （選用）ollama，--profile ollama
```

草稿（`compose.yaml`；連線字串重複的部分實作時以 YAML anchor 整理）：

```yaml
services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-<固定標籤>      # Q1
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: Express
      MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD:?請在 .env 設定}
      MSSQL_COLLATION: Chinese_Taiwan_Stroke_CI_AS             # §2.2
    ports: ["127.0.0.1:14333:1433"]
    healthcheck:
      test: /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$$MSSQL_SA_PASSWORD" -Q "SELECT 1" -b -o /dev/null
      interval: 5s
      retries: 30
  seed:
    build: { context: ., target: seed }
    depends_on: { sqlserver: { condition: service_healthy } }
    environment:
      ConnectionStrings__Demo: Server=sqlserver;Database=GeoNl2SqlDemo;User ID=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True
      ConnectionStrings__Reader: …User ID=geo_reader;Password=${GEO_READER_PASSWORD:?…}…
      ConnectionStrings__ReaderPii: …
      ConnectionStrings__Auditor: …
  web:
    build: { context: ., target: web }
    depends_on: { seed: { condition: service_completed_successfully } }
    ports: ["127.0.0.1:8080:8080"]
    environment:                                               # 沒有 SA（K13）
      ConnectionStrings__Reader: …
      ConnectionStrings__ReaderPii: …
      ConnectionStrings__Auditor: …
      Model__Provider: ${MODEL_PROVIDER:-Anthropic}
      Model__ModelId: ${MODEL_ID:-claude-haiku-4-5}
      Model__ApiKey: ${MODEL_API_KEY:-}
      Model__OllamaEndpoint: ${OLLAMA_ENDPOINT:-http://host.docker.internal:11434}
    extra_hosts: ["host.docker.internal:host-gateway"]          # Linux 的 Docker Engine 也能連主機
  ollama:
    image: ollama/ollama:<固定版本>
    profiles: [ollama]
    volumes: [ollama:/root/.ollama]
volumes:
  ollama: {}
```

設計決定：

- **連接埠只綁 `127.0.0.1`**。網頁會用作者自己的金鑰呼叫雲端模型，綁 `0.0.0.0` 等於讓區網內任何人花你的錢。
- **資料庫開 `127.0.0.1:14333`**：避開本機可能已有的 1433；讓主機上的 `dotnet test`、`eval --replay`、SSMS 能連到容器的資料庫。也就是說，沒有 SQL Server 的人可以拿容器當資料庫來重現評估。
- **四個密碼**（SA、`geo_reader`、`geo_reader_pii`、`geo_auditor`）；以 `${VAR:?}` 寫法，缺少時立即停止並指出缺哪一個。`.env.example` 註明 SQL Server 的密碼規則：至少 8 碼，且包含大寫、小寫、數字、符號四類中的三類。另外不可含 `;`、`'`、`"`、`$`、空白，這些字元會破壞連線字串或被 compose 當成變數。
- **密碼從哪裡來（本機與 CI 不同）**：

  | 情境 | 密碼來源 | 每次啟動是否相同 |
  |---|---|---|
  | 本機 `docker compose up`（作者或任何 clone 的人） | 使用者第一次把 `.env.example` 複製成 `.env` 後自己填寫；`compose.yaml` 讀 `.env` 組出連線字串 | **相同**。要換密碼就改 `.env` 再 `up`：SQL Server 容器重建時套用新的 SA 密碼，seed 以 `CREATE`／`ALTER LOGIN` 把三個登入改成新密碼 |
  | CI（GitHub Actions） | 每次執行開始時隨機產生，寫成 runner 上暫時的 `.env`，並在紀錄中遮蔽（§2.5） | **每次不同**，執行結束時隨容器一起刪除 |

  `.env` 不進版控（`.gitignore` 已排除）。
- **`TrustServerCertificate=True`**：容器的 SQL Server 使用自簽憑證，連線只走 compose 內部網路與 `127.0.0.1`。
- **seed 每次 `up` 都重建資料庫，不掛資料 volume**（Q2）：
  - 掛了也沒有好處：seed 每次都刪掉資料庫、以固定種子重建，有沒有 volume，啟動後的資料都相同；唯一會清掉的稽核紀錄，有 volume 也一樣會被 seed 清掉。
  - 掛了反而有兩個問題：
    - `MSSQL_SA_PASSWORD` 只在第一次建立資料目錄時套用。之後改 `.env` 的 SA 密碼，volume 裡還是舊密碼，healthcheck 一直失敗且看不出原因，使用者得知道要先 `docker compose down -v`。
    - 若為了保留資料而讓 seed「已存在就跳過」，之後 `db/` 的腳本有改，舊資料庫不會更新，與新程式不一致。
  - 代價：`docker compose down` 後資料庫消失（下次 `up` 由 seed 在幾秒內重建）；稽核紀錄只保留到下次重啟，與 README 現有的說法「seed 時稽核紀錄一併清掉」一致。
  - 只有要展示「稽核紀錄跨重啟保留、以 ledger 驗證歷史」時，才需要 volume 加上 seed 的「已存在就跳過」，屬於新功能，不在 M6。
  - S2 要確認 SQL Server 映像本身有沒有宣告 `VOLUME`：有的話 compose 會自動建匿名 volume，同樣會出現舊 SA 密碼的問題，需明確處理。
- **模型**：預設 Anthropic；沒有金鑰的人把 `MODEL_PROVIDER` 改成 `Ollama`、`MODEL_ID` 改成 `qwen2.5:3b`。
- **Ollama**：建議原生跑在主機上（可行性報告 §1.4；Windows 上直接用得到 GPU）。`--profile ollama` 提供容器版，模型存在具名 volume，第一次需執行 `docker compose exec ollama ollama pull qwen2.5:3b`，並把 `OLLAMA_ENDPOINT` 改成 `http://ollama:11434`。容器內用 GPU 需要 NVIDIA 驅動、WSL2 與 compose 的 `deploy.resources.reservations.devices`，只寫進文件，不放進預設設定。

### 2.4 映像（`Dockerfile`）

- 一個 Dockerfile、兩個最終階段：`sdk:9.0` 建置並 publish Web 與 Eval，`web` 階段用 `aspnet:9.0`，`seed` 階段用 `runtime:9.0`（入口為 `GeoNl2Sql.Eval.dll seed`）。
- 用 Debian 基底，不用 Alpine、chiseled 或 globalization invariant 模式：SqlClient 與 Bogus 的 `zh_TW` 資料需要 ICU。
- 以映像內建的非 root 使用者執行（`USER $APP_UID`）。
- `.dockerignore` 排除 `.git`、`**/bin`、`**/obj`、`.env`、`eval/results`、`docs`、`tools/bin`。排除 `.env` 是為了不把密碼帶進建置；排除 `bin`／`obj` 是為了不讓本機的 Windows 建置結果混進容器建置。
- 要確認：`db/schema-description.md` 有出現在 Web 的 publish 輸出裡（目前以 `CopyToOutputDirectory` 複製）。

### 2.5 CI（`.github/workflows/ci.yml`）

```yaml
on: { push: { branches: [main] }, pull_request: {} }
permissions: { contents: read }
jobs:
  offline:                         # ubuntu 與 windows 各一（Q3）
    steps: checkout → setup-dotnet（global-json-file）→ dotnet test --filter "Category!=Database"
  integration:                     # ubuntu；needs: offline
    steps:
      - 產生 .env：四個密碼每次隨機產生並以 ::add-mask:: 遮蔽（K12）
      - docker compose up -d --build
      - 等 seed 以結束代碼 0 結束、web 回 200
      - dotnet test --filter "Category=Database"   # 以環境變數連 127.0.0.1,14333
      - eval --replay                               # 結束代碼 0 = 6 份結果與公布的逐題相同
      - 失敗時輸出 docker compose logs；最後一律 docker compose down -v
```

- `offline` 已涵蓋 K10 要求的項目：AST 驗證攻擊集（`SqlValidatorTests` 60 條）、離線護欄、評估的完整性與報告測試。
- `windows` 那一份的 checkout 預設就是 CRLF，正好守住 §2.2 的換行問題，避免日後退回。
- `integration` 讓 K11 每次 push 都檢查一次：資料庫測試和評估重播在乾淨的 Linux 與容器資料庫上仍然成立。
- 第三方 action 以完整 commit SHA 固定版本，並在旁邊註明版本號。
- CI 的密碼不用固定值，原因有兩個：寫死在 workflow 檔裡就等於公開在儲存庫上（gitleaks 也會擋）；改用 repo secret 需要作者另外設定，而且他人 fork 後發的 PR 讀不到 secret，整合 job 會跑不起來。每次隨機產生，就沒有東西會外洩，也沒有東西要管理。

### 2.6 新增與修改的檔案

| 檔案 | 內容 |
|---|---|
| `.gitattributes`（新） | `* text=auto eol=lf`（§2.2） |
| `Dockerfile`、`.dockerignore`（新） | §2.4 |
| `compose.yaml`、`.env.example`（新） | §2.3 |
| `.gitignore`（改） | 加 `!.env.example` |
| `.github/workflows/ci.yml`（新） | §2.5 |
| `README.md`（改） | 快速開始（Docker）、CI 徽章、定序說明、執行測試一節加上容器資料庫的用法；英文段同步 |
| `docs/project-technical-guide.md`、`docs/feasibility-report.md`（改） | §6 |

`src/` 不改（K14）。

---

## 3. S1：換行與 Linux 可攜性

這一步放在最前面，因為 Docker 建置與 CI 都建立在「任何地方取出的原始碼都相同」之上。

1. 重現：在暫存目錄全新 clone，離線測試失敗 2 項（規劃時已做過一次，正式執行時再做一次並記錄）。
2. 加 `.gitattributes`，執行 `git add --renormalize .`。索引裡已全是 LF，預期除了 `.gitattributes` 之外沒有內容變更，用 `git diff --cached --stat` 確認。
3. 作者工作目錄中目前有 34 個 CRLF 檔，重新取出成 LF。工作目錄是乾淨的，內容不變。
4. 在 Linux 跑離線測試：用 `sdk:9.0` 容器，唯讀掛載儲存庫，在容器內 clone 後建置，不污染本機的 `bin`／`obj`。容器裡沒有 user-secrets，正好模擬 CI。若因文化特性、時區或路徑大小寫而失敗，在測試或 Eval 修正；涉及 Core 的依 K14 處理。

**完成條件**：Windows 全新 clone 離線 408 項全過；`git ls-files --eol` 沒有 `w/crlf`；Linux 容器內離線 408 項全過。

---

## 4. S2：映像與 Compose

依 §2.3、§2.4 建立檔案後，從零開始驗證（沒有既有的容器與 volume）：

| 檢查 | 預期 |
|---|---|
| `docker compose up --build` | seed 結束代碼 0；印出的 6 張表雜湊與本機 seed 相同（K8）；`http://127.0.0.1:8080` 回 200 |
| 定序 | `SELECT SERVERPROPERTY('Collation')` 為 `Chinese_Taiwan_Stroke_CI_AS` |
| K11 | 在主機以環境變數指向 `127.0.0.1,14333`：資料庫測試 159 項全過；`eval --replay` 結束代碼 0 |
| 端到端一題 | 網頁以主機的 Ollama 問一題（免費），回答與地圖正常，稽核表多一筆（K9）。是否另加雲端一題見 Q6 |
| K13 | `docker compose exec web id -u` 不是 0；`docker image inspect` 與 `docker history --no-trunc` 找不到密碼與金鑰；web 容器的環境變數沒有 SA 密碼 |
| 沒有殘留狀態 | SQL Server 映像沒有宣告 `VOLUME`（`docker image inspect`），或已明確處理（§2.3）；改 `.env` 的 SA 密碼後重新 `up` 可正常啟動 |
| 定序實驗（Q4） | 已完成，結果見 §8.2 Q4 說明（雲端少 8 題、本機少 3 題） |
| 設定錯誤 | 缺少密碼時立即停止並指出變數名稱；SA 密碼不符規則時的實際錯誤訊息記錄進 README |
| `--profile ollama` | 容器版 Ollama 可啟動，web 改連它也能回答 |

---

## 5. S3：GitHub Actions

1. 依 §2.5 建立工作流程，推到一個分支（**需作者同意 push**），確認三個 job 全綠；`integration` 的紀錄要看得到資料庫測試 159 項通過與 replay「逐題相同」。
2. 確認關卡真的會擋：在本機把快取裡一題的回應改壞後跑 `eval --replay`，確認結束代碼為 1（CI 以結束代碼判定成敗）。確認後還原。
3. 記錄各 job 的實際耗時；`integration` 目標在 15 分鐘內。

---

## 6. S4：文件與發佈

- **README**：在「環境需求」前新增「快速開始（Docker）」，內容依序為：
  - 步驟：`cp .env.example .env` → 填密碼與金鑰 → `docker compose up` → 開 `http://127.0.0.1:8080`。
  - 沒有金鑰時改用 Ollama。
  - Docker 至少要分配 2 GB 記憶體。
  - 連接埠衝突時怎麼改。
  - SQL Server 映像只有 amd64，Apple Silicon 需 Rosetta，速度較慢。
  - GPU 直通的取捨。

  另外的修改：
  - 「評估結果」一節：寫明數字依賴 `Chinese_Taiwan_Stroke_CI_AS` 定序。
  - 「執行測試」一節：加上以容器當資料庫的做法。
  - 加 CI 徽章。
  - 英文段同步。
  - 不寫里程碑進度。
- **`project-technical-guide.md`**：新增部署與 CI 一節，並在金鑰表加上 `.env`。
- **`feasibility-report.md`**：新增「§12 M6 實測紀錄」，內容包括 §2.2 三個差異的處理與結果、映像標籤、CI 耗時，並勾選 M6 驗收。
- 全部測試通過；作者確認後打 tag（Q5）。

---

## 7. 費用

| 項目 | 雲端呼叫 |
|---|---|
| S1～S4 的所有驗證（測試、replay、端到端用本機 Ollama） | 0 |
| 選用：容器內雲端一題（Q6） | 約 2～3 次，約 5 千輸入 token（低於 0.01 美元） |
| CI（公開儲存庫的 GitHub 託管 runner） | 0；不需要金鑰 |

---

## 8. 執行順序、檢查點與待決事項

### 8.1 步驟

| 步驟 | 內容 | 驗證（做完要看到什麼） | 需要 | 預估 |
|---|---|---|---|---|
| S1 | `.gitattributes`、Linux 離線測試 | §3 完成條件 | Docker | 0.5 日 |
| S2 | Dockerfile、compose、`.env.example` | §4 全部檢查 | Docker、主機 Ollama | 1.25 日 |
| S3 | GitHub Actions | §5 | 作者同意 push 分支 | 0.75 日 |
| S4 | README、技術文件、實測紀錄、tag | §6 | 作者確認 tag | 0.5 日 |

建議的 commit 切點：S1 一個（只有 `.gitattributes`，加上可能的 Linux 修正）；S2 一個；S3 一個；S4 一個。

### 8.2 需要作者決定的事

| # | 問題 | 建議 | 影響 |
|---|---|---|---|
| Q1 | SQL Server 映像版本 | 固定為 2022 當時最新的 CU 標籤，並寫進實測紀錄。另一個選項 2022 RTM 與本機完全相同，但映像基於已停止支援的 Ubuntu 20.04，也沒有 CU 的安全修補。差異由 K11 檢查 | S2 |
| Q2 | 每次 `up` 是否重建資料庫、要不要掛資料 volume | 每次重建，不掛 volume。理由見 §2.3：掛了資料也一樣，反而會保留舊的 SA 密碼；稽核紀錄隨重啟清空。另一個選項是掛 volume，並讓 seed 在資料庫已存在時跳過，目的是讓稽核紀錄跨重啟保留；需要改 seed，`db/` 腳本更新時也要使用者自己 `down -v` | S2 |
| Q3 | CI 範圍 | 離線測試跑 ubuntu 與 windows；整合 job 跑 compose、資料庫測試與 `eval --replay`。另一個選項是只跑離線測試，較快但無法守住 K11 | S3 |
| Q4 | 是否實測定序的影響 | 做（約 15 分鐘、0 費用），說明見表下 | S2、S4 |
| Q5 | 完成後打 `v0.4.0` | 同意，打 tag 前仍請作者確認。S1 修正了 v0.3.0 在 Windows 全新 clone 上無法 replay 的問題；若想讓這個修正早點出現在 tag 上，可在 S1 後先打 `v0.3.1` | S4 |
| Q6 | 端到端驗證用哪個模型 | 用主機的 Ollama（免費）。是否另加雲端一題（約 2～3 次呼叫）由作者決定 | S2 |

**Q4 說明：定序實驗**

- **要回答的問題**：M5 公布的準確率，有多少是靠「伺服器剛好是中文定序」才算對。§2.2 只知道快取裡有不帶 `N` 的中文字串（雲端每輪 8 個回應、本機每輪 5 個），還不知道實際會錯幾題。有些回應可能是中途被修正掉的嘗試，有些題即使查無資料也剛好算對。標準 SQL 都有加 `N`，不受影響。
- **做法**：
  1. 另起一個使用預設 Latin1 定序的 SQL Server 容器（另一個連接埠），以 seed 灌入相同資料。
  2. 對它跑 `eval --replay`。只用快取的模型回應，0 次模型呼叫。
  3. replay 會逐題與公布的結果比對並列出不同的題目（因此結束代碼為 1，這是預期的）。記錄雲端與本機各有幾題、哪幾題由對變錯。
  4. 刪除這個容器。
- **不影響正式數字**：結果寫在不進版控的 `eval/results/m5-replay/`。
- **用途**：
  - README 的已知限制可以寫成「換成 Latin1 定序時，雲端少 N 題、本機少 M 題」，而不只是「依賴定序」。
  - 多一個真實的失敗類型：模型習慣寫不帶 `N` 的中文字串，平常被伺服器定序掩蓋，與 M1 的 `LIMIT`、CTE 等同屬 T-SQL 方言問題。

**Q4 實測結果（2026-10-13）**：Latin1 容器（`SQL_Latin1_General_CP1_CI_AS`）以 seed 灌入後，6 張表雜湊與中文定序版完全相同（資料欄位是 `nvarchar`，資料本身不受影響）。對它跑 `eval --replay`（0 次模型呼叫，結束代碼 1），3 輪結果一致：

| 模型 | 公布（有修正） | Latin1 | 少 | 由對變錯的題目 |
|---|---|---|---|---|
| 雲端 claude-haiku-4-5 | 55／60（91.7%） | 47／60（78.3%） | 8 | J01、J02、J04、J06、J07、J10、J13、G09 |
| 本機 qwen2.5:3b | 22／60（36.7%） | 19／60（31.7%） | 3 | J01、J02、J06 |

無修正：雲端 55→47，本機 21→18。受影響的題目幾乎都是含中文地名的 J 類。compose 與 CI 因此必須設中文定序，README 要明寫。
  - 為 compose 必須設定 `MSSQL_COLLATION` 提供數字佐證。
- **不做的事**：不為此改提示詞（例如加一句「中文字串用 `N'…'`」），那會改到受測物（K14）；只記錄，留到之後決定。
- **不做這個實驗的話**：README 只寫「評估數字依賴 `Chinese_Taiwan_Stroke_CI_AS` 定序」，沒有數字。

---

## 9. 風險與預先決定

| 風險 | 影響 | 預先決定 |
|---|---|---|
| CI runner 較慢，觸發 10 秒查詢逾時 | replay 出現原本沒有的 `exec_error`，CI 變紅 | 先觀察，不預先調高限制。若發生，逐題確認是逾時，照實記錄後再決定（調高限制會改變量測條件，需寫明） |
| 換 CU 後最佳化器行為改變 | `SpatialIndexPlanTests` 或 replay 不同 | 照實記錄；判斷是否為測試本身綁了特定版本的計畫，不為了變綠改測試（K14） |
| SQL Server 映像自行宣告 `VOLUME` | compose 自動建立匿名 volume，改 `.env` 的 SA 密碼後 healthcheck 一直失敗 | S2 以 `docker image inspect` 確認；有宣告時明確處理，並在 README 說明 `docker compose down -v` |
| 密碼含特殊字元或不符規則 | 連線字串被截斷，或容器啟動失敗 | `.env.example` 寫明規則與禁用字元；`${VAR:?}` 提早報錯；README 附實際錯誤訊息 |
| Docker 記憶體不足 2 GB | SQL Server 容器起不來 | README 寫明需求 |
| 連接埠 8080／14333 已被占用 | `up` 失敗 | README 說明如何修改對應的連接埠 |
| `docker inspect` 看得到環境變數裡的密碼與金鑰 | 本機上其他能操作 Docker 的人可取得 | 只適合本機示範，README 寫明；不改用 Docker secrets（需改程式讀檔，超出範圍） |
| `sdk:9.0` 映像的 SDK 不符 `global.json`（9.0.306，`latestFeature`） | 建置失敗 | S2 驗證；不符時固定 SDK 映像標籤 |
| 建置時把本機檔案帶進映像 | 密碼或快取進到映像 | `.dockerignore`；S2 以 `docker history` 檢查（K13） |
| Apple Silicon 執行 amd64 的 SQL Server | 慢或不穩 | README 註明；不另做 arm64 方案 |
