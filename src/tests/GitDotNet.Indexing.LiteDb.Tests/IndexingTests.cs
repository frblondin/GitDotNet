using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static GitDotNet.Tests.Helpers.DependencyInjectionProvider;

namespace GitDotNet.Indexing.Realm;

public class IndexingTests
{
    [Test]
    public async Task SimpleJsonPropertyIndexing()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        var provider = CreateProvider(out var services);
        using var connection = provider.Invoke($"{folder}/.git");
        using var sut = new GitIndexing(connection,
                                        Options.Create(new GitIndexing.Options
                                        {
                                            IndexTypes = [typeof(PageIndex)],
                                            IndexProviders = [IndexPageAsync],
                                        }),
                                        services.GetRequiredService<IFileSystem>());

        // Act
        var values = await (sut.SearchAsync<PageIndex>(connection.Head.Tip!, x => x.Name == "d0b04f8e-6a60-4a37-888b-64b6a62d0019")
            .ToListAsync());

        // Assert
        using (new AssertionScope())
        {
            values.Should().HaveCount(1);
            values[0].Path.Should().Be("Applications/f3agr4hyae6c/Pages/9i79gkozs9f7/9i79gkozs9f7.json");
        }
    }

    private static async IAsyncEnumerable<BlobIndex> IndexPageAsync(string path, TreeEntryItem blobEntry)
    {
        if (IsPageBlob(path))
        {
            yield break;
        }
        var blob = await blobEntry.GetEntryAsync<BlobEntry>();
        using var stream = blob.OpenRead();
        var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("$type", out var type) ||
            !type.ValueEquals("Models.Software.Table") ||
            !document.RootElement.TryGetProperty("name", out var name))
        {
            yield break;
        }
        yield return new PageIndex
        {
            Id = blobEntry.Id,
            Name = name.GetString()!,
        };
    }

    private static bool IsPageBlob(string path)
    {
        return Path.GetExtension(path) != ".json" ||
                    Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) != "Pages";
    }

    private static async IAsyncEnumerable<BlobIndex> IndexVisualBasicFile(string path, TreeEntryItem blobEntry)
    {
        if (Path.GetExtension(path) != ".vb")
        {
            yield break;
        }
        var blob = await blobEntry.GetEntryAsync<BlobEntry>();
        using var stream = blob.OpenRead();
        var buffer = new byte[1024];
        stream.ReadAtLeast(buffer.AsSpan(), buffer.Length, throwOnEndOfStream: false);
        var stringValue = Encoding.UTF8.GetString(buffer);
        yield return new VisualBasicFileIndex
        {
            Id = blobEntry.Id,
            UsesStrictOff = stringValue.Contains("Option Strict Off", StringComparison.OrdinalIgnoreCase),
        };
    }

    private class PageIndex : BlobIndex
    {
        public required string Name { get; set; }
    }

    private class VisualBasicFileIndex : BlobIndex
    {
        public bool UsesStrictOff { get; set; }
    }
}
