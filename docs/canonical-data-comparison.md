# Canonical data comparison

## Encoding

Canonical values are typed. A row hash is not a delimiter-joined string: each value contributes its canonical kind plus a big-endian byte length and UTF-8 bytes. This prevents ambiguous rows such as `("ab","c")` and `("a","bc")` from sharing the same serialization.

Text is normalized to Unicode NFC. It is not case-folded unless the run explicitly selects a case-insensitive comparison contract. Fixed-width `char`/`nchar` trailing spaces may be removed; varying-width trailing spaces are retained.

| Logical value | Canonical rule |
|---|---|
| Null | dedicated null kind; never an empty string |
| Boolean | `true` or `false` |
| Integral | invariant base-10 digits |
| Decimal | invariant base-10; configured scale uses midpoint-to-even rounding |
| Floating point | round-trip representation; equality uses absolute or relative tolerance |
| Date | `yyyy-MM-dd` |
| Time | invariant time with configured fractional precision |
| Timestamp | timezone-free ISO representation with configured precision |
| Timestamp with zone | normalized to UTC by default, retaining an explicit offset |
| Text/Unicode | NFC, optional case contract, fixed-width policy |
| Binary | lowercase hexadecimal |
| UUID | lowercase canonical `D` format |
| XML | unchanged unless explicit formatting normalization is enabled |
| JSON | unchanged unless recursive property sorting is enabled |
| Spatial | provider representation only when a common SRID/format contract exists |

Over-normalization is forbidden. In particular, empty strings are not null, varying-width spaces are not discarded, local timestamps are not assumed to be UTC, JSON arrays are not reordered, and XML text/attribute semantics are not rewritten.

## Datatype semantics

Datatype validation compares the PostgreSQL declaration with the configured conversion rule. Expected mappings such as `bit` to `boolean`, `nvarchar(max)` to `text`, `uniqueidentifier` to `uuid`, and `varbinary` to `bytea` are `EquivalentWithExpectedTransformation`.

A different target declaration is a mismatch even if another general-purpose mapping might have been possible. Decimal precision/scale narrowing and `datetimeoffset` to a timezone-free timestamp are explicit risk findings.

## Hash hierarchy

- Row: SHA-256 over framed typed values.
- Ordered table: streaming SHA-256 over length-framed row hashes in mapped-key order.
- Keyless table: modulo-2^256 addition of row hashes plus row count, followed by SHA-256. This is order independent and retains duplicate multiplicity.
- Chunk: SHA-256 over framed chunk hashes.
- Sample: the same algorithm over a deterministic bounded query.

SQL Server `CHECKSUM` and `BINARY_CHECKSUM` are not used because they do not share PostgreSQL semantics and have unsuitable collision and datatype behavior.

Sensitive values are transformed into `sha256:<digest>` before they enter a metric or checksum accumulator. Reports can prove that the two protected values differ without disclosing either value.

## Collision and cost limits

SHA-256 collision risk is extremely small but not zero. The commutative keyless accumulator has different algebraic properties from ordered SHA-256 and must be combined with row counts and, where practical, column aggregates. Regulatory or evidentiary workflows may require full row-level reconciliation in a controlled system.

Optional exact distinct tracking consumes memory proportional to distinct cardinality. Comprehensive scans consume source and target I/O and should run in a maintenance window. Sampling reduces cost but cannot prove equality outside the sample.
