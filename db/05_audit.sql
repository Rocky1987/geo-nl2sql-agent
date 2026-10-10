/*
 * 05_audit.sql — M4 稽核：audit.QueryLog（append-only ledger 資料表）與寫入專用的 geo_auditor
 *
 * 前提：伺服器層級的 login geo_auditor 已存在（由 seed 依 ConnectionStrings:Auditor 建立）。
 * 執行方式：seed 在 04_reader_pii.sql 之後自動執行；手動執行：
 *       sqlcmd -S .\SQLEXPRESS -E -b -d GeoNl2SqlDemo -f 65001 -i db/05_audit.sql
 *
 * 設計（docs/m4-implementation-plan.md §5.1）：
 *   - 資料表是 append-only ledger：寫入後連擁有者都不能 UPDATE／DELETE／TRUNCATE，竄改可由 ledger 摘要驗證。
 *   - geo_auditor 只有這張表的 INSERT，讀不到任何資料（含稽核表本身）。
 *   - geo_reader、geo_reader_pii 對 audit 結構描述 DENY：就算模型被誘騙寫出 SQL，也碰不到稽核紀錄。
 *   - 不存結果的資料列，避免稽核表本身變成個資外洩點。
 * seed 重建資料庫時稽核紀錄一併清掉（示範環境）。
 */

CREATE SCHEMA audit AUTHORIZATION dbo;
GO

CREATE TABLE audit.QueryLog (
    LogId               bigint         IDENTITY(1,1) NOT NULL,
    OccurredAt          datetime2(3)   NOT NULL,           -- UTC
    DurationMs          int            NOT NULL,           -- 整個請求耗時
    [Role]              nvarchar(50)   NOT NULL,           -- 請求選擇的角色（自選，非驗證過的身分；不合法的請求記原文）
    Question            nvarchar(500)  NOT NULL,           -- 原文，超過 500 字元截斷
    Outcome             varchar(20)    NOT NULL,           -- success／failed／blocked_input／blocked_output／limit／model_error／invalid
    GuardRule           varchar(100)   NULL,               -- 命中的偵測規則或輸出防護原因
    ToolCalls           nvarchar(max)  NULL,               -- 呼叫過的工具與參數（JSON）
    [Sql]               nvarchar(max)  NULL,               -- 最後一次 query_database 的 SQL
    Attempts            int            NULL,               -- 該次 NL2SQL 的嘗試次數
    [RowCount]          int            NULL,
    Truncated           bit            NULL,
    ScrubbedCells       int            NOT NULL,           -- 工具結果清理掉的儲存格數
    ModelCalls          int            NOT NULL,           -- 本次請求所有模型呼叫的合計
    InputTokens         bigint         NULL,               -- 供應商沒回報用量時為 NULL（不估算）
    OutputTokens        bigint         NULL,
    Provider            varchar(30)    NOT NULL,
    ModelId             varchar(100)   NOT NULL,
    AgentPromptVersion  varchar(20)    NOT NULL,
    Nl2SqlPromptVersion varchar(20)    NOT NULL,
    DetectorVersion     varchar(20)    NULL,
    AnswerExcerpt       nvarchar(500)  NULL,               -- 回答前 500 字元（模型只看過遮蔽值）
    CONSTRAINT PK_QueryLog PRIMARY KEY CLUSTERED (LogId)
) WITH (LEDGER = ON (APPEND_ONLY = ON));
GO

CREATE USER geo_auditor FOR LOGIN geo_auditor WITH DEFAULT_SCHEMA = audit;
GO

GRANT INSERT ON audit.QueryLog TO geo_auditor;
-- 只寫不讀：連自己寫的紀錄也讀不到。
DENY SELECT, UPDATE, DELETE ON SCHEMA::audit TO geo_auditor;
GO

-- reader 帳號只有在 seed 設定了對應連線字串時才存在，所以用動態 SQL 判斷後再 DENY。
IF USER_ID('geo_reader') IS NOT NULL EXEC (N'DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::audit TO geo_reader');
IF USER_ID('geo_reader_pii') IS NOT NULL EXEC (N'DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::audit TO geo_reader_pii');
GO
