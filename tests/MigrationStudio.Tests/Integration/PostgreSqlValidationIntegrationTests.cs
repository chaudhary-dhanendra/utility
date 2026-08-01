using System.IO;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Validation;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Deployment;
using MigrationStudio.Infrastructure.Conversion;
using Npgsql;
using NpgsqlTypes;

namespace MigrationStudio.Tests.Integration;

public sealed class PostgreSqlValidationIntegrationTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task TemporalDefaults_CompileInsertAndPopulateBothTimestampTypes()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            const string sql =
                """
                CREATE TEMP TABLE migrationstudio_temporal_t1 (
                    created_at timestamp without time zone
                        DEFAULT timezone('UTC', CURRENT_TIMESTAMP)
                );
                CREATE TEMP TABLE migrationstudio_temporal_t2 (
                    created_at timestamptz
                        DEFAULT CURRENT_TIMESTAMP
                );
                """;
            await using (var create = new NpgsqlCommand(sql, connection, transaction))
            {
                await create.ExecuteNonQueryAsync();
            }
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO migrationstudio_temporal_t1 DEFAULT VALUES;
                INSERT INTO migrationstudio_temporal_t2 DEFAULT VALUES;
                SELECT
                    (SELECT created_at IS NOT NULL FROM migrationstudio_temporal_t1),
                    (SELECT created_at IS NOT NULL FROM migrationstudio_temporal_t2);
                """,
                connection,
                transaction);
            await using var reader = await insert.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task EmployeeAgeFunction_CompilesAndReturnsRepresentativeAges()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schema = $"employee_age_{Guid.NewGuid():N}"[..28];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var sql = $"""
                CREATE SCHEMA "{schema}";
                CREATE FUNCTION "{schema}".fn_employeeage(
                    p_dateofbirth date,
                    p_asofdate date)
                RETURNS integer
                LANGUAGE plpgsql
                AS $migrationstudio$
                BEGIN
                    IF p_dateofbirth IS NULL OR p_asofdate IS NULL THEN
                        RETURN NULL;
                    END IF;
                    RETURN
                        EXTRACT(YEAR FROM p_asofdate)::integer -
                        EXTRACT(YEAR FROM p_dateofbirth)::integer -
                        CASE
                            WHEN (p_dateofbirth +
                                (EXTRACT(YEAR FROM p_asofdate)::integer -
                                 EXTRACT(YEAR FROM p_dateofbirth)::integer) *
                                INTERVAL '1 year') > p_asofdate
                            THEN 1 ELSE 0
                        END;
                END;
                $migrationstudio$;
                """;
            await using (var create = new NpgsqlCommand(sql, connection, transaction))
            {
                await create.ExecuteNonQueryAsync();
            }
            await using var call = new NpgsqlCommand(
                $"SELECT \"{schema}\".fn_employeeage(DATE '2000-07-29', DATE '2026-07-28'), " +
                $"\"{schema}\".fn_employeeage(NULL, DATE '2026-07-28');",
                connection,
                transaction);
            await using var reader = await call.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(25, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ManualTableDependency_BlocksPrimaryKeyInsteadOfExecutingIt()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var tableId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Table, "cert", "documentstore", 850);
        var primaryKeyId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.PrimaryKey, "cert", "pk_documentstore", 851, tableId);
        var table = ValidationArtifact(
            tableId,
            "Table",
            "cert",
            "documentstore",
            DeploymentPhase.Tables,
            "-- Manual table definition required.",
            "manual-documentstore",
            []);
        table = table with
        {
            Classification = ConversionClassification.ManualConversion,
            RequiresManualReview = true
        };
        var primaryKey = ValidationArtifact(
            primaryKeyId,
            "PrimaryKey",
            "cert",
            "pk_documentstore",
            DeploymentPhase.PrimaryKeys,
            "ALTER TABLE cert.documentstore ADD CONSTRAINT pk_documentstore PRIMARY KEY(documentid);",
            "documentstore-pk",
            [tableId]);

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            [table, primaryKey],
            new PostgreSqlValidationOptions(connectionString!),
            CancellationToken.None);

        Assert.Equal(LiveSqlValidationOutcome.Manual, results[table.ContentHash].Outcome);
        Assert.Equal(
            LiveSqlValidationOutcome.BlockedByDependency,
            results[primaryKey.ContentHash].Outcome);
        Assert.Contains(tableId, results[primaryKey.ContentHash].BlockingDependencies);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ProductionScalarFunctionShapes_CompileAndFunctionPrecedesDependentCheck()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"function_{Guid.NewGuid():N}"[..26];
        var schemaId = InventoryObjectId.Create("fixture", InventoryObjectType.Schema, "", schemaName, 800);
        var detailsId = InventoryObjectId.Create("fixture", InventoryObjectType.Table, schemaName, "sau_details1617", 801);
        var calendarId = InventoryObjectId.Create("fixture", InventoryObjectType.Table, schemaName, "calendar", 802);
        var summaryId = InventoryObjectId.Create("fixture", InventoryObjectType.Table, schemaName, "sau_gp_level_summary_data", 803);
        var duplicateFunctionId = InventoryObjectId.Create("fixture", InventoryObjectType.Function, schemaName, "fnchksau_dupacc", 804);
        var duplicatePeriodId = InventoryObjectId.Create("fixture", InventoryObjectType.Function, schemaName, "sau_fndupperioddate", 805);
        var workingDaysId = InventoryObjectId.Create("fixture", InventoryObjectType.Function, schemaName, "fc_get_working_days_bank", 806);
        var laborDaysId = InventoryObjectId.Create("fixture", InventoryObjectType.Function, schemaName, "fc_get_labor_days", 807);
        var checkId = InventoryObjectId.Create("fixture", InventoryObjectType.CheckConstraint, schemaName, "chksau_dupacc", 808);
        var q = $"\"{schemaName}\"";
        var artifacts = new[]
        {
            ValidationArtifact(schemaId, "Schema", schemaName, schemaName, DeploymentPhase.Schemas,
                $"CREATE SCHEMA {q};", "function-schema", []),
            ValidationArtifact(detailsId, "Table", schemaName, "sau_details1617", DeploymentPhase.Tables,
                $"CREATE TABLE {q}.sau_details1617(acc_no varchar(18));", "function-details", [schemaId]),
            ValidationArtifact(calendarId, "Table", schemaName, "calendar", DeploymentPhase.Tables,
                $"CREATE TABLE {q}.calendar(bank_holiday char(1), holiday char(1), datevalue timestamp);",
                "function-calendar", [schemaId]),
            ValidationArtifact(summaryId, "Table", schemaName, "sau_gp_level_summary_data", DeploymentPhase.Tables,
                $"CREATE TABLE {q}.sau_gp_level_summary_data(" +
                "panchayat_code varchar(10), sa_period_from_date timestamp, sa_period_to_date timestamp);",
                "function-summary", [schemaId]),
            ValidationArtifact(duplicateFunctionId, "Function", schemaName, "fnchksau_dupacc", DeploymentPhase.Functions,
                $"CREATE FUNCTION {q}.fnchksau_dupacc(p_acc_no varchar(18)) RETURNS boolean " +
                "LANGUAGE plpgsql AS $body$ DECLARE v_return boolean; v_sql1 text; BEGIN " +
                "v_return := false; SELECT CASE WHEN count(1)>1 THEN true ELSE false END INTO v_return " +
                $"FROM {q}.sau_details1617 WHERE acc_no=p_acc_no; RETURN v_return; END; $body$;",
                "function-duplicate", [detailsId]),
            ValidationArtifact(duplicatePeriodId, "Function", schemaName, "sau_fndupperioddate", DeploymentPhase.Functions,
                $"CREATE FUNCTION {q}.sau_fndupperioddate(p_panchayat_code varchar(10), " +
                "p_sa_period_from_date timestamp, p_sa_period_to_date timestamp) RETURNS boolean " +
                "LANGUAGE plpgsql AS $body$ DECLARE v_dup_cnt boolean := false; BEGIN " +
                "SELECT CASE WHEN count(1)>=1 THEN true ELSE false END INTO v_dup_cnt " +
                $"FROM {q}.sau_gp_level_summary_data WHERE panchayat_code=p_panchayat_code " +
                "AND sa_period_from_date=p_sa_period_from_date AND sa_period_to_date=p_sa_period_to_date; " +
                "RETURN v_dup_cnt; END; $body$;",
                "function-period", [summaryId]),
            ValidationArtifact(workingDaysId, "Function", schemaName, "fc_get_working_days_bank", DeploymentPhase.Functions,
                $"CREATE FUNCTION {q}.fc_get_working_days_bank(p_from timestamp, p_to timestamp) RETURNS integer " +
                $"LANGUAGE sql AS $body$ SELECT COALESCE(count(1),0)::integer FROM {q}.calendar " +
                "WHERE bank_holiday='Y' AND datevalue > p_from AND datevalue <= p_to; $body$;",
                "function-working", [calendarId]),
            ValidationArtifact(laborDaysId, "Function", schemaName, "fc_get_labor_days", DeploymentPhase.Functions,
                $"CREATE FUNCTION {q}.fc_get_labor_days(p_from timestamp, p_to timestamp) RETURNS integer " +
                $"LANGUAGE sql AS $body$ SELECT count(1)::integer FROM {q}.calendar " +
                "WHERE holiday='Y' AND datevalue > p_from AND datevalue <= p_to; $body$;",
                "function-labor", [calendarId]),
            ValidationArtifact(checkId, "CheckConstraint", schemaName, "chksau_dupacc", DeploymentPhase.CheckConstraints,
                $"ALTER TABLE {q}.sau_details1617 ADD CONSTRAINT chksau_dupacc " +
                $"CHECK ({q}.fnchksau_dupacc(acc_no) = false);",
                "function-check", [detailsId, duplicateFunctionId])
        };

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            artifacts,
            new PostgreSqlValidationOptions(connectionString!),
            CancellationToken.None);

        Assert.All(results.Values, result =>
            Assert.Equal(LiveSqlValidationOutcome.Passed, result.Outcome));
        Assert.Contains(duplicateFunctionId, artifacts[^1].Dependencies);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task CertStateTemporalFix_RetriesBlockedCheckAndPublishesValidatedPackage()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"cert_{Guid.NewGuid():N}"[..24];
        var schemaId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Schema, string.Empty, schemaName, 901);
        var stateId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Table, schemaName, "state", 902);
        var checkId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.CheckConstraint, schemaName, "ck_state_dates", 903, stateId);
        var schema = ValidationArtifact(
            schemaId,
            "Schema",
            schemaName,
            schemaName,
            DeploymentPhase.Schemas,
            $"CREATE SCHEMA {schemaName};",
            "cert-schema",
            []);
        var brokenState = ValidationArtifact(
            stateId,
            "Table",
            schemaName,
            "state",
            DeploymentPhase.Tables,
            $"CREATE TABLE {schemaName}.state(" +
            "state_id integer PRIMARY KEY, " +
            "effective_from timestamp without time zone NOT NULL DEFAULT sysutcdatetime(), " +
            "effective_to timestamp without time zone);",
            "cert-state-broken",
            [schemaId]);
        var check = ValidationArtifact(
            checkId,
            "constraint",
            schemaName,
            "ck_state_dates",
            DeploymentPhase.CheckConstraints,
            $"ALTER TABLE {schemaName}.state ADD CONSTRAINT ck_state_dates " +
            "CHECK (effective_to IS NULL OR effective_to >= effective_from);",
            "cert-check",
            [stateId]);

        var firstResults = await new GeneratedSqlValidator().ValidateLiveAsync(
            [schema, brokenState, check],
            new PostgreSqlValidationOptions(connectionString!),
            CancellationToken.None);
        Assert.Equal(
            LiveSqlValidationOutcome.Failed,
            firstResults[brokenState.ContentHash].Outcome);
        Assert.Equal(
            LiveSqlValidationOutcome.BlockedByDependency,
            firstResults[check.ContentHash].Outcome);

        var correctedState = brokenState with
        {
            PostgreSqlDefinition =
                $"CREATE TABLE {schemaName}.state(" +
                "state_id integer PRIMARY KEY, " +
                "effective_from timestamp without time zone NOT NULL " +
                "DEFAULT timezone('UTC', CURRENT_TIMESTAMP), " +
                "effective_to timestamp without time zone);",
            ContentHash = "cert-state-corrected"
        };
        var reusable = firstResults
            .Where(item => item.Value.Outcome == LiveSqlValidationOutcome.Passed)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var correctedArtifacts = new[] { schema, correctedState, check };
        var secondResults = await new GeneratedSqlValidator().ValidateLiveAsync(
            correctedArtifacts,
            new PostgreSqlValidationOptions(connectionString!)
            {
                ReusableSuccessfulResults = reusable
            },
            CancellationToken.None);

        Assert.Equal(
            LiveSqlValidationOutcome.Passed,
            secondResults[correctedState.ContentHash].Outcome);
        Assert.Equal(
            LiveSqlValidationOutcome.Passed,
            secondResults[check.ContentHash].Outcome);
        Assert.Contains(stateId, check.Dependencies);

        var validated = correctedArtifacts.Select(item =>
            item with { Validation = secondResults[item.ContentHash] }).ToArray();
        var run = new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "certification",
            new PostgreSqlVersion(14),
            new ConversionOptions(),
            [],
            [],
            validated,
            [],
            [],
            "integration-test");
        var root = Path.Combine(
            Path.GetTempPath(),
            $"MigrationStudio-CertTemporal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var package = await new MigrationPackageWriter(new EmptyReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            var manifest = await new MigrationPackageReader().ReadAndVerifyAsync(
                package,
                false,
                CancellationToken.None);

            Assert.Equal(validated.Length, manifest.Artifacts.Count);
            Assert.All(
                manifest.Artifacts.Where(item => item.IsExecutable),
                item => Assert.Equal(
                    LiveSqlValidationOutcome.Passed,
                    item.LiveValidation.Outcome));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ValidatesGeneratedSqlInsideRolledBackTransaction()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var id = InventoryObjectId.Create("fixture", InventoryObjectType.Table, "public", "validation_probe", 1);
        var artifact = new ConversionArtifact(
            id,
            new TargetObjectIdentifier("Table", "pg_temp", "migrationstudio_probe"),
            "catalog fixture",
            "CREATE TEMP TABLE migrationstudio_probe(id integer PRIMARY KEY);",
            ConversionClassification.Automatic,
            "TEST.POSTGRESQL",
            1m,
            [],
            [],
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            DeploymentPhase.Tables,
            "05_Tables.sql",
            "fixture");

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            [artifact],
            new PostgreSqlValidationOptions(connectionString!)
            {
                PreferDisposableDatabase = false
            },
            CancellationToken.None);

        Assert.True(results["fixture"].WasLiveValidated);
        Assert.True(results["fixture"].IsStructurallyValid);
        Assert.Equal(
            LiveSqlValidationConfidence.RollbackTransaction,
            results["fixture"].Confidence);
        Assert.Equal(LiveSqlValidationOutcome.Passed, results["fixture"].Outcome);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ContinuesIndependentArtifactsAndBlocksFailedDependents()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var failedId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Table, "public", "failed", 10);
        var blockedId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.View, "public", "blocked", 11);
        var independentId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Table, "public", "independent", 12);
        var failed = Artifact(
            failedId,
            "failed-hash",
            "CREATE TABLE migrationstudio_invalid(id definitely_not_a_postgresql_type);",
            []);
        var blocked = Artifact(
            blockedId,
            "blocked-hash",
            "CREATE VIEW migrationstudio_blocked AS SELECT 1;",
            [failedId]);
        var independent = Artifact(
            independentId,
            "independent-hash",
            "CREATE TEMP TABLE migrationstudio_independent(id integer);",
            []);

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            [failed, blocked, independent],
            new PostgreSqlValidationOptions(connectionString!)
            {
                PreferDisposableDatabase = false
            },
            CancellationToken.None);

        Assert.Equal(LiveSqlValidationOutcome.Failed, results["failed-hash"].Outcome);
        Assert.False(string.IsNullOrWhiteSpace(results["failed-hash"].SqlState));
        Assert.Equal(
            LiveSqlValidationOutcome.BlockedByDependency,
            results["blocked-hash"].Outcome);
        Assert.Contains(failedId, results["blocked-hash"].BlockingDependencies);
        Assert.Equal(LiveSqlValidationOutcome.Passed, results["independent-hash"].Outcome);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ConversionRegressionSql_ValidatesWithoutPreviousSqlStates()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"conversion_{Guid.NewGuid():N}"[..28];
        var schema = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Schema, string.Empty, schemaName, 100);
        var parent = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Table, schemaName, "mapped_parent", 101);
        var child = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Table, schemaName, "mapped_child", 102);
        var primaryKey = InventoryObjectId.Create(
            "fixture", InventoryObjectType.PrimaryKey, schemaName, "mapped_parent_pk", 103, parent);
        var unique = InventoryObjectId.Create(
            "fixture", InventoryObjectType.UniqueConstraint, schemaName, "mapped_parent_name_uq", 104, parent);
        var check = InventoryObjectId.Create(
            "fixture", InventoryObjectType.CheckConstraint, schemaName, "mapped_child_value_ck", 105, child);
        var sequence = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Sequence, schemaName, "mapped_sequence", 106);
        var foreignKey = InventoryObjectId.Create(
            "fixture", InventoryObjectType.ForeignKey, schemaName, "mapped_child_parent_fk", 107, child);
        var index = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Index, schemaName, "mapped_child_parent_ix", 108, child);
        var function = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Function, schemaName, "mapped_function", 109);
        var procedure = InventoryObjectId.Create(
            "fixture", InventoryObjectType.StoredProcedure, schemaName, "mapped_procedure", 110);
        var view = InventoryObjectId.Create(
            "fixture", InventoryObjectType.View, schemaName, "mapped_view", 111);
        var qualifiedSchema = $"\"{schemaName}\"";
        var artifacts = new[]
        {
            Create(schema, "Schema", DeploymentPhase.Schemas,
                $"CREATE SCHEMA {qualifiedSchema};", []),
            Create(parent, "Table", DeploymentPhase.Tables,
                $"CREATE TABLE {qualifiedSchema}.mapped_parent(" +
                "mapped_id integer NOT NULL, mapped_name text NOT NULL, " +
                "mapped_created_at timestamptz NOT NULL DEFAULT timezone('UTC', now()));",
                [schema]),
            Create(child, "Table", DeploymentPhase.Tables,
                $"CREATE TABLE {qualifiedSchema}.mapped_child(" +
                "mapped_id integer NOT NULL, mapped_parent_id integer NOT NULL, mapped_value integer NOT NULL);",
                [schema]),
            Create(primaryKey, "PrimaryKey", DeploymentPhase.PrimaryKeys,
                $"ALTER TABLE {qualifiedSchema}.mapped_parent ADD CONSTRAINT mapped_parent_pk PRIMARY KEY (mapped_id);",
                [parent]),
            Create(unique, "UniqueConstraint", DeploymentPhase.UniqueConstraints,
                $"ALTER TABLE {qualifiedSchema}.mapped_parent ADD CONSTRAINT mapped_parent_name_uq UNIQUE (mapped_name);",
                [parent]),
            Create(check, "CheckConstraint", DeploymentPhase.CheckConstraints,
                $"ALTER TABLE {qualifiedSchema}.mapped_child ADD CONSTRAINT mapped_child_value_ck CHECK (mapped_value >= 0);",
                [child]),
            Create(sequence, "Sequence", DeploymentPhase.Sequences,
                $"CREATE SEQUENCE {qualifiedSchema}.mapped_sequence;", [schema]),
            Create(foreignKey, "ForeignKey", DeploymentPhase.ForeignKeys,
                $"ALTER TABLE {qualifiedSchema}.mapped_child ADD CONSTRAINT mapped_child_parent_fk " +
                $"FOREIGN KEY (mapped_parent_id) REFERENCES {qualifiedSchema}.mapped_parent(mapped_id);",
                [child, parent, primaryKey]),
            Create(index, "Index", DeploymentPhase.Indexes,
                $"CREATE INDEX mapped_child_parent_ix ON {qualifiedSchema}.mapped_child(mapped_parent_id);",
                [child]),
            Create(function, "Function", DeploymentPhase.Functions,
                $"CREATE FUNCTION {qualifiedSchema}.mapped_function() RETURNS integer " +
                "LANGUAGE sql IMMUTABLE AS $$ SELECT 1 $$;", [schema]),
            Create(procedure, "Procedure", DeploymentPhase.Procedures,
                $"CREATE PROCEDURE {qualifiedSchema}.mapped_procedure() " +
                "LANGUAGE plpgsql AS $$ BEGIN NULL; END; $$;", [schema]),
            Create(view, "View", DeploymentPhase.Views,
                $"CREATE VIEW {qualifiedSchema}.mapped_view AS " +
                $"SELECT {qualifiedSchema}.mapped_function() AS mapped_value;", [function])
        };

        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            artifacts,
            new PostgreSqlValidationOptions(connectionString!)
            {
                PreferDisposableDatabase = false
            },
            CancellationToken.None);

        Assert.Equal(artifacts.Length, results.Count);
        Assert.All(results.Values, result =>
        {
            Assert.Equal(LiveSqlValidationOutcome.Passed, result.Outcome);
            Assert.True(result.WasLiveValidated);
            Assert.True(result.IsStructurallyValid);
            Assert.Null(result.SqlState);
        });

        static ConversionArtifact Create(
            InventoryObjectId id,
            string objectType,
            DeploymentPhase phase,
            string sql,
            IReadOnlyList<InventoryObjectId> dependencies) =>
            new(
                id,
                new TargetObjectIdentifier(objectType, "public", id.ToString()),
                "conversion regression fixture",
                sql,
                ConversionClassification.Automatic,
                "TEST.CONVERSION.REGRESSION",
                1m,
                [],
                dependencies,
                [],
                [],
                false,
                [],
                new SqlValidationResult(true, false, null, null, null),
                phase,
                $"{phase}.sql",
                id.ToString());
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ReservedIdentifiers_AreCreatedCopiedIndexedReferencedViewedAndValidated()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schema = $"identifier_{Guid.NewGuid():N}"[..24];
        var parent = Object("dbo", "parent", 1, InventoryObjectType.Table);
        var order = Object("dbo", "order", 2, InventoryObjectType.Table);
        var view = Object("dbo", "reserved_view", 3, InventoryObjectType.View);
        var snapshot = TestInventory.CreateSnapshot([parent, order, view]) with
        {
            Schemas = [Schema("dbo")]
        };
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            snapshot,
            new ConversionOptions
            {
                SchemaMappingMode = SchemaMappingMode.Custom,
                SchemaMappings = [new SchemaMappingRule("dbo", schema)]
            });
        var parentTarget = mapper.MapObject(parent);
        var orderTarget = mapper.MapObject(order);
        var viewTarget = mapper.MapObject(view);
        var parentUser = mapper.MapChildIdentifier(parent.Id, "column", "dbo", "user");
        var orderUser = mapper.MapChildIdentifier(order.Id, "column", "dbo", "user");
        var freeze = mapper.MapChildIdentifier(order.Id, "column", "dbo", "freeze");
        var foreignKey = mapper.MapChildIdentifier(order.Id, "constraint", "dbo", "FK_order_user");
        var index = mapper.MapChildIdentifier(order.Id, "index", "dbo", "IX_order_user");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var sql = $"""
                CREATE SCHEMA {mapper.QuoteIdentifier(schema)};
                CREATE TABLE {parentTarget.QualifiedName} ({parentUser} integer PRIMARY KEY);
                CREATE TABLE {orderTarget.QualifiedName} ({freeze} text NOT NULL, {orderUser} integer NOT NULL);
                ALTER TABLE {orderTarget.QualifiedName} ADD CONSTRAINT {foreignKey}
                    FOREIGN KEY ({orderUser}) REFERENCES {parentTarget.QualifiedName} ({parentUser});
                CREATE INDEX {index} ON {orderTarget.QualifiedName} ({orderUser});
                CREATE VIEW {viewTarget.QualifiedName} AS
                    SELECT {freeze}, {orderUser} FROM {orderTarget.QualifiedName};
                """;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = new NpgsqlCommand(
                             $"INSERT INTO {parentTarget.QualifiedName} ({parentUser}) VALUES (7)",
                             connection,
                             transaction))
            {
                await command.ExecuteNonQueryAsync();
            }
            await using (var importer = await connection.BeginBinaryImportAsync(
                             $"COPY {orderTarget.QualifiedName} ({freeze}, {orderUser}) FROM STDIN (FORMAT BINARY)"))
            {
                await importer.StartRowAsync();
                await importer.WriteAsync("frozen", NpgsqlDbType.Text);
                await importer.WriteAsync(7, NpgsqlDbType.Integer);
                await importer.CompleteAsync();
            }
            await using (var command = new NpgsqlCommand(
                             $"SELECT {freeze}, {orderUser} FROM {viewTarget.QualifiedName}",
                             connection,
                             transaction))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("frozen", reader.GetString(0));
                Assert.Equal(7, reader.GetInt32(1));
            }
            await using (var command = new NpgsqlCommand(
                             "SELECT relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace " +
                             "WHERE n.nspname=@schema AND c.relname=@name",
                             connection,
                             transaction))
            {
                command.Parameters.AddWithValue("schema", schema);
                command.Parameters.AddWithValue("name", Unquote(orderTarget.Name));
                Assert.Equal(Unquote(orderTarget.Name), await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static InventoryObject Object(
        string schema,
        string name,
        int sqlId,
        InventoryObjectType type)
    {
        var id = InventoryObjectId.Create("fixture", type, schema, name, sqlId);
        return new InventoryObject(
            id, "fixture", schema, name, $"[{schema}].[{name}]", type, sqlId,
            null, null, null, false, true, SelectionReason.CompleteDatabase, 0, 0, [],
            ConversionClassification.Automatic, null, null, "hash", [], DiscoveryStatus.Discovered);
    }

    private static SchemaInventory Schema(string name)
    {
        var item = Object(string.Empty, name, 0, InventoryObjectType.Schema);
        return new SchemaInventory(item, "dbo", 1, false, true);
    }

    private static string Unquote(string identifier) =>
        identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : identifier;

    private static ConversionArtifact Artifact(
        InventoryObjectId id,
        string hash,
        string sql,
        IReadOnlyList<InventoryObjectId> dependencies) =>
        new(
            id,
            new TargetObjectIdentifier("Table", "pg_temp", hash),
            "fixture",
            sql,
            ConversionClassification.Automatic,
            "TEST.POSTGRESQL",
            1m,
            [],
            dependencies,
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            DeploymentPhase.Tables,
            "05_Tables.sql",
            hash);

    private static ConversionArtifact ValidationArtifact(
        InventoryObjectId id,
        string objectType,
        string schema,
        string name,
        DeploymentPhase phase,
        string sql,
        string hash,
        IReadOnlyList<InventoryObjectId> dependencies) =>
        new(
            id,
            new TargetObjectIdentifier(objectType, schema, name),
            "fixture",
            sql,
            ConversionClassification.Automatic,
            "TEST.CERT.TEMPORAL",
            1m,
            [],
            dependencies,
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            phase,
            phase switch
            {
                DeploymentPhase.Schemas => "02_Schemas.sql",
                DeploymentPhase.Tables => "05_Tables.sql",
                DeploymentPhase.CheckConstraints => "09_CheckConstraints.sql",
                _ => $"{phase}.sql"
            },
            hash);

    private sealed class EmptyReportWriter : IConversionReportWriter
    {
        public Task WriteAsync(
            ConversionRun run,
            string directory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            return Task.CompletedTask;
        }
    }
}

internal sealed class PostgreSqlIntegrationFactAttribute : FactAttribute
{
    public PostgreSqlIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION")))
        {
            Skip = "Set MIGRATIONSTUDIO_POSTGRES_INTEGRATION to run live PostgreSQL validation.";
        }
    }
}
