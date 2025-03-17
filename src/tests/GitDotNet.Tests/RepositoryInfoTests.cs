using System.IO.Abstractions;
using FakeItEasy;
using FluentAssertions;
using GitDotNet.Readers;
using GitDotNet.Tools;

namespace GitDotNet.Tests;
internal class RepositoryInfoTests
{
    [Test]
    public void GetRepositoryPath()
    {
        // Arrange
        var configReader = A.Fake<ConfigReader>(o => o.ConfigureFake(f =>
            A.CallTo(() => f.IsBare).Returns(false)));
        var cliCommand = A.Fake<GitCliCommand>(o => o.ConfigureFake(f =>
            A.CallTo(() => f.GetAbsoluteGitPath(A<string>._)).Returns(@"C:\\repository\\.git")));
        var sut = new RepositoryInfo(@"C:\repository", _ => configReader, new FileSystem(), cliCommand);

        // Act
        var path = sut.GetRepositoryPath(@"C:\repository\A\B\foo.exe");

        // Assert
        path.ToString().Should().Be("A/B/foo.exe");
    }
}
