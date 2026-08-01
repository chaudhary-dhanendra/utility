# Migration Studio 1.0.11

## Conversion reliability and scale

- Replaced repeated full-inventory scans in identifier classification, table conversion,
  constraint conversion, index conversion, and dependency ordering with immutable keyed indexes.
- Replaced repeated child-mapping list searches with constant-time canonical-key lookups.
- Added bounded deterministic collision allocation for long and collision-heavy identifiers.
- Added explicit weighted conversion stages from scope collection through package verification.
- Unified Simple Wizard and Operations-grid conversion progress on one authoritative snapshot.
- Added live elapsed time, current object, throughput, ETA, responsiveness, and mapping-set identity.
- Added a conversion watchdog that marks stale work unresponsive, exports sanitized diagnostics,
  and terminates an operation that cannot demonstrate liveness.
- Changed large conversion-result projections to bulk observable-collection updates and enabled
  recycling virtualization for large WPF grids.
- Made deployment-package generation and integrity verification part of the tracked Convert
  operation, with cancellation and atomic partial-package cleanup.

## Production verification

- The persisted VBGRAMG discovery inventory (191,444 objects) completes identifier candidate
  generation, identifier validation, 34,799 object conversions, and 35,767 dependency-ordering
  entries without the previous 88% progress stall.
- The production regression generated and verified a 1.42 GB deployment package containing
  14,141 files.
