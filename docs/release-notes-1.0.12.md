# Migration Studio 1.0.12

This release hardens production migration scope and PostgreSQL deployment readiness.

- Preserves selected SQL Server user schemas by default, including `dbo`, and consistently excludes SQL Server built-in schemas, roles, and feature metadata from deployable scope.
- Excludes PostgreSQL system and temporary schemas from assessment and validation through one shared policy.
- Replaces one-query-per-artifact conflict probing with a typed, batched PostgreSQL catalog comparison. Nonexistent objects are no longer reported as conflicts.
- Separates generated-package duplicates from existing-target conflicts and includes parent-table and routine-signature identity where applicable.
- Distinguishes executable package artifacts from traceability-only entries such as constraint-owned indexes and inline defaults.
- Orders executable artifacts by their actual dependency graph, including functions required by table expressions, rather than relying only on nominal phase numbers.
- Preserves computed source values when a SQL Server computed column must be emitted as an ordinary PostgreSQL column.
- Improves deployment readiness counts and presents existing conflicts, package duplicates, manual review, and blocking findings separately.

Production validation against the persisted VBGRAMG inventory covered 191,444 discovery records, 5,368 selected user tables, 156,617 columns, 4,058 stored procedures, 108 functions, 24 triggers, and a 35,173-artifact package. The corrected offline assessment produced zero package duplicates and zero blockers.
