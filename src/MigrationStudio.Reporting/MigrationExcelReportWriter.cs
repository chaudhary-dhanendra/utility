using System.Globalization;
using ClosedXML.Excel;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Reporting;

public sealed class MigrationExcelReportWriter(int maximumRowsPerSheet = 1_048_576)
{
    private readonly int _maximumRowsPerSheet = maximumRowsPerSheet >= 3
        ? maximumRowsPerSheet
        : throw new ArgumentOutOfRangeException(
            nameof(maximumRowsPerSheet), "A worksheet must allow a header and at least two data rows.");
    private CancellationToken _writeCancellationToken;

    public void Write(MigrationReportDocument report, string path, CancellationToken cancellationToken)
    {
        _writeCancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        using var workbook = new XLWorkbook();
        AddExecutiveSummary(workbook, report);
        AddRows(workbook, "Source Database",
            ["Property", "Value"],
            SourceDatabaseRows(report));
        AddRows(workbook, "Migration Configuration",
            ["Area", "Setting", "Value"],
            ConfigurationRows(report));
        AddRows(workbook, "Object Inventory",
            ["Object ID", "Type", "Schema", "Name", "Included", "Selection", "Classification", "Discovery", "Metadata hash"],
            report.Inventory.Objects.Select(item => Row(
                item.Id, item.ObjectType, item.SourceSchema, item.SourceName, item.IsIncluded,
                item.SelectionReason, item.ConversionClassification, item.DiscoveryStatus, item.MetadataHash)));
        AddRows(workbook, "Schema Mapping",
            ["Source schema", "Target schema", "Excluded"],
            report.Conversion?.Options.SchemaMappings.Select(item =>
                Row(item.SourceSchema, item.TargetSchema, item.IsExcluded)) ?? []);
        AddIdentifierMappingRows(workbook, report);
        AddRows(workbook, "Datatype Mapping",
            ["Source type", "Target type", "Classification", "Rule", "Extensions"],
            report.Conversion?.TypeMappings.Select(item => Row(
                item.SourceType, item.TargetType, item.Classification, item.RuleId,
                string.Join(", ", item.RequiredExtensions))) ?? []);
        AddRows(workbook, "Tables",
            ["Object ID", "Kind", "Estimated rows", "Reserved bytes", "Used bytes", "Temporal", "External", "Memory optimized"],
            report.Inventory.Tables.Select(item => Row(
                item.ObjectId, item.Kind, item.RowCountEstimate, item.ReservedBytes, item.UsedBytes,
                item.TemporalType, item.IsExternal, item.IsMemoryOptimized)));
        AddRows(workbook, "Columns",
            ["Table ID", "Ordinal", "Name", "System type", "User type", "Length", "Precision", "Scale", "Nullable", "Identity", "Computed", "Default", "Collation", "Masked"],
            report.Inventory.Columns.Select(item => Row(
                item.ParentObjectId, item.OrdinalPosition, item.Name, item.SystemTypeName,
                $"{item.TypeSchema}.{item.UserTypeName}", item.MaximumLength, item.Precision, item.Scale,
                item.IsNullable, item.IsIdentity, item.IsComputed, item.DefaultDefinition,
                item.Collation, item.IsMasked)));
        AddConstraintSheet(workbook, report, "Primary Keys", ConstraintKind.PrimaryKey);
        AddConstraintSheet(workbook, report, "Unique Constraints", ConstraintKind.Unique);
        AddConstraintSheet(workbook, report, "Check Constraints", ConstraintKind.Check);
        AddConstraintSheet(workbook, report, "Foreign Keys", ConstraintKind.ForeignKey);
        AddRows(workbook, "Indexes",
            ["Table ID", "Name", "Kind", "Unique", "Disabled", "Filtered", "Predicate", "Key columns", "Included columns"],
            report.Inventory.Indexes.Select(item => Row(
                item.TableObjectId, item.Name, item.Kind, item.IsUnique, item.IsDisabled,
                item.IsFiltered, item.FilterDefinition,
                string.Join(", ", item.Columns.Where(column => !column.IsIncluded)
                    .OrderBy(column => column.KeyOrdinal).Select(column => column.Name)),
                string.Join(", ", item.Columns.Where(column => column.IsIncluded).Select(column => column.Name)))));
        AddRows(workbook, "Sequences",
            ["Object ID", "Type", "Start", "Increment", "Minimum", "Maximum", "Current", "Cycling", "Exhausted"],
            report.Inventory.Sequences.Select(item => Row(
                item.ObjectId, $"{item.TypeSchema}.{item.TypeName}", item.StartValue, item.Increment,
                item.MinimumValue, item.MaximumValue, item.CurrentValue, item.IsCycling, item.IsExhausted)));
        AddObjectTypeSheet(workbook, report, "Views", InventoryObjectType.View);
        AddObjectTypeSheet(workbook, report, "Functions", InventoryObjectType.Function);
        AddObjectTypeSheet(workbook, report, "Procedures", InventoryObjectType.StoredProcedure);
        AddObjectTypeSheet(workbook, report, "Triggers", InventoryObjectType.Trigger);
        AddObjectTypeSheet(workbook, report, "Types", InventoryObjectType.UserDefinedType, InventoryObjectType.TableType);
        AddRows(workbook, "Synonyms",
            ["Object ID", "Base object", "Server", "Database", "Schema", "Object", "Cross database", "Linked server"],
            report.Inventory.Synonyms.Select(item => Row(
                item.ObjectId, item.BaseObjectName, item.ServerName, item.DatabaseName, item.SchemaName,
                item.ObjectName, item.IsCrossDatabaseReference, item.IsLinkedServerReference)));
        AddRows(workbook, "Security",
            ["Kind", "Name", "Type or permission", "State", "Memberships or target"],
            report.Inventory.SecurityPrincipals.Select(item => Row(
                    "Principal", item.Name, item.TypeDescription, item.AuthenticationType,
                    string.Join(", ", item.RoleMemberships)))
                .Concat(report.Inventory.Permissions.Select(item => Row(
                    "Permission", item.Grantee, item.PermissionName, item.State,
                    item.TargetObjectId?.ToString() ?? item.ClassDescription))));
        AddRows(workbook, "Extended Properties",
            ["Object ID", "Target level", "Subobject", "Name", "Value"],
            report.Inventory.Objects.SelectMany(item => item.ExtendedProperties)
                .Concat(report.Inventory.Columns.SelectMany(item => item.ExtendedProperties))
                .Select(item => Row(
                    item.TargetObjectId, item.TargetLevel, item.TargetSubObjectName,
                    item.Name, item.Value)));
        AddRows(workbook, "Data Migration",
            ["Table", "State", "Rows read", "Rows written", "Rejected", "Bytes", "Rows per second", "Duration", "Retries", "Failures"],
            report.DataMigration?.Tables.Select(item => Row(
                item.Table, item.State, item.RowsRead, item.RowsWritten, item.RowsRejected,
                item.BytesTransferred, item.RowsPerSecond, item.TotalDuration, item.RetryCount,
                item.FailureCount)) ?? []);
        AddRows(workbook, "Data Reconciliation",
            ["Source table", "Target table", "Source rows", "Target rows", "Classification", "Ordered checksum", "Detail"],
            report.Validation?.DataComparisons.Select(item => Row(
                item.SourceTable, item.TargetTable, item.SourceRowCount, item.TargetRowCount,
                item.Classification, item.IsOrderedChecksum, item.Detail)) ??
            report.DataMigration?.Validations.Select(item => Row(
                item.Table, item.Table, item.SourceRowCount, item.TargetRowCount,
                item.Outcome, false, item.Message)) ?? []);
        AddRows(workbook, "Sequence Validation",
            ["Source", "Target", "Current", "Maximum key", "Expected next", "Increment", "Cycle", "Duplicate risk", "Classification"],
            report.Validation?.SequenceResults.Select(item => Row(
                item.SourceSequence, item.TargetSequence, item.CurrentValue, item.MaximumColumnValue,
                item.ExpectedNextValue, item.Increment, item.IsCycling, item.WouldGenerateDuplicate,
                item.Classification)) ?? []);
        AddRows(workbook, "Deployment Journal",
            ["Phase", "Target object", "Status", "Commit", "Started", "Ended", "Retries", "Message", "Failure SQLSTATE"],
            report.Deployment?.Objects.Select(item => Row(
                item.Phase, item.TargetObject, item.Status, item.CommitStatus, item.StartedAt,
                item.EndedAt, item.Retries.Count, item.Message, item.Failure?.SqlState)) ?? []);
        AddRows(workbook, "Conversion Findings",
            ["Severity", "Code", "Object ID", "Message", "Evidence", "Remediation"],
            (report.Conversion?.Findings ?? report.Inventory.Findings).Select(item => Row(
                item.Severity, item.Code, item.ObjectId, item.Message, item.Evidence, item.Remediation)));
        AddRows(workbook, "Manual Review",
            ["Status", "Critical", "Owner", "Title", "Source", "Comments", "Resolution", "Reviewed by", "Reviewed at"],
            report.ManualReviews.Select(item => Row(
                item.Status, item.IsCriticalBlocker, item.Owner, item.Title, item.Source,
                item.Comments, item.Resolution, item.ReviewedBy, item.ReviewedAt)));
        AddRows(workbook, "Unsupported Features",
            ["Source object", "Target object", "Unsupported constructs", "Rule"],
            report.Conversion?.Artifacts.Where(item =>
                    item.Classification == ConversionClassification.Unsupported ||
                    item.UnsupportedConstructs.Count > 0)
                .Select(item => Row(
                    item.SourceObjectId, item.TargetObjectId.QualifiedName,
                    string.Join(", ", item.UnsupportedConstructs), item.RuleId)) ?? []);
        AddRows(workbook, "External Dependencies",
            ["Source object", "Kind", "Referenced name", "Server", "Database", "Schema", "Resolved", "Evidence"],
            report.Inventory.ExternalDependencies.Select(item => Row(
                item.SourceObjectId, item.ReferenceKind, item.ReferencedName, item.ServerName,
                item.DatabaseName, item.SchemaName, item.IsResolved, item.Evidence)));
        AddRows(workbook, "SQL Agent Jobs",
            ["Name", "Enabled", "Owner", "Category", "Steps", "Schedules"],
            report.Inventory.SqlAgentJobs.Select(item => Row(
                item.Name, item.IsEnabled, item.Owner, item.Category, item.Steps.Count,
                string.Join(", ", item.Schedules))));
        AddRows(workbook, "Full Text",
            ["Kind", "Name", "Target object", "Change tracking", "Stoplist", "Columns"],
            report.Inventory.FullText.Select(item => Row(
                item.Kind, item.Name, item.TargetObjectId, item.ChangeTrackingState,
                item.Stoplist, string.Join(", ", item.IndexedColumns))));
        AddRows(workbook, "Temporal and CDC",
            ["Feature", "Table", "Related object", "Enabled", "Detail"],
            report.Inventory.TemporalTables.Select(item => Row(
                    "Temporal", item.CurrentTableId, item.HistoryTableId, item.IsSystemVersioned,
                    item.HistoryRetentionPeriod))
                .Concat(report.Inventory.ChangeData.Select(item => Row(
                    item.Feature, item.TableObjectId, item.CaptureInstance, item.IsEnabled, item.Retention))));
        AddRows(workbook, "Partitioning",
            ["Kind", "Object ID", "Name or function", "Detail"],
            report.Inventory.PartitionFunctions.Select(item => Row(
                    "Function", item.ObjectId, string.Empty, string.Join(", ", item.BoundaryValues)))
                .Concat(report.Inventory.PartitionSchemes.Select(item => Row(
                    "Scheme", item.ObjectId, item.FunctionName,
                    string.Join(", ", item.DestinationDataSpaces)))));
        AddRows(workbook, "Required Extensions",
            ["Extension"],
            report.Conversion?.RequiredExtensions.Select(item => Row(item)) ?? []);
        AddRows(workbook, "Performance Metrics",
            ["Phase or table", "Duration", "Rows", "Bytes", "Rows per second", "Bytes per second"],
            PerformanceRows(report));
        AddRows(workbook, "Validation Summary",
            ["Severity", "Classification", "Category", "Rule", "Source", "Target", "Summary"],
            report.Validation?.Findings.Select(item => Row(
                item.Severity, item.Classification, item.Category, item.RuleId,
                item.SourceObject, item.TargetObject, item.Summary)) ?? []);
        AddRows(workbook, "Readiness Score",
            ["Category", "Status", "Score", "Weight", "Applicable", "Passed", "Warnings", "Blockers", "Explanation"],
            report.Validation?.Readiness.Categories.Select(item => Row(
                item.Category, item.Status, item.Score, item.Weight, item.ApplicableChecks,
                item.PassedChecks, item.WarningChecks, item.BlockerChecks, item.Explanation)) ?? []);

        cancellationToken.ThrowIfCancellationRequested();
        ExcelReportSecurity.Protect(workbook);
        cancellationToken.ThrowIfCancellationRequested();
        workbook.SaveAs(path);
    }

