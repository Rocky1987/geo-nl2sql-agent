/*
 * 03_masking.sql — M4 個資遮蔽：ai.Customer 遮蔽檢視與 geo_reader 的欄位層級 DENY
 *
 * 前提：01_schema.sql 與 02_reader.sql 已執行（geo_reader 的預設結構描述為 ai）。
 * 執行方式：seed 在 02_reader.sql 之後自動執行；手動執行：
 *       sqlcmd -S .\SQLEXPRESS -E -b -d GeoNl2SqlDemo -f 65001 -i db/03_masking.sql
 *
 * 設計：geo_reader 對 dbo.Customer 的四個個資欄位 DENY SELECT（原值在資料庫層讀不到，
 * 錯誤 230），改由 ai.Customer 檢視取遮蔽後的值。檢視與資料表同屬 dbo，靠擁有權鏈結讀底層，
 * 不檢查底層欄位的 DENY。檢視的欄位本身就是遮蔽後的運算式，所以 WHERE 比對的是遮蔽值，
 * 無法用條件反推原值（這是不用 Dynamic Data Masking 的原因）。
 */

CREATE SCHEMA ai AUTHORIZATION dbo;
GO

CREATE VIEW ai.Customer AS
SELECT CustomerId,
       LEFT(FullName, 1) + N'**'                                             AS FullName,
       LEFT(NationalId, 1) + REPLICATE('*', 7) + RIGHT(NationalId, 2)        AS NationalId,
       LEFT(Phone, 4) + '-***-' + RIGHT(Phone, 3)                            AS Phone,
       LEFT(Email, 1) + '***' + SUBSTRING(Email, CHARINDEX('@', Email), 100) AS Email,
       DistrictId,
       flg1
FROM dbo.Customer;
GO

GRANT SELECT ON SCHEMA::ai TO geo_reader;
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, TAKE OWNERSHIP ON SCHEMA::ai TO geo_reader;
DENY SELECT ON dbo.Customer (FullName, NationalId, Phone, Email) TO geo_reader;
GO
