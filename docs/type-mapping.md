# Datatype and expression mapping

## Type registry

All columns, routine parameters, return types, user-defined types, and generated helper objects use `ITypeMappingRegistry`. Rules can be overridden globally or at schema/table/column specificity; the most-specific matching override wins.

| SQL Server | PostgreSQL rule |
|---|---|
| `bit` | `boolean` |
| `tinyint`, `smallint` | `smallint` |
| `int` | `integer` |
| `bigint` | `bigint` |
| `decimal`, `numeric` | `numeric(p,s)` |
| `money`, `smallmoney` | `numeric(19,4)`, `numeric(10,4)` |
| `float(1..24)`, larger `float` | `real`, `double precision` |
| `real` | `real` |
| `date` | `date` |
| `time` | `time(p)` up to PostgreSQL precision |
| `smalldatetime`, `datetime` | `timestamp without time zone` |
| `datetime2` | `timestamp(p) without time zone` |
| `datetimeoffset` | `timestamp(p) with time zone` |
| `char`, `varchar` | same family and meaningful length |
| `nchar`, `nvarchar` | `char`/`varchar`, converting SQL Server byte length to Unicode character units |
| max-length character types, `text`, `ntext` | `text` |
| binary families, `image`, `rowversion`/`timestamp` | `bytea` |
| `uniqueidentifier` | `uuid` |
| `xml` | `xml` |
| geography/geometry | PostGIS type only when enabled |
| `sql_variant`, `hierarchyid`, unknown/CLR types | configurable override or manual review |

Deprecated SQL Server types generate findings. PostGIS and pgcrypto are emitted only when rules require them and their options are enabled.

## Identity

Strategies are:

- `GeneratedByDefaultAsIdentity` (default);
- `GeneratedAlwaysAsIdentity`;
- `SequenceAndDefault`, which generates a stable helper sequence and `nextval` default;
- `PlainIntegerManual`, which intentionally removes automatic generation and produces a manual finding.

Seed and increment are preserved. Identity artifacts record the need for post-load reset when an explicit sequence is used.

## Defaults and computed columns

Expressions are tokenized before transformation. Literals, quoted identifiers, comments, nested parentheses, and statement boundaries remain opaque to keyword rules.

Automatic rules include `ISNULL`→`COALESCE`, `GETDATE`/`SYSDATETIME`, UTC time, UUID generation, character/octet length, case conversion, trimming, substring operations, `IIF`, date parts, interval-based `DATEADD`, common-unit `DATEDIFF`, `CHARINDEX`, `CONVERT`, Unicode literal prefixes, and `NEXT VALUE FOR`.

String `+` becomes `||` only when a typed operand or literal establishes string semantics. Numeric addition remains `+`. A finding records `CONCAT_NULL_YIELDS_NULL` semantic risk.

Computed columns become stored generated columns only when the converted expression is considered immutable. Non-deterministic functions, queries, unsupported functions, or unsafe semantics produce a data-migration/manual strategy and never claim successful generated-column conversion.
