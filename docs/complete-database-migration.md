# Complete database migration

Complete Database scope includes all discovered, supported database objects and closes the
dependency graph according to the selected dependency policy. Server-level objects are reported
but are not silently recreated.

Before discovery, confirm source permissions and whether SQL Agent, replication, external
dependencies, temporal tables, CDC, full text, and security metadata should be inventoried.

After discovery:

1. review object counts and every finding;
2. confirm cross-database and linked-server references;
3. run conversion and resolve manual/unsupported artifacts;
4. generate and archive the immutable deployment package;
5. assess extension, privilege, conflict, and target-version requirements;
6. deploy schemas and data structures;
7. stream table data and correct identity/sequence state;
8. deploy remaining constraints and programmable objects in dependency order;
9. run full validation; and
10. generate the final evidence package.

The application never guarantees semantic equivalence for dynamic SQL, platform-specific security,
CLR, external integration, or unsupported SQL Server features. Those items require documented
manual disposition.

