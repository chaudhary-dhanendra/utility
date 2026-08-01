# Discovery engine

The SQL Server discovery engine issues version-aware, set-based catalog queries through
`Microsoft.Data.SqlClient`. It accumulates immutable metadata for database properties, objects,
columns, constraints, indexes, sequences, modules, security, dependencies, and advanced features.

Discovery scope supports complete database, selected schemas, Excel-selected tables, and manual
object selection. Dependency closure, strongly connected components, unresolved references, and
external dependencies are calculated without recursively querying per object.

Catalog reads are asynchronous and cancellable. Concurrency is bounded. Large object collections
are accumulated by stable identifiers and persisted as compressed, versioned, SHA-256-verified
inventory snapshots.

See `sqlserver-discovery.md` for query behavior and `catalog-query-reference.md` for catalog
coverage.

