using System.IO.Abstractions.TestingHelpers;
using GitDotNet.Readers;
using GitDotNet.Tests.Properties;
using GitDotNet.Tests.Helpers;
using FakeItEasy;
using FluentAssertions.Execution;
using FluentAssertions;

namespace GitDotNet.Tests.Readers;

public class CommitGraphReaderTests
{
    [Test]
    public void GetCommitDetails()
    {
        // Arrange
        var sut = CommitGraphReader.Load(".git/objects",
                                         A.Fake<IObjectResolver>(),
                                         fileSystem,
                                         p => fileSystem.CreateOffsetReader(p))!;

        // Act
        var commit = sut.Get(new HashId("86fe932f320a5524668a9de2023ab4860601c67f"));

        // Assert
        using (new AssertionScope())
        {
            commit!.Id.ToString().Should().Be("86fe932f320a5524668a9de2023ab4860601c67f");
            commit.CommitTime.Should().Be(new DateTimeOffset(2025, 1, 1, 12, 16, 4, TimeSpan.Zero));
            commit._parentIds![0].ToString().Should().Be("7eb21b3c5d6444eb7ec0c98e0e50dff156393d47");
        }
    }

    private MockFileSystem fileSystem;

    [SetUp]
    public void Setup()
    {
        fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/info/commit-graph", new MockFileData(Resource.CommitGraph));
    }
}
