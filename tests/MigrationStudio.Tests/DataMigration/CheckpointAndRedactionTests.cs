using System.IO;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Platform;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.DataMigration;

namespace MigrationStudio.Tests.DataMigration;

public sealed class CheckpointAndRedactionTests
{
    [Fact]
    public async Task Checkpoint_RoundTripsAndTableRestartRemovesOnlySelectedTable()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new JsonMigrationCheckpointStore(new TestPaths(root));
            var runId = Guid.NewGuid();
            var first = TableCheckpoint("dbo.First");
            var second = TableCheckpoint("dbo.Second");
            var checkpoint = new MigrationCheckpoint(
                MigrationCheckpoint.CurrentFormatVersion,
                runId,
                "source",
                "metadata",
                "target",
                "configuration",
                "1.0",
                DateTimeOffset.UtcNow,
                [first, second]);

            var path = await store.SaveAsync(checkpoint, CancellationToken.None);
            var loaded = await store.LoadAsync(runId, CancellationToken.None);
            await store.DeleteTableAsync(runId, first.TableId, CancellationToken.None);
            var restarted = await store.LoadAsync(runId, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.NotNull(loaded);
            Assert.Equal(checkpoint.RunId, loaded.RunId);
            Assert.Equal(checkpoint.SourceMetadataHash, loaded.SourceMetadataHash);
            Assert.Equal(checkpoint.ConfigurationHash, loaded.ConfigurationHash);
            Assert.Equal(checkpoint.Tables, loaded.Tables);
            Assert.DoesNotContain(restarted!.Tables, item => item.TableId == first.TableId);
            Assert.Contains(restarted.Tables, item => item.TableId == second.TableId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FailedRowExport_MasksSensitiveAndDoesNotEmitBinaryPayload()
    {
        const string secret = "$2b$12$never-write-this-hash";
        var bytes = Convert.FromHexString("DEADBEEFCAFEBABE");
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exporter = new FailedRowExporter(new TestPaths(root));
            FailedRowRecord[] rows =
            [
                new(
                    "dbo.Users",
                    "42",
                    new Dictionary<string, FailedRowValue>
                    {
                        ["PasswordHash"] = new(secret, true, false),
                        ["Ciphertext"] = new(bytes, false, true)
                    },
                    "conversion failed")
            ];

            var json = await exporter.ExportJsonAsync(Guid.NewGuid(), rows, false, CancellationToken.None);
            var csv = await exporter.ExportCsvAsync(Guid.NewGuid(), rows, false, CancellationToken.None);
            var output = await File.ReadAllTextAsync(json) + await File.ReadAllTextAsync(csv);

            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(bytes), output, StringComparison.Ordinal);
            Assert.Contains("***MASKED***", output, StringComparison.Ordinal);
            Assert.Contains("BINARY length=8", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("refresh_token")]
    [InlineData("APIKey")]
    [InlineData("private-key")]
    [InlineData("PIN")]
    public void SensitiveClassifier_RecognizesDefaultPatterns(string name)
    {
        var classifier = new SensitiveColumnClassifier();
        Assert.True(classifier.IsSensitive(Column(name), new SensitiveDataOptions()));
    }

    [Fact]
    public void SensitiveClassifier_DoesNotExcludeOrMutateColumn()
    {
        var column = Column("PasswordHash");
        var classifier = new SensitiveColumnClassifier();

        Assert.True(classifier.IsSensitive(column, new SensitiveDataOptions()));
        Assert.Equal("PasswordHash", column.Name);
        Assert.False(column.IsComputed);
    }

    private static TableCheckpoint TableCheckpoint(string name) =>
        new(
            new InventoryObjectId(Guid.NewGuid()),
            name,
            name.ToLowerInvariant(),
            DataTransferStrategy.PostgreSqlBinaryCopy,
            4,
            "I100",
            100,
            100,
            0,
            null,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null,
            TableMigrationState.Running,
            true);

    private static ColumnInventory Column(string name) =>
        new(
            ObjectId: new InventoryObjectId(Guid.NewGuid()),
            ParentObjectId: new InventoryObjectId(Guid.NewGuid()),
            ColumnId: 1,
            OrdinalPosition: 1,
            Name: name,
            SystemTypeName: "nvarchar",
            UserTypeName: "nvarchar",
            TypeSchema: "sys",
            MaximumLength: 400,
            Precision: 0,
            Scale: 0,
            Collation: null,
            IsNullable: true,
            IsIdentity: false,
            IdentitySeed: null,
            IdentityIncrement: null,
            IdentityLastValue: null,
            IsIdentityNotForReplication: false,
            IsComputed: false,
            ComputedDefinition: null,
            IsComputedPersisted: false,
            IsComputedDeterministic: null,
            IsSparse: false,
            IsColumnSet: false,
            IsRowGuidColumn: false,
            IsFileStream: false,
            GeneratedAlwaysType: 0,
            IsHidden: false,
            IsMasked: false,
            MaskingFunction: null,
            EncryptionType: null,
            EncryptionAlgorithm: null,
            ColumnEncryptionKey: null,
            XmlSchemaCollection: null,
            DefaultConstraintName: null,
            DefaultDefinition: null,
            RuleName: null,
            ExtendedProperties: []);

    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ApplicationDataDirectory { get; } = root;

        public string LogsDirectory { get; } = Path.Combine(root, "Logs");

        public string PluginsDirectory { get; } = Path.Combine(root, "Plugins");

        public string SettingsFilePath { get; } = Path.Combine(root, "settings.json");
    }
}
