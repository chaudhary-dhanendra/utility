# Scale Acceptance Criteria

Version: 1.0  
Workload: approximately 6,000 SQL Server user tables and more than 10 GB of source data  
Last qualification run: 2026-07-24

## Release-gate rules

A skipped test is not a pass. Synthetic tests qualify deterministic application paths but do not
replace live SQL Server or PostgreSQL measurements. The product must not be described as validated
for the customer's production workload until every mandatory live and interactive gate has passed
on representative infrastructure.

## Acceptance targets

### Discovery and inventory

- discover at least 6,000 tables without reading table data;
- managed-memory peak below 2.5 GB;
- no continuing managed-memory growth after retained inventory state is released;
- stage progress includes stage number and discovered object count;
- each catalog stage logs duration and inventory rows added;
- cancellation completes resource disposal within five seconds during normal catalog operations;
- the compressed snapshot saves and reloads with identical table and column counts;
- initial inventory projection completes within five seconds.

### Object presentation

- WPF grids use recycling row and column virtualization;
- searches are debounced, obsolete searches are cancelled, and filtering runs off the UI thread;
- one cached view model exists per displayed inventory object;
- collection replacement raises one reset notification rather than one notification per row;
- source and target SQL are bound only for the selected inspector item;
- interactive render, keyboard navigation, expansion, and selection remain responsive at 6,000
  tables on the supported workstation image.

### Dependencies and planning

- process at least 50,000 object IDs and 200,000 resolved dependency edges;
- find cycles and strongly connected components without recursion;
- produce identical component ordering when input order is reversed;
- acknowledge cancellation within five seconds;
- record managed-memory peak;
- table ordering is `O(V + E)` apart from deterministic ordered-set operations;
- the wave planner separates foundation, reference, independent, dependent, large, cyclic,
  programmable, security, and validation work.

### Excel

- import at least 6,000 table rows without COM automation;
- read only the selected worksheet and selected table-name column;
- classify blanks, duplicates, unmatched names, and ambiguous unqualified names;
- indexed name lookup must avoid scanning all inventory tables for every input row;
- report progress at bounded intervals and acknowledge cancellation within five seconds.

### Reporting

- produce Excel, offline HTML, and executive PDF for 6,000 tables, at least 72,000 columns,
  18,000 constraints, and 6,000 indexes;
- managed-memory peak below 2.5 GB;
- split Excel detail sheets deterministically below the configured threshold and always below
  1,048,576 rows;
- freeze headers and create filters on continuation sheets;
- escape spreadsheet formulas;
- emit HTML table data outside the initial DOM and render at most 100 rows per client page;
- escape HTML values and keep all resources offline;
- keep PDF content summary-focused;
- acknowledge report cancellation within five seconds and release workbook files.

### Data migration

- source readers and PostgreSQL writers stream rows through bounded batches and channels;
- live qualification includes binary COPY, parameterized text fallback, no-key tables, composite
  keys, identity gaps, sequence reset, Unicode, null-heavy values, decimal/date boundaries, LOBs,
  failed-row bisection, and connection-drop resume;
- run at least one several-million-row live migration;
- record rows/s, MiB/s, read/write/conversion/validation durations, managed-memory peak, GC
  collections, peak connections, checkpoint overhead, and resume overhead;
- sensitive application table values are transported unchanged while remaining absent from logs
  and reports.

### Cancellation and failure

Cancellation instrumentation must distinguish request time, command completion, reader disposal,
writer disposal, operation completion, and connection return. The UI remains `Cancelling` until
resource cleanup is complete.

Live failure qualification covers source and target disconnects, command and network timeouts,
server restart, permission failures, invalid packages, and disk exhaustion where the test machine
can provide a bounded disposable volume. No test may leave a background task, locked file, open
reader, leased pooled connection, corrupt checkpoint, or permanently disabled UI.

## 2026-07-24 local results

Machine: Intel Core i9-14900HX, 32 logical processors, 32 GB physical RAM, Windows
10.0.26200, x64, .NET 8.0.29. The fixed drive's SSD/HDD media type was not detectable without
privileged storage APIs.

| Workload | Result | Duration | Peak managed memory |
|---|---:|---:|---:|
| Construct 6,000 tables, 72,000 columns, 18,000 constraints | Passed | 494 ms | 44.5 MiB |
| Compressed snapshot save | Passed | 796 ms | 45.8 MiB |
| Compressed snapshot reload and count reconciliation | Passed | 1,130 ms | 101.7 MiB |
| 50,000 objects / 200,000 edges, cycles and reversed-input determinism | Passed | 589 ms | 200.8 MiB |
| Graph cancellation | Passed | 234 ms | 174.0 MiB |
| Excel import with 6,000 matches plus blanks/duplicates/invalid/ambiguous rows | Passed | 669 ms | 99.4 MiB |
| Excel cancellation | Passed | 90 ms | 91.1 MiB |
| UI projection/filter kernel, 6,000 unique view models | Passed | 9 ms | 74.1 MiB |
| Atomic checkpoint save/reload for 6,000 table states | Passed | 45 ms | 79.6 MiB |
| Three-million-row bounded transform/checksum workload | Passed | 249 ms | 73.4 MiB |
| Streaming workload cancellation | Passed | 23 ms | 73.4 MiB |
| 102,020-row identifier XLSX/CSV/HTML/JSON report | Passed | 27,208 ms | 1,476.4 MiB |
| Excel/HTML/PDF for 6,000 tables and 72,000 columns | Passed | 9,286 ms | 1,323.3 MiB |
| Report cancellation | Passed | 86 ms | 401.1 MiB |

The synthetic transform/checksum result is not a database throughput result. Rendered WPF
automation was not reproducible in the headless harness. Live SQL Server discovery, execution-plan
capture, live COPY, deployment, validation, disconnect handling, and server-side resume were
skipped because integration connection strings were unavailable. Those gates remain open.

Machine-readable and rendered evidence is under `artifacts/benchmarks`.