    public static string SanitizeWorksheetName(string name)
    {
        var invalid = new HashSet<char>([':', '\\', '/', '?', '*', '[', ']']);
        var sanitized = new string(name.Where(character => !invalid.Contains(character)).ToArray()).Trim('\'');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Sheet";
        }
        return sanitized.Length <= 31 ? sanitized : sanitized[..31];
    }

    private static void AddExecutiveSummary(XLWorkbook workbook, MigrationReportDocument report)
    {
        var sheet = workbook.AddWorksheet("Executive Summary");
        var summary = report.Summary;
        var values = new (string Label, object? Value)[]
        {
            ("Application version", summary.ApplicationVersion),
            ("Report timestamp", summary.GeneratedAt),
            ("Source server", summary.Source.Server),
            ("Source database", summary.Source.Database),
            ("SQL Server version", summary.Source.Version),
            ("SQL Server edition", summary.Source.Edition),
            ("Target server", summary.Target.Server),
            ("Target database", summary.Target.Database),
            ("PostgreSQL version", summary.Target.Version),
            ("Migration scope", summary.Scope),
            ("Included schemas", string.Join(", ", summary.IncludedSchemas)),
            ("Included objects", summary.IncludedObjects),
            ("Excluded objects", summary.ExcludedObjects),
            ("Schema conversion", summary.SchemaConversionResult),
            ("Data migration", summary.DataMigrationResult),
            ("Deployment", summary.DeploymentResult),
            ("Validation", summary.ValidationResult),
            ("Overall readiness", summary.OverallReadiness),
            ("Critical blockers", summary.CriticalBlockers),
            ("Warnings", summary.Warnings),
            ("Manual review", summary.ManualReviews),
            ("Unsupported", summary.Unsupported),
            ("Rows read", summary.RowsRead),
            ("Rows written", summary.RowsWritten),
            ("Failed rows", summary.FailedRows),
            ("Total duration", summary.TotalDuration),
            ("Data throughput rows/sec", summary.RowsPerSecond),
            ("Deployment duration", summary.DeploymentDuration),
            ("Validation duration", summary.ValidationDuration)
        };
        sheet.Cell(1, 1).Value = report.Template.ReportTitle;
        sheet.Range(1, 1, 1, 2).Merge().Style
            .Font.SetBold().Font.SetFontSize(18).Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E78"));
        sheet.Range(1, 1, 1, 2).Style.Font.SetFontColor(XLColor.White);
        for (var index = 0; index < values.Length; index++)
        {
            sheet.Cell(index + 3, 1).Value = values[index].Label;
            sheet.Cell(index + 3, 2).Value =
                XLCellValue.FromObject(values[index].Value, CultureInfo.InvariantCulture);
        }
        var links = new[]
        {
            "Object Inventory", "Data Migration", "Deployment Journal", "Validation Summary",
            "Manual Review", "Unsupported Features", "Readiness Score"
        };
        var linkRow = values.Length + 5;
        sheet.Cell(linkRow, 1).Value = "Detailed sheets";
        for (var index = 0; index < links.Length; index++)
        {
            var cell = sheet.Cell(linkRow + index + 1, 1);
            cell.Value = links[index];
            cell.SetHyperlink(new XLHyperlink($"'{links[index]}'!A1"));
            cell.Style.Font.SetFontColor(XLColor.Blue).Font.SetUnderline();
        }
        SetReadableWidths(sheet, 2, values.Length + 5);
        AddIdentifierLegend(sheet, values.Length + 7, 1);
    }

    private void AddIdentifierMappingRows(XLWorkbook workbook, MigrationReportDocument report)
    {
        var mappings = report.Conversion?.IdentifierMappings ?? [];
        AddRows(
            workbook,
            "Identifier Mapping",
            [
                "Object type", "Parent object", "Source database", "Source schema", "Source name",
                "Source qualified name", "Target schema", "Target name", "Target qualified name",
                "Source UTF-8 byte length", "Target UTF-8 byte length", "Source character length",
                "Target character length", "Is reserved word", "Requires quoting", "Was quoted",
                "Was case-normalized", "Was shortened", "Collision detected", "Collision resolved",
                "Mapping status", "Transformation reason", "Hash suffix", "Severity",
                "Manual review required"
            ],
            mappings.Select(item => Row(
                item.ObjectType, item.ParentObject, item.SourceDatabase, item.SourceSchema,
                item.SourceName, item.SourceQualifiedName, item.TargetSchema, item.TargetName,
                item.TargetQualifiedName, item.OriginalUtf8ByteLength, item.TargetUtf8ByteLength,
                item.SourceCharacterLength, item.TargetCharacterLength, item.IsReservedWord,
                item.RequiresQuoting, item.WasQuoted, item.WasCaseNormalized, item.WasShortened,
                item.HadCollision, item.CollisionResolved, IdentifierStatusText(item),
                item.TransformationReason, item.HashSuffix, item.Severity,
                item.ManualReviewRequired)));

        var offset = 0;
        var part = 0;
        while (offset < mappings.Count || part == 0)
        {
            var name = part == 0 ? "Identifier Mapping" : $"Identifier Mapping {part + 1}";
            if (!workbook.Worksheets.TryGetWorksheet(name, out var sheet))
            {
                break;
            }
            var count = Math.Min(_maximumRowsPerSheet - 1, mappings.Count - offset);
            for (var index = 0; index < count; index++)
            {
                var mapping = mappings[offset + index];
                var row = index + 2;
                sheet.Range(row, 1, row, 25).Style.Fill.BackgroundColor =
                    IdentifierStatusColor(mapping);
                if (mapping.IsBlocking)
                {
                    sheet.Range(row, 1, row, 25).Style.Font.FontColor = XLColor.White;
                }
            }
            AddIdentifierLegend(sheet, count + 3, 1);
            offset += count;
            part++;
            if (mappings.Count == 0)
            {
                break;
            }
        }
    }

    private void AddConstraintSheet(
        XLWorkbook workbook,
        MigrationReportDocument report,
        string name,
        ConstraintKind kind) =>
        AddRows(workbook, name,
            ["Table ID", "Name", "Columns", "Referenced table", "Referenced columns", "Definition", "Disabled", "Not trusted", "Delete", "Update"],
            report.Inventory.Constraints.Where(item => item.Kind == kind).Select(item => Row(
                item.TableObjectId, item.Name,
                string.Join(", ", item.Columns.OrderBy(column => column.Ordinal).Select(column => column.Name)),
                item.ReferencedTableObjectId,
                string.Join(", ", item.ReferencedColumns.OrderBy(column => column.Ordinal).Select(column => column.Name)),
                item.Definition, item.IsDisabled, item.IsNotTrusted, item.DeleteAction, item.UpdateAction)));

    private void AddObjectTypeSheet(
        XLWorkbook workbook,
        MigrationReportDocument report,
        string name,
        params InventoryObjectType[] types) =>
        AddRows(workbook, name,
            ["Object ID", "Schema", "Name", "Classification", "Definition", "Definition hash", "Discovery"],
            report.Inventory.Objects.Where(item => types.Contains(item.ObjectType)).Select(item => Row(
                item.Id, item.SourceSchema, item.SourceName, item.ConversionClassification,
                item.SourceDefinition, item.SourceDefinitionHash, item.DiscoveryStatus)));

    private void AddRows(
        XLWorkbook workbook,
        string baseName,
        IReadOnlyList<string> headers,
        IEnumerable<object?[]> sourceRows)
    {
        var capacity = _maximumRowsPerSheet - 1;
        using var rows = sourceRows.GetEnumerator();
        var hasRow = rows.MoveNext();
        var part = 0;
        do
        {
            var suffix = part == 0 ? string.Empty : $" {part + 1}";
            var sheetName = UniqueName(workbook, SanitizeWorksheetName(baseName + suffix));
            var sheet = workbook.AddWorksheet(sheetName);
            for (var column = 0; column < headers.Count; column++)
            {
                sheet.Cell(1, column + 1).Value = headers[column];
            }

            var row = 0;
            while (hasRow && row < capacity)
            {
                if ((row & 255) == 0)
                {
                    _writeCancellationToken.ThrowIfCancellationRequested();
                }

                var current = rows.Current;
                for (var column = 0; column < headers.Count; column++)
                {
                    var value = column < current.Length ? current[column] : null;
                    sheet.Cell(row + 2, column + 1).Value =
                        XLCellValue.FromObject(value, CultureInfo.InvariantCulture);
                }

                row++;
                hasRow = rows.MoveNext();
            }

            StyleSheet(sheet, headers, row);
            part++;
        } while (hasRow);
    }

    private static void StyleSheet(IXLWorksheet sheet, IReadOnlyList<string> headers, int rowCount)
    {
        var lastColumn = Math.Max(1, headers.Count);
        var header = sheet.Range(1, 1, 1, lastColumn);
        header.Style.Font.SetBold().Font.SetFontColor(XLColor.White);
        header.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E78"));
        sheet.SheetView.FreezeRows(1);
        if (rowCount > 0)
        {
            var range = sheet.Range(1, 1, rowCount + 1, lastColumn);
            range.CreateTable();
            var severityColumn = headers.Select((value, index) => (value, index))
                .FirstOrDefault(item => item.value.Equals("Severity", StringComparison.OrdinalIgnoreCase));
            if (severityColumn.value is not null)
            {
                var cells = sheet.Range(2, severityColumn.index + 1, rowCount + 1, severityColumn.index + 1);
                cells.AddConditionalFormat().WhenEquals("Critical").Fill.SetBackgroundColor(XLColor.FromHtml("#F4CCCC"));
                cells.AddConditionalFormat().WhenEquals("Error").Fill.SetBackgroundColor(XLColor.FromHtml("#FCE5CD"));
                cells.AddConditionalFormat().WhenEquals("Warning").Fill.SetBackgroundColor(XLColor.FromHtml("#FFF2CC"));
            }
        }
        SetReadableWidths(sheet, lastColumn, Math.Min(rowCount + 1, 500));
        sheet.RangeUsed()?.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
    }

    private static void SetReadableWidths(IXLWorksheet sheet, int lastColumn, int sampledLastRow)
    {
        for (var columnNumber = 1; columnNumber <= lastColumn; columnNumber++)
        {
            var maximum = 0;
            for (var row = 1; row <= sampledLastRow; row++)
            {
                maximum = Math.Max(maximum, sheet.Cell(row, columnNumber).GetString().Length);
            }
            var column = sheet.Column(columnNumber);
            column.Width = Math.Clamp(maximum + 2, 10, 60);
            if (maximum > 58)
            {
                column.Style.Alignment.WrapText = true;
            }
        }
    }

    private static IEnumerable<object?[]> SourceDatabaseRows(MigrationReportDocument report)
    {
        var db = report.Inventory.Database;
        yield return Row("Product version", db.ProductVersion);
        yield return Row("Product level", db.ProductLevel);
        yield return Row("Edition", db.Edition);
        yield return Row("Database", db.DatabaseName);
        yield return Row("Owner", db.Owner);
        yield return Row("Compatibility level", db.CompatibilityLevel);
        yield return Row("Collation", db.Collation);
        yield return Row("Recovery model", db.RecoveryModel);
        yield return Row("Encrypted", db.IsEncrypted);
        yield return Row("Snapshot timestamp", report.Inventory.SnapshotTimestamp);
    }

    private static IEnumerable<object?[]> ConfigurationRows(MigrationReportDocument report)
    {
        yield return Row("Scope", "Mode", report.Inventory.ScopeMode);
        if (report.Conversion is not null)
        {
            yield return Row("Conversion", "Target PostgreSQL", report.Conversion.TargetVersion);
            yield return Row("Conversion", "Identifier case", report.Conversion.Options.IdentifierCaseMode);
            yield return Row("Conversion", "Schema mapping", report.Conversion.Options.SchemaMappingMode);
            yield return Row("Conversion", "Identity", report.Conversion.Options.IdentityStrategy);
            yield return Row("Conversion", "Security", report.Conversion.Options.SecurityStrategy);
        }
        if (report.Validation is not null)
        {
            yield return Row("Validation", "Level", report.Validation.Configuration.Level);
            yield return Row("Validation", "Keyless strategy", report.Validation.Configuration.KeylessTableStrategy);
            yield return Row("Validation", "Sample size", report.Validation.Configuration.SampleSize);
        }
    }

    private static IEnumerable<object?[]> PerformanceRows(MigrationReportDocument report)
    {
        if (report.DataMigration is not null)
        {
            foreach (var table in report.DataMigration.Tables)
            {
                yield return Row(
                    table.Table, table.TotalDuration, table.RowsWritten, table.BytesTransferred,
                    table.RowsPerSecond, table.BytesPerSecond);
            }
        }
        if (report.Deployment is not null)
        {
            yield return Row(
                "Deployment", report.Deployment.CompletedAt - report.Deployment.StartedAt,
                null, null, null, null);
        }
        if (report.Validation is not null)
        {
            yield return Row(
                "Validation", report.Validation.CompletedAt - report.Validation.StartedAt,
                null, null, null, null);
        }
    }

    private static object?[] Row(params object?[] values) => values;

    private static string IdentifierStatusText(MigrationStudio.Domain.Conversion.IdentifierMappingEntry item) =>
        item.MappingStatus switch
        {
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.ReservedWordSafelyQuoted =>
                "Reserved word — safely quoted",
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.AutomaticallyShortened =>
                "Long identifier — automatically shortened",
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.CollisionResolved =>
                "Collision — automatically resolved",
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.BlockingConflict =>
                "Blocking identifier conflict",
            _ => "Safe"
        };

    private static XLColor IdentifierStatusColor(MigrationStudio.Domain.Conversion.IdentifierMappingEntry item) =>
        item.MappingStatus switch
        {
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.ReservedWordSafelyQuoted =>
                XLColor.FromHtml("#FFF2CC"),
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.AutomaticallyShortened or
                MigrationStudio.Domain.Conversion.IdentifierMappingStatus.CollisionResolved =>
                XLColor.FromHtml("#F4B183"),
            MigrationStudio.Domain.Conversion.IdentifierMappingStatus.BlockingConflict =>
                XLColor.FromHtml("#C00000"),
            _ => XLColor.FromHtml("#D9EAD3")
        };

    private static void AddIdentifierLegend(IXLWorksheet sheet, int row, int column)
    {
        sheet.Cell(row, column).Value = "Identifier status legend";
        sheet.Cell(row, column).Style.Font.Bold = true;
        var values = new[]
        {
            ("Safe", "#D9EAD3"),
            ("Reserved word — safely quoted", "#FFF2CC"),
            ("Long identifier or collision — automatically resolved", "#F4B183"),
            ("Blocking identifier conflict", "#C00000")
        };
        for (var index = 0; index < values.Length; index++)
        {
            var cell = sheet.Cell(row + index + 1, column);
            cell.Value = values[index].Item1;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(values[index].Item2);
            if (values[index].Item2 == "#C00000")
            {
                cell.Style.Font.FontColor = XLColor.White;
            }
        }
    }

    private static string UniqueName(XLWorkbook workbook, string desired)
    {
        var candidate = desired;
        var suffix = 2;
        while (workbook.Worksheets.Any(item =>
                   item.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            var marker = $" {suffix++}";
            candidate = desired[..Math.Min(desired.Length, 31 - marker.Length)] + marker;
        }
        return candidate;
    }
}
