# Sensitive application data

Sensitive detection controls masking and reporting only. It never excludes a column and never
changes a value.

Default normalized name patterns cover password/passwd/pwd/passcode, password hashes, salts, PINs,
secrets, tokens, refresh/access tokens, credentials, API/access/private/encryption keys. Extended
property metadata containing sensitive, secret, or credential markers is also recognized. Patterns
are configurable per run.

## Transfer invariants

- Text is transferred without trimming, case conversion, normalization, rehashing, or delimiter
  changes.
- Null and empty text remain distinct.
- Binary hashes, salts, ciphertext, and rowversion values stay raw bytes.
- The engine does not decrypt or re-encrypt unless a configured transformer explicitly does so.
- bcrypt, PBKDF2, Argon2, ASP.NET Identity-shaped strings, and binary hashes have regression tests
  proving unchanged transport.

## Diagnostics

Progress contains table names and counters only. Migration failures contain SQLSTATE, type metadata,
batch/ordinal metadata, retry count, disposition, and a generic redacted message. Provider details
that may echo an offending value are not copied into the result.

Normal Excel and HTML reports contain no failed-row values. Protected failed-row export is disabled
unless selected. When used, sensitive fields default to `***MASKED***`; binary fields default to
length metadata. Requesting unmasked output is a separate explicit flag and should only target a
directory protected by the administrator's Windows access controls.

Connection passwords and encryption keys are held only in the live request. They are excluded from
plans, checkpoints, progress, reports, and telemetry.

## Encryption classification

Always Encrypted, deterministic/randomized encryption, `EncryptByKey`, certificate/symmetric-key
encryption, and application ciphertext are classified independently from TDE. The default is opaque
ciphertext copying when SQL Server exposes bytes and the target can preserve them. Decrypt/re-encrypt,
exclusion, and manual migration require an explicit column policy. Target-side decryptability is
manual until verified by an application-specific validator.
