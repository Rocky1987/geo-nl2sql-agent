/*
 * 02_reader.sql — M2 唯讀使用者 geo_reader 的資料庫層級權限
 *
 * 前提：伺服器層級的 login geo_reader 已存在（由 seed 依 ConnectionStrings:Reader 建立，或手動 CREATE LOGIN）。
 * 執行方式：seed 在 01_schema.sql 之後自動執行；手動執行：
 *       sqlcmd -S .\SQLEXPRESS -E -b -d GeoNl2SqlDemo -f 65001 -i db/02_reader.sql
 *
 * 設計：只 GRANT SELECT 就已擋住寫入；其餘 DENY 是第二道保險（DENY 優先於 GRANT，
 * 即使日後誤把 geo_reader 加進某個角色也不會多出權限）。geo_reader 不加入任何資料庫角色。
 */

CREATE USER geo_reader FOR LOGIN geo_reader;
GO

GRANT SELECT ON SCHEMA::dbo TO geo_reader;
-- 不可 DENY CONTROL：CONTROL 隱含 SELECT，DENY CONTROL 會連 SELECT 一併拒絕
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, TAKE OWNERSHIP ON SCHEMA::dbo TO geo_reader;
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA, CREATE TYPE TO geo_reader;
-- 資料庫層級 DENY EXECUTE：擋本資料庫內的預存程序。實測管不到 master 的 sp_who 這類
-- 授權給 public 的系統預存程序（權限在 master 內判斷），那一類只能靠驗證器擋
DENY EXECUTE TO geo_reader;
GO
