using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Tests.Helpers;

namespace GitDotNet.Tests;

public class SubModuleTests
{
    [Test]
    public async Task CreateGitLink()
    {
        // Arrange
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, TestContext.CurrentContext.Test.Name);
        TestUtils.ForceDeleteDirectory(folder);
        GitConnection.Create(folder, isBare: true);
        using var sut = GitConnectionTests.CreateProvider().Invoke(folder);

        // Act
        var commit = await sut.CommitAsync("main",
            c => c.AddOrUpdate("Link", "60725d77a64d73566a4c72e27128d76fbb7769a4", new FileMode("160000")),
            sut.CreateCommit("Commit message",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        // Act, Assert
        var tree = await commit.GetRootTreeAsync();
        var link = await tree.GetFromPathAsync("Link");
        link!.Mode.Type.Should().Be(ObjectType.GitLink);
    }
}
