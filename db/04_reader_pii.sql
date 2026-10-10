/*
 * 04_reader_pii.sql — M4 管理者旁路專用的唯讀使用者 geo_reader_pii
 *
 * 前提：伺服器層級的 login geo_reader_pii 已存在（由 seed 依 ConnectionStrings:ReaderPii 建立）。
 * 用途：只給 admin 角色「同一句已通過驗證的 SQL 重跑」取原始值（docs/m4-implementation-plan.md §4.2）；
 *       模型的任何路徑都不使用它。
 *
 * 與 geo_reader 的差別只有兩點：預設結構描述是 dbo（Customer 解析到原始表），且沒有個資欄位的 DENY。
 * 它同樣是唯讀：寫入、改結構、EXECUTE 等 DENY 與 geo_reader 相同（兩層獨立性對它也要成立）。
 */

CREATE USER geo_reader_pii FOR LOGIN geo_reader_pii WITH DEFAULT_SCHEMA = dbo;
GO

GRANT SELECT ON SCHEMA::dbo TO geo_reader_pii;
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, TAKE OWNERSHIP ON SCHEMA::dbo TO geo_reader_pii;
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA, CREATE TYPE TO geo_reader_pii;
DENY EXECUTE TO geo_reader_pii;
-- 不給 ai：它讀的是原始表，不需要也不該碰遮蔽檢視。
DENY SELECT ON SCHEMA::ai TO geo_reader_pii;
GO
