using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Readers;
using GitDotNet.Tests.Helpers;
using GitDotNet.Tests.Properties;
using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Readers;

public class LfsReaderTests
{
    [Test]
    public void TryLoad_ShouldReturnBlobEntryTypeAndDataProvider()
    {
        // Act
        var (type, dataProvider, length) = sut.TryLoad("1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");

        // Assert
        using (new AssertionScope())
        {
            type.Should().Be(EntryType.Blob);
            dataProvider.Should().NotBeNull();
            length.Should().Be(-1);
        }
    }

    [Test]
    public void TryLoad_ShouldReturnDefaultValuesWhenObjectNotFound()
    {
        // Act
        var (type, dataProvider, length) = sut.TryLoad("nonexistentobject");

        // Assert
        using (new AssertionScope())
        {
            type.Should().Be(default);
            dataProvider.Should().BeNull();
            length.Should().Be(-1);
        }
    }

    [Test]
    public void Load_ShouldReturnStreamWhenObjectExists()
    {
        // Act
        using var stream = sut.Load("1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        // Assert
        content.Should().Be("LFS object content");
    }

    [Test]
    public void Load_ShouldThrowInvalidOperationExceptionWhenObjectNotFound()
    {
        // Act
        Action act = () => sut.Load("nonexistentobject");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Object nonexistentobject could not be found in LFS. Make sure to run 'git lfs fetch' or 'git lfs fetch --all' for all branches and commits.");
    }

    private LfsReader sut;
    private MockFileSystem fileSystem;

    [SetUp]
    public void Setup()
    {
        fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/lfs/objects/12/34/1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef", new MockFileData("LFS object content"));
        sut = new LfsReader(".git/lfs/objects", fileSystem);
    }
}
