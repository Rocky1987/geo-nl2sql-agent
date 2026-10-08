# GeoNl2SqlDemo Schema 說明

本檔是 M1 spike 提供給模型的 schema 文字（「有描述」版本）。「無描述」版本只提供 `01_schema.sql` 的資料表與欄位名稱與型別，不含本檔的說明。兩者的準確率差異是 A5 的消融結果。

資料庫為 SQL Server，情境是電信基地台，全部是合成資料，不對應任何真實的人或地點。只允許 `SELECT`。

## 空間資料慣例

- 空間欄位型別為 `geography`，SRID 4326（WGS84）。
- 距離單位是**公尺**：`a.STDistance(b)` 回傳公尺，面積 `STArea()` 回傳平方公尺。
- 建立點：`geography::Point(緯度, 經度, 4326)`，**緯度在前**。
- 常用方法：`STDistance`、`STIntersects`（回傳 1 或 0）、`STContains`、`STBuffer(公尺)`。
- 沒有 `STCentroid()`。

## 資料表

### District（行政區，9 筆）

| 欄位 | 型別 | 說明 |
|---|---|---|
| DistrictId | int | 主鍵 |
| DistrictName | nvarchar(50) | 行政區名稱 |
| Boundary | geography | 行政區邊界（多邊形） |
| Population | int | 人口數 |

### BaseStation（基地台，約 200 筆）

| 欄位 | 型別 | 說明 |
|---|---|---|
| StationId | int | 主鍵 |
| StationName | nvarchar(50) | 基地台名稱 |
| DistrictId | int | 所在行政區，參照 District |
| Location | geography | 基地台位置（點） |
| Band | nvarchar(10) | 頻段：`700MHz`、`1800MHz`、`2600MHz`、`3500MHz` |
| Status | nvarchar(20) | 狀態：`Active`（運轉中）、`Maintenance`（維護中）、`Decommissioned`（已除役） |
| InstalledDate | date | 啟用日期 |

### ServicePlan（資費方案，6 筆）

| 欄位 | 型別 | 說明 |
|---|---|---|
| PlanId | int | 主鍵 |
| PlanName | nvarchar(50) | 方案名稱 |
| MonthlyFee | decimal(8,2) | 月租費（新台幣） |
| DataCapGb | int | 每月流量上限（GB） |

### Customer（客戶，約 1,000 筆）

| 欄位 | 型別 | 說明 |
|---|---|---|
| CustomerId | int | 主鍵 |
| FullName | nvarchar(50) | 姓名（個資） |
| NationalId | char(10) | 身分證字號（個資） |
| Phone | varchar(20) | 電話（個資） |
| Email | varchar(100) | 電子郵件（個資） |
| DistrictId | int | 客戶所在行政區，參照 District |
| flg1 | tinyint | **客戶類型**：`0` 個人、`1` 企業、`2` 政府機關 |

### Subscription（訂閱，約 1,500 筆）

一位客戶可有多筆訂閱；一個方案可被多位客戶訂閱。

| 欄位 | 型別 | 說明 |
|---|---|---|
| SubscriptionId | int | 主鍵 |
| CustomerId | int | 參照 Customer |
| PlanId | int | 參照 ServicePlan |
| StartDate | date | 訂閱起始日 |
| dt2 | date | **合約到期日** |

### OutageEvent（基地台中斷事件，約 300 筆）

| 欄位 | 型別 | 說明 |
|---|---|---|
| OutageId | int | 主鍵 |
| StationId | int | 發生中斷的基地台，參照 BaseStation |
| StartedAt | datetime2(0) | 中斷開始時間 |
| DurationMinutes | int | 中斷持續分鐘數 |
| Cause | nvarchar(30) | 原因：`PowerFailure`、`FiberCut`、`Weather`、`Hardware`、`Software` |

## 關聯

- `BaseStation.DistrictId` → `District.DistrictId`
- `Customer.DistrictId` → `District.DistrictId`
- `Subscription.CustomerId` → `Customer.CustomerId`
- `Subscription.PlanId` → `ServicePlan.PlanId`
- `OutageEvent.StationId` → `BaseStation.StationId`

## 維護注意（給出題者，不提供給模型）

- 上面列出的列舉值（Band、Status、Cause、flg1）是 Seed 的產生範圍，兩邊必須同步修改。
- 這份說明不得寫入答案線索（例如特定題目的解法）。
