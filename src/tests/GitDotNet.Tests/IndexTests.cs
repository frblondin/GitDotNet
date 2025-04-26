using System.IO.Abstractions.TestingHelpers;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using static GitDotNet.Tests.Helpers.DependencyInjectionProvider;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Readers;

public class IndexTests
{
    [Test]
    public async Task Load()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/index", new MockFileData(Resource.Index));
        var resolver = CreateObjectResolver(hash => new BlobEntry(hash, [], _ => throw new NotImplementedException()));
        var reader = new IndexReader(".git/index", resolver, fileSystem);
        var info = A.Fake<IRepositoryInfo>(o => o.ConfigureFake(i =>
        {
            A.CallTo(() => i.Path).Returns(".git");
        }));
        var sut = new Index(info, resolver, EmptyLock, (_, _) => reader, fileSystem);

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
            entries[0].Path.ToString().Should().Be(".editorconfig");
            entries[0].FileSize.Should().Be(7823);
            var entry = await entries[0].GetEntryAsync<BlobEntry>();
            entry.Id.ToString().Should().Be("cbfaa61957324a6c5714d86008aac650adf19d24");
        }
    }

    [Test]
    public async Task AddEntry()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder);
        using var connection = CreateProvider().Invoke(folder, isWrite: true);
        var sut = connection.Index;

        // Act
        sut.AddEntry(Encoding.UTF8.GetBytes("foo"), "test.txt", FileMode.RegularFile);

        // Assert
        using (new AssertionScope())
        {
            var entries = await sut.GetEntriesAsync();
            entries.Should().HaveCount(1);
            entries[0].Path.ToString().Should().Be("test.txt");
            entries[0].Type.Should().Be(IndexEntryType.Regular);
            entries[0].UnixPermissions.Should().Be(420);
            var blob = await entries[0].GetEntryAsync<BlobEntry>();
            blob.GetText().Should().Be("foo");
        }
    }
}
