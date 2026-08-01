# Migration package manifest

`manifest.json` format version 2 is the authoritative deployment contract.

It contains:

- package ID, conversion run ID, generation time, application version;
- source database and optional source server;
- target PostgreSQL major version;
- aggregate source metadata and conversion-configuration hashes;
- every required package file with relative path, SHA-256, length, and required flag;
- structured object artifacts with target identity, phase, SQL and SQL hash, dependencies,
  conversion classification, required extensions, manual-review state, and unsupported constructs;
- identifier mappings;
- data/checkpoint references;
- required extensions, manual items, unsupported features, and security classification.

The manifest itself is not included in its file list, avoiding a recursive hash. Paths must remain
inside the package root; rooted paths and traversal are rejected. Required missing files, size
changes, content-hash changes, structured SQL changes, unsupported manifest versions, and incomplete
identity fields fail verification.

Diagnostic mode may read a damaged package for inspection, but cannot make its integrity valid and
does not authorize deployment.

Artifacts embed the generated object-level PostgreSQL statement. This lets the executor avoid
reconstructing object boundaries from concatenated scripts. Script files remain human-reviewable
and support carefully parsed external editing, while the manifest hash makes every edit explicit.

The package contains schema metadata and generated SQL, not migrated row values. `10_Data` contains
references/checkpoints rather than secrets unless a future explicitly protected data-file plugin is
configured.
