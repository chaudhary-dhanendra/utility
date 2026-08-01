# SQL Server permissions

Use a dedicated least-privilege login. For database discovery, the account normally needs:

```sql
GRANT CONNECT TO [migration_login];
GRANT VIEW DEFINITION TO [migration_login];
GRANT SELECT TO [migration_login];
```

Grant only the narrower schema/object `SELECT` permissions required when a database-wide grant is
not acceptable. Data migration needs read access to every selected table. Validation needs the
same access plus permission to execute the metadata and aggregate queries selected by policy.

Server metadata and accessible-database enumeration may require `VIEW ANY DATABASE` and limited
server definition visibility. SQL Agent inventory is optional and requires suitable `msdb` role
membership, commonly `SQLAgentReaderRole`; it is not required for ordinary database migration.

Encrypted modules, Always Encrypted, external providers, linked servers, and cross-database
dependencies can require additional access. Do not grant `sysadmin` merely to simplify discovery.

The account does not need write permission to the source database.

