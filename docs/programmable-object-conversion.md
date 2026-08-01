# Programmable-object conversion

## Translation method

Programmable definitions use the same lossless tokenizer as defaults and computed expressions. Bracketed identifiers, strings, nested comments, whitespace, and nested function arguments are preserved. Rules operate on token sequences and parsed function argument lists, not blind global regular expressions.

## Views

Views support identifier conversion, common built-in functions, literals/comments, CTEs and normal PostgreSQL-compatible query syntax, and simple `TOP n`→`LIMIT n`.

`TOP PERCENT`, `WITH TIES`, temporary objects, `FOR XML`, provider rowset functions, unresolved three/four-part names, and other target-specific constructs produce a manual-review view skeleton with preserved source.

## Functions

Simple scalar functions with a single return expression become SQL-language functions. Inline TVFs become SQL functions returning the discovered table structure. Parameters and return columns use the central datatype and identifier registries.

Multi-statement TVFs, CLR functions, aggregates, missing/encrypted definitions, and unsupported procedural constructs remain manual. Their skeletons raise a clear runtime error or return an empty typed/manual view and are never classified automatic.

## Procedures

Procedures containing straightforward data-modification statements can become PL/pgSQL procedures. Parameter direction/defaults and simple variable assignment are translated.

Dynamic SQL, result sets, complex control flow, temporary/table variables, cursors, `MERGE`, `OUTPUT`, SQL Server TRY/CATCH, provider calls, and transaction edge cases produce compile-safe manual skeletons with actionable findings.

## DML triggers

Enabled SQL Server AFTER triggers with resolved parent tables can use PostgreSQL statement-level trigger functions and transition tables:

- `inserted` becomes `NEW TABLE AS inserted`;
- `deleted` becomes `OLD TABLE AS deleted`;
- the trigger remains statement-level to preserve SQL Server multi-row behavior.

Every automatic trigger carries a semantic-review warning for ordering and recursion differences. Disabled, `INSTEAD OF`, database/server DDL, cursor/dynamic, and SQL Server-specific trigger-state constructs remain manual or unsupported.

## Source preservation and review

Every manual programmable artifact contains:

- the target object and compile-safe skeleton where useful;
- preserved source T-SQL inside a bounded safe comment;
- unsupported construct names;
- conversion findings and rule ID;
- manual classification and unvalidated state.

The WPF conversion workspace provides side-by-side source/generated viewers, findings, and an editable manual-review copy. Edited SQL is not silently upgraded to automatic or validated status.
