# Manual object selection

Manual scope begins with discovered objects and allows explicit inclusion or exclusion. Search,
filter, and object-tree views are virtualized for large catalogs.

Selecting an object does not bypass dependency analysis. Depending on policy, required objects are
included, reported for approval, or left unresolved. The final inventory records why each object
was selected and whether it was directly chosen or dependency-added.

Before conversion, verify:

- every required schema and table is included;
- table data scope matches the intended cutover;
- referenced types, sequences, constraints, and routines are present;
- external dependencies have owners and remediation plans; and
- excluded objects have a documented business reason.

