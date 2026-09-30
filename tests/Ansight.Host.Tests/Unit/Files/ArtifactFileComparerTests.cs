using Ansight.Host.Files;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Tests.Unit.Files;

public sealed class ArtifactFileComparerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "artifact-diff-tests-" + Guid.NewGuid().ToString("N"));
    public ArtifactFileComparerTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private async Task<ArtifactFileDiff> Compare(string before, string after, string format = "json", ArtifactDiffRequest? request = null)
    {
        var oldPath = Path.Combine(directory, "before"); var newPath = Path.Combine(directory, "after");
        await File.WriteAllTextAsync(oldPath, before); await File.WriteAllTextAsync(newPath, after);
        return await ArtifactFileComparer.CompareAsync(oldPath, newPath, "state." + format, format, request ?? new([]), default);
    }

    [Fact]
    public async Task JsonIgnoresPropertyOrderButDistinguishesMissingNullAndExactIntegers()
    {
        Assert.Equal("equivalent", (await Compare("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}")).Status);
        var changed = await Compare("{\"n\":null,\"id\":9007199254740992}", "{\"id\":9007199254740993}");
        Assert.Contains(changed.Changes, change => change.Path == "/n" && change.Kind == "removed" && change.Before == "null");
        Assert.Contains(changed.Changes, change => change.Path == "/id" && change.After == "9007199254740993");
    }

    [Fact]
    public async Task ArrayIdentityAvoidsPositionalNoiseAndRejectsDuplicateKeys()
    {
        var before = "[{\"id\":1,\"value\":10},{\"id\":2,\"value\":20}]";
        var after = "[{\"id\":2,\"value\":21},{\"id\":1,\"value\":10}]";
        var changed = await Compare(before, after, request: new([], ArrayKey: "id"));
        Assert.Equal("/2/value", Assert.Single(changed.Changes).Path);
        await Assert.ThrowsAsync<ArgumentException>(() => Compare(before, "[{\"id\":1},{\"id\":1}]", request: new([], ArrayKey: "id")));
    }

    [Fact]
    public async Task PaginationDoesNotClaimUnchangedWhenChangesAreOffPage()
    {
        var result = await Compare("{\"a\":1,\"b\":2}", "{\"a\":2,\"b\":3}", request: new([], Limit: 1));
        Assert.Equal(2, result.TotalChanges); Assert.Equal(1, result.NextOffset); Assert.True(result.IsComplete);
        var emptyPage = await Compare("{\"a\":1}", "{\"a\":2}", request: new([], Offset: 10));
        Assert.Empty(emptyPage.Changes); Assert.Equal("changed", emptyPage.Status);
    }

    [Fact]
    public async Task XmlResolvesNamespacesAndPreservesSignificantWhitespace()
    {
        Assert.Equal("equivalent", (await Compare("<a:root xmlns:a='urn:test' x='1'/>", "<b:root x='1' xmlns:b='urn:test'/>", "xml")).Status);
        Assert.Equal("changed", (await Compare("<root> a </root>", "<root>a</root>", "xml")).Status);
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => Compare("<!DOCTYPE root SYSTEM 'file:///etc/passwd'><root/>", "<root/>", "xml"));
    }

    [Fact]
    public async Task TextInsertionsDoNotMarkFollowingLinesAsChanged()
    {
        var result = await Compare("a\nb\nc", "a\nnew\nb\nc", "txt");
        var change = Assert.Single(result.Changes);
        Assert.Equal("added", change.Kind); Assert.Equal("new", change.After);
        Assert.Equal("equivalent", (await Compare(" a \r\n", "a\n", "txt", new([], IgnoreWhitespace: true))).Status);
    }

    [Fact]
    public async Task CsvMatchesQuotedMultilineFieldsByKey()
    {
        var result = await Compare("id,name\n1,\"hello,\nworld\"\n2,x\n", "id,name\n2,y\n1,\"hello,\nworld\"\n", "csv", new([], ArrayKey: "id"));
        Assert.Single(result.Changes); Assert.EndsWith("/name", result.Changes[0].Path);
    }

    [Fact]
    public async Task CsvComparesHeadersEvenWhenThereAreNoRows()
    {
        var result = await Compare("id,name\n", "id,title\n", "csv", new([], ArrayKey: "id"));
        Assert.Equal("changed", result.Status);
        Assert.Equal("/columns/1", Assert.Single(result.Changes).Path);
        await Assert.ThrowsAsync<ArgumentException>(() => Compare("id,name\n", "id,title\n", "csv", new([], ArrayKey: "missing")));
    }

    [Fact]
    public async Task OversizedFilesReportByteChangesWithoutComparingTruncatedText()
    {
        var before = Path.Combine(directory, "large-before"); var after = Path.Combine(directory, "large-after");
        foreach (var path in new[] { before, after })
        {
            await using var stream = File.Create(path);
            stream.SetLength(33 * 1024 * 1024);
            stream.WriteByte(path == before ? (byte)1 : (byte)2);
        }
        var result = await ArtifactFileComparer.CompareAsync(before, after, "large.txt", "txt", new([]), default);
        Assert.Equal("changed", result.Status);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Changes);
        Assert.Contains("no partial content", result.Message);
    }

    [Fact]
    public async Task DatabaseDetectsChangesBeyondPreviewLimitAndSameLengthBlobs()
    {
        FileVisualizationService.EnsureSqliteProviderInitialized();
        var before = Path.Combine(directory, "before.db"); var after = Path.Combine(directory, "after.db");
        foreach (var path in new[] { before, after })
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE data (tenant TEXT, id INTEGER, value, PRIMARY KEY(tenant,id)); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<600) INSERT INTO data SELECT 't',x,'unchanged' FROM n; UPDATE data SET value=x'0102' WHERE id=600;";
            await command.ExecuteNonQueryAsync();
            if (path == after) { command.CommandText = "UPDATE data SET value=x'0304' WHERE id=600; UPDATE data SET value=NULL WHERE id=599;"; await command.ExecuteNonQueryAsync(); }
        }
        var result = await ArtifactFileComparer.CompareAsync(before, after, "data.db", "sqlite", new([]), default);
        Assert.True(result.IsComplete, result.Message);
        Assert.Contains(result.Changes, change => change.Database?.BeforeRow?["value"]?["sha256"] is not null && change.Database.AfterRow?["value"]?["sha256"]?.ToString() != change.Database.BeforeRow["value"]?["sha256"]?.ToString());
        Assert.Contains(result.Changes, change => change.Database?.BeforeRow?["value"] is not null && change.Database.AfterRow?["value"] is null);
    }

    [Fact]
    public async Task DatabaseReportsRowAdditionsAndReversedRemovalsWithoutJsonPaths()
    {
        FileVisualizationService.EnsureSqliteProviderInitialized();
        var before = Path.Combine(directory, "areas-before.db");
        var after = Path.Combine(directory, "areas-after.db");
        foreach (var path in new[] { before, after })
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE SyncedArea (Id TEXT PRIMARY KEY, Name TEXT, IsDeleted INTEGER); INSERT INTO SyncedArea VALUES ('a','Existing',0);";
            await command.ExecuteNonQueryAsync();
            if (path == after)
            {
                command.CommandText = "INSERT INTO SyncedArea VALUES ('b','Castle Crag',0),('c','Tiger Wall',0),('d','Plaque',0);";
                await command.ExecuteNonQueryAsync();
            }
        }
        var forward = await ArtifactFileComparer.CompareAsync(before, after, "data.db", "sqlite", new([]), default);
        Assert.Equal(3, forward.TotalChanges);
        Assert.All(forward.Changes, change =>
        {
            Assert.Equal("added", change.Kind);
            Assert.Equal("SyncedArea", change.Database?.Table);
            Assert.Null(change.Database?.BeforeRow);
            Assert.NotNull(change.Database?.AfterRow?["Name"]);
            Assert.StartsWith("SyncedArea [Id=", change.Path);
            Assert.DoesNotContain("type", change.After!);
        });
        var reversed = await ArtifactFileComparer.CompareAsync(after, before, "data.db", "sqlite", new([]), default);
        Assert.All(reversed.Changes, change => Assert.Equal("removed", change.Kind));
    }

    [Fact]
    public async Task DatabasePreservesDuplicateRowsAndUsesCommonMatchingWhenKeysBecomeNullable()
    {
        FileVisualizationService.EnsureSqliteProviderInitialized();
        var before = Path.Combine(directory, "duplicates-before.db");
        var after = Path.Combine(directory, "duplicates-after.db");
        foreach (var path in new[] { before, after })
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE bags(value TEXT); INSERT INTO bags VALUES ('same'); CREATE TABLE nullable(Id TEXT PRIMARY KEY, value TEXT); INSERT INTO nullable VALUES ('a','same');";
            await command.ExecuteNonQueryAsync();
            if (path == after)
            {
                command.CommandText = "INSERT INTO bags VALUES ('same'); INSERT INTO nullable VALUES (NULL,'new');";
                await command.ExecuteNonQueryAsync();
            }
        }
        var result = await ArtifactFileComparer.CompareAsync(before, after, "data.db", "sqlite", new([]), default);
        Assert.Equal(2, result.TotalChanges);
        Assert.All(result.Changes, change => Assert.Equal("added", change.Kind));
        Assert.Contains(result.Changes, change => change.Database is { Table: "bags", BeforeCount: 1, AfterCount: 2 });
        Assert.Contains(result.Changes, change => change.Database is { Table: "nullable", BeforeCount: 0, AfterCount: 1 });
    }

    [Fact]
    public async Task ZipComparesMemberContentNotCompressionMetadata()
    {
        var before = Path.Combine(directory, "before.zip"); var after = Path.Combine(directory, "after.zip");
        foreach (var path in new[] { before, after })
        {
            using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("../../state.json").Open());
            writer.Write(path == before ? "{\"x\":1}" : "{\"x\":2}");
        }
        var result = await ArtifactFileComparer.CompareAsync(before, after, "bundle.zip", "zip", new([]), default);
        Assert.Equal("../../state.json:/x", Assert.Single(result.Changes).Path);
        Assert.False(File.Exists(Path.Combine(directory, "state.json")));
    }
}
