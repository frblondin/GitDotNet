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
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/info/commit-graph", new MockFileData(Resource.CommitGraph));
        using var sut = CommitGraphReader.Load(".git/objects",
            A.Fake<IObjectResolver>(),
            fileSystem,
            p => fileSystem.CreateOffsetReader(p))!;

        // Act
        var commit = sut.Get("86fe932f320a5524668a9de2023ab4860601c67f");

        // Assert
        using (new AssertionScope())
        {
            commit!.Id.ToString().Should().Be("86fe932f320a5524668a9de2023ab4860601c67f");
            commit.CommitTime.Should().Be(new DateTimeOffset(2025, 1, 1, 12, 16, 4, TimeSpan.Zero));
            commit.ParentIds![0].ToString().Should().Be("7eb21b3c5d6444eb7ec0c98e0e50dff156393d47");
        }
    }

    [Test]
    public void GetMergeCommitDetails()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/info/commit-graph", new MockFileData(Resource.MergeCommitGraph));
        using var sut = CommitGraphReader.Load(".git/objects",
                                         A.Fake<IObjectResolver>(),
                                         fileSystem,
                                         p => fileSystem.CreateOffsetReader(p))!;

        // Act
        var commit = sut.Get("2e18708dc062f5d1c9f94e82b39081f9bdc17d82");

        // Assert
        using (new AssertionScope())
        {
            commit!.Id.ToString().Should().Be("2e18708dc062f5d1c9f94e82b39081f9bdc17d82");
            commit.CommitTime.Should().Be(new DateTimeOffset(2025, 3, 11, 13, 58, 08, TimeSpan.Zero));
            commit.ParentIds!.Count.Should().Be(2);
            commit.ParentIds![0].ToString().Should().Be("e159d393542c45cb945e892d6245fb9647e9df73");
            commit.ParentIds![1].ToString().Should().Be("57e779b92132f469060e6aaf2c5d61bb687e5c09");
        }
    }

    [Test]
    public void GetMultiParentMergeCommitDetails()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(".git/objects/info/commit-graph", new MockFileData(Resource.MultiParentMergeCommitGraph));
        using var sut = CommitGraphReader.Load(".git/objects",
                                               A.Fake<IObjectResolver>(),
                                               fileSystem,
                                               p => fileSystem.CreateOffsetReader(p))!;

        // Act
        var commit = sut.Get("36010d7f7c4503ff54ba5989cbb0404ae989b5e7");

        // Assert
        using (new AssertionScope())
        {
            commit!.Id.ToString().Should().Be("36010d7f7c4503ff54ba5989cbb0404ae989b5e7");
            commit.CommitTime.Should().Be(new DateTimeOffset(2025, 3, 11, 14, 42, 54, TimeSpan.Zero));
            commit.ParentIds!.Count.Should().Be(3);
            commit.ParentIds![0].ToString().Should().Be("e159d393542c45cb945e892d6245fb9647e9df73");
            commit.ParentIds![1].ToString().Should().Be("57e779b92132f469060e6aaf2c5d61bb687e5c09");
            commit.ParentIds![2].ToString().Should().Be("a28d9681fdf40631632a42b303be274e3869d5d5");
        }
    }
}
