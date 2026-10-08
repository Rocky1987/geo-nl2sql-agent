/*
 * 01_schema.sql — GeoNl2SqlDemo 的資料表結構（情境：電信基地台，全為合成資料）
 *
 * 用法：
 *   - 一般由 `dotnet run --project eval/GeoNl2Sql.Eval -- seed` 執行：seed 先連 master 重建資料庫
 *     GeoNl2SqlDemo，再以 GO 切批執行本檔。
 *   - 手動執行：先自行建立空的 GeoNl2SqlDemo 並切換到該資料庫（USE GeoNl2SqlDemo），再執行本檔。
 *   - 以 sqlcmd 執行時請加 -f 65001（本檔為 UTF-8，含中文註解）：
 *       sqlcmd -S .\SQLEXPRESS -E -b -d GeoNl2SqlDemo -f 65001 -i db/01_schema.sql
 *   - 本檔不含 CREATE DATABASE / DROP DATABASE，因此不會動到同一個執行個體上的其他資料庫。
 *
 * 內容：6 張資料表、2 個空間索引、spike 專用唯讀使用者 spike_reader。
 * 資料表建立順序即外鍵相依順序（被參照者在前）。
 *
 * geography 慣例（SRID 4326 = WGS84）：
 *   - WKT 是 POINT(經度 緯度)；geography::Point(緯度, 經度, 4326) 的參數順序則是「緯度在前」。
 *   - 多邊形外環須為逆時針；方向相反會被解讀成「地球上除了這塊以外的全部」。
 *   - geography 沒有 STCentroid()。
 *
 * 刻意保留的命名不佳欄位（用於測試「補上欄位說明」是否提升準確率）：
 *   Customer.flg1、Subscription.dt2。它們的意義只寫在 db/schema-description.md。
 */

-- 建立空間索引需要 QUOTED_IDENTIFIER ON；SqlClient 預設已是 ON，但 sqlcmd 預設是 OFF（或改用 sqlcmd -I）。
SET QUOTED_IDENTIFIER ON;
GO

-- 行政區：9 個合成多邊形（3×3 網格），用於空間 JOIN 與 STIntersects。
CREATE TABLE dbo.District (
    DistrictId   int           NOT NULL CONSTRAINT PK_District PRIMARY KEY,
    DistrictName nvarchar(50)  NOT NULL,
    Boundary     geography     NOT NULL,   -- 行政區邊界（多邊形，SRID 4326）
    Population   int           NOT NULL
);
GO

-- 資費方案：小型維度表。
CREATE TABLE dbo.ServicePlan (
    PlanId      int            NOT NULL CONSTRAINT PK_ServicePlan PRIMARY KEY,
    PlanName    nvarchar(50)   NOT NULL,
    MonthlyFee  decimal(8, 2)  NOT NULL,
    DataCapGb   int            NOT NULL    -- 每月流量上限（GB）
);
GO

-- 基地台：空間查詢的主體（附近、距離、落在哪個行政區）。
CREATE TABLE dbo.BaseStation (
    StationId     int           NOT NULL CONSTRAINT PK_BaseStation PRIMARY KEY,
    StationName   nvarchar(50)  NOT NULL,
    DistrictId    int           NOT NULL CONSTRAINT FK_BaseStation_District REFERENCES dbo.District (DistrictId),
    Location      geography     NOT NULL,  -- 基地台位置（點，SRID 4326）
    Band          nvarchar(10)  NOT NULL,  -- 頻段，例如 '700MHz'
    Status        nvarchar(20)  NOT NULL,  -- Active / Maintenance / Decommissioned
    InstalledDate date          NOT NULL
);
GO

-- 客戶：含 PII 欄位（M4 遮蔽用）；flg1 為命名不佳欄位。
CREATE TABLE dbo.Customer (
    CustomerId  int            NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    FullName    nvarchar(50)   NOT NULL,   -- PII
    NationalId  char(10)       NOT NULL,   -- PII，合成的身分證字號格式
    Phone       varchar(20)    NOT NULL,   -- PII
    Email       varchar(100)   NOT NULL,   -- PII
    DistrictId  int            NOT NULL CONSTRAINT FK_Customer_District REFERENCES dbo.District (DistrictId),
    flg1        tinyint        NOT NULL    -- 命名不佳：意義見 schema-description.md
);
GO

-- 訂閱：客戶與方案的多對多關係；dt2 為命名不佳欄位。
CREATE TABLE dbo.Subscription (
    SubscriptionId int   NOT NULL CONSTRAINT PK_Subscription PRIMARY KEY,
    CustomerId     int   NOT NULL CONSTRAINT FK_Subscription_Customer REFERENCES dbo.Customer (CustomerId),
    PlanId         int   NOT NULL CONSTRAINT FK_Subscription_ServicePlan REFERENCES dbo.ServicePlan (PlanId),
    StartDate      date  NOT NULL,
    dt2            date  NOT NULL          -- 命名不佳：意義見 schema-description.md
);
GO

-- 中斷事件：聚合與時間範圍題。
CREATE TABLE dbo.OutageEvent (
    OutageId        int           NOT NULL CONSTRAINT PK_OutageEvent PRIMARY KEY,
    StationId       int           NOT NULL CONSTRAINT FK_OutageEvent_BaseStation REFERENCES dbo.BaseStation (StationId),
    StartedAt       datetime2(0)  NOT NULL,
    DurationMinutes int           NOT NULL,
    Cause           nvarchar(30)  NOT NULL
);
GO

-- 空間索引：geography 索引要求資料表有叢集主鍵（上面已具備）。
CREATE SPATIAL INDEX SIX_District_Boundary    ON dbo.District    (Boundary);
CREATE SPATIAL INDEX SIX_BaseStation_Location ON dbo.BaseStation (Location);
GO

-- spike 專用受限身分：不需密碼的使用者，只有 dbo 結構描述的 SELECT 權限。
-- 執行模型生成的 SQL 時用：EXECUTE AS USER = 'spike_reader'; <SQL>; REVERT;
-- 注意：這不是安全邊界（生成的 SQL 若含 REVERT 可跳出），完整防護屬 M2。
CREATE USER spike_reader WITHOUT LOGIN;
GRANT SELECT ON SCHEMA::dbo TO spike_reader;
GO
