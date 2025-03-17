using FluentAssertions.Execution;
using FluentAssertions;
using System.IO.Abstractions.TestingHelpers;
using GitDotNet.Readers;
using GitDotNet.Tests.Properties;
using GitDotNet.Tests.Helpers;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Readers;

public class IndexReaderTests
{
    [Test]
    public async Task Load()
    {
        // Arrange
        var resolver = CreateObjectResolver(hash => new BlobEntry(hash, [], _ => throw new NotImplementedException()));
        var reader = IndexReader.Load(fileSystem.CreateOffsetReader(".git/index"), resolver);
        using var sut = new Index(".git", resolver, (_, _) => reader, fileSystem);

        // Act
        var entries = await sut.GetEntriesAsync();

        // Assert
        using (new AssertionScope())
        {
            entries.Should().HaveCount(56);
            entries[0].Id.ToString().Should().Be("cbfaa61957324a6c5714d86008aac650adf19d24");
            entries[0].LastMetadataChange.Should().BeCloseTo(new DateTime(2025, 1, 10, 16, 59, 28, 659, 130), TimeSpan.FromMilliseconds(1));
            entries[0].LastDataChange.Should().Be(new DateTime(2023, 3, 23, 17, 52, 06));
            entries[0].Type.Should().Be(IndexEntryType.Regular);
            entries[0].UnixPermissions.Should().Be(420);
            entries[0].Path.Should().Be(".editorconfig");
            entries[0].FileSize.Should().Be(7823);
            var entry = await entries[0].GetEntryAsync();
            entry.Id.ToString().Should().Be("cbfaa61957324a6c5714d86008aac650adf19d24");
        }
    }

    private MockFileSystem fileSystem;

    [SetUp]
    public void Setup()
    {
        fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/index", new MockFileData(Resource.Index));
    }
}
