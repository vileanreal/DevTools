# Index advisor (backlog)

Status: **not started**. Captured 2026-11-23 for later.

## Goal

Read-only index advice for the databases saved in SQL Doc: indexes that would speed up real queries, indexes that cost writes but are never read, and duplicate or overlapping indexes. It only suggests and produces scripts to review and copy; it never creates or drops anything.

## Supported databases

| Database | Missing indexes | Unused indexes | Duplicate / overlapping | Notes |
|---|---|---|---|---|
| **SQL Server** | ✓ Full, from the optimizer's own records (`sys.dm_db_missing_index_details`, `_groups`, `_group_stats`), with impact and, on 2019+, example queries (`sys.dm_db_missing_index_group_stats_query`) | ✓ `sys.dm_db_index_usage_stats` + `sys.indexes` | ✓ `sys.index_columns` | Also fragmentation (`sys.dm_db_index_physical_stats`, LIMITED mode, on request). Needs `VIEW SERVER STATE` (`VIEW SERVER PERFORMANCE STATE` on 2022). |
| **PostgreSQL** | ◐ No built-in data. Flag tables scanned often instead of by index (`pg_stat_user_tables`: `seq_scan` vs `idx_scan`, `seq_tup_read`), plus the slowest queries on them when `pg_stat_statements` is enabled. No exact columns. | ✓ `pg_stat_user_indexes` (`idx_scan = 0`) | ✓ `pg_index` | Statistics reset with `pg_stat_reset` or restarts. |
| **MySQL / MariaDB** | ◐ Queries doing full table scans (`sys.statements_with_full_table_scans`) with query text. No exact columns. | ✓ `sys.schema_unused_indexes` | ✓ `sys.schema_redundant_indexes` | Needs `performance_schema` (on by default in recent versions). |
| **Oracle** | ◐ Oracle's SQL Tuning / Access Advisor needs the paid **Tuning Pack**; only use it if the user confirms the licence. Otherwise show full-table-scan heavy SQL from `V$SQL_PLAN`. | ✓ `DBA_INDEX_USAGE` (12.2+) | ✓ `DBA_IND_COLUMNS` | Querying Tuning Pack views without a licence can count as usage. |
| **SQLite** | ◐ No statistics. Paste a query; run `EXPLAIN QUERY PLAN` to show full scans vs index use and the columns an index would need. | ✗ Not tracked | ✓ `PRAGMA index_list` / `index_info` | Per-query check only. |

Order: SQL Server first, then unused/duplicates for PostgreSQL and MySQL, then Oracle and SQLite.

## How missing-index suggestions are built (SQL Server)

1. **Detection is done by SQL Server.** When the optimizer plans a query and no suitable index exists, it records a missing-index entry with **equality** columns (`=`), **inequality** columns (`<`, `>`, `BETWEEN`, `<>`, `LIKE 'abc%'`, `IS NOT NULL`) and **included** columns (read but not filtered on, to make the index covering), plus `user_seeks`, `user_scans`, `avg_total_user_cost` and `avg_user_impact` (estimated % reduction).
2. **Ranking:** `avg_total_user_cost × avg_user_impact × (user_seeks + user_scans)`; hide tiny gains below a threshold.
3. **Key column order:** equality columns first, ordered by selectivity (most distinct values first, from the existing statistics' density), then inequality columns last (only one range can be used efficiently). SQL Server itself lists them in table order.
4. **Merge overlaps:** suggestions for the same table whose keys are a prefix of another's become one index with the union of included columns.
5. **Reuse existing indexes:** if an index already starts with the same key columns, suggest extending it (`INCLUDE (...)`) instead of a new one.
6. **Sanity rules:** never put `(max)` text/binary or XML columns in keys (include only, or drop); if the include list covers nearly the whole table, suggest key columns only; warn above the key size limit (900 bytes clustered, 1,700 bytes nonclustered).
7. **Output:** a named, ready-to-review script, e.g. `CREATE NONCLUSTERED INDEX IX_Orders_CustomerId_Status_CreatedAt ON dbo.Orders (CustomerId, Status, CreatedAt) INCLUDE (OrderNo, Total);`, with "merged from N suggestions" and the table's write count next to it.

## Unused and duplicate indexes

- **Unused:** no seeks, scans or lookups but updates > 0, with size on disk. Never suggest dropping primary keys, unique indexes or indexes backing constraints. Offered as a `DROP INDEX` script to review.
- **Duplicates:** same key columns in the same order. **Overlapping:** one index's keys are a leading prefix of another's (`(A)` vs `(A, B)`).

## Caveats shown in the UI

- **Statistics reset on restart:** show "Statistics since: … (server start time)"; warn on unused-index advice when uptime is under about a month (month-end jobs).
- **Missing-index suggestions are naive:** they ignore write cost, existing indexes and filtered indexes (SQL Server never suggests filtered indexes). Review and test on UAT before applying.
- **Permissions:** explain which permission to request when the views can't be read.
- Short query timeouts; fragmentation only on request.

## Architecture (follows AGENTS.md)

- **Shared:** suggestion records (missing, unused, duplicate, fragmentation), the merge/ordering logic and the script builder (naming, quoting) — pure logic, unit-tested with sample data.
- **Application:** `IIndexAdvisor` with one implementation per provider (SQL Server first); read-only queries with timeouts; reuses SQL Doc's saved connections and providers.
- **Web:** a page to pick a saved connection, a "statistics since" banner, tabs for Missing / Unused / Duplicates / Fragmentation, and **Copy script** per suggestion. Results are not persisted.

## Estimate

About 1.5–2 days for SQL Server; about half a day more each for PostgreSQL and MySQL (unused and duplicates); Oracle and SQLite after that.
