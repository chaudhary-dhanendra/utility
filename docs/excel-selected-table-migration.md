# Excel-selected-table migration

Excel scope accepts `.xlsx` workbooks through ClosedXML and never uses Excel COM automation.

Select a worksheet and the column containing table names. Names may be schema-qualified. The
matcher normalizes quoting and case, reports ambiguous and unmatched rows, and never silently
chooses between multiple candidates.

Review the matching result before discovery. Required dependencies may be added according to the
dependency policy. Dependencies do not automatically imply inclusion of unrelated data; verify the
final selected-object list.

Treat the workbook as untrusted input. Formula cells are read as workbook values, macros are not
executed, and only `.xlsx` is accepted. Keep selection files under change control for repeatable
migrations.

