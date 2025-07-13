using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Readers;
using GitDotNet.Tests.Properties;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static GitDotNet.Tests.Helpers.DependencyInjectionProvider;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests;

public class ObjectsTests
{
    [Test]
    public async Task UsesLooseObjectReaderIfFileExists()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(".git");
        var looseReader = A.Fake<LooseReader>(o =>
        {
            o.ConfigureFake(fake =>
                A.CallTo(() => fake.TryLoad("0123456789abcdef0123456789abcdef01234567"))
                 .Returns((EntryType.Blob, () => new MemoryStream([42]), 1)));
        });
        var sut = new ObjectResolver(".git", useReadCommitGraph: true,
            options: Options.Create(new GitConnection.Options()),
            packManager: null!,
            looseReaderFactory: _ => looseReader,
            packReaderFactory: _ => throw new NotImplementedException(),
            lfsReaderFactory: path => CreateLfsReader(path, fileSystem),
            commitReaderFactory: (_, _) => throw new NotImplementedException(),
            memoryCache: A.Fake<IMemoryCache>(),
            fileSystem: fileSystem);

        // Act
        var entry = await sut.GetAsync<BlobEntry>("0123456789abcdef0123456789abcdef01234567");

        // Assert
        entry.Data.Should().Equal([42]);
    }

    [Test]
    public async Task TryGetNonExistingCommitReturnsNull()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        ZipFile.ExtractToDirectory(new MemoryStream(Resource.CompleteRepository), folder, overwriteFiles: true);
        using var sut = CreateServiceProvider().GetRequiredService<ObjectResolverFactory>().Invoke(folder, true);

        // Act
        var commit = await sut.TryGetAsync<CommitEntry>(HashId.Empty);

        // Assert
        commit.Should().BeNull();
    }
}
