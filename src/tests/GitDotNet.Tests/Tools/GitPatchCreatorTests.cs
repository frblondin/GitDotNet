using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Hunk = System.Collections.Generic.List<(string Prefix, string Data)>;

namespace GitDotNet.Tools.Tests;

public partial class GitPatchCreatorTests
{
    [Test]
    public void BothContentsNull_ReturnsEmptyList()
    {
        // Arrange
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(null, null);

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void OldContentNull_NewContentHasContent_ReturnsAdditions()
    {
        // Arrange
        var newContent = Encoding.UTF8.GetBytes("Line1\nLine2\n");
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(null, new MemoryStream(newContent)).ToList();

        // Assert
        result.Should().HaveCount(1);
        HunkToString(result[0]).Should().Contain("+Line1\n+Line2\n");
    }

    [Test]
    public void OldContentHasContent_NewContentNull_ReturnsDeletions()
    {
        // Arrange
        var oldContent = Encoding.UTF8.GetBytes("Line1\nLine2\n");
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(new MemoryStream(oldContent), null).ToList();

        // Assert
        result.Should().HaveCount(1);
        HunkToString(result[0]).Should().Contain("-Line1\n-Line2\n");
    }

    [Test]
    public void BothContentsHaveSameContent_ReturnsEmptyList()
    {
        // Arrange
        var oldContent = Encoding.UTF8.GetBytes("Line1\nLine2\n");
        var newContent = Encoding.UTF8.GetBytes("Line1\nLine2\n");
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(new MemoryStream(oldContent), new MemoryStream(newContent)).ToList();

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void BothContentsHaveDifferentContent_ReturnsChanges()
    {
        // Arrange
        var oldContent = Encoding.UTF8.GetBytes("Line1\nLine2\n");
        var newContent = Encoding.UTF8.GetBytes("Line1\nLine3\n");
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(new MemoryStream(oldContent), new MemoryStream(newContent)).ToList();

        // Assert
        result.Should().HaveCount(1);
        HunkToString(result[0]).Should().Contain("-Line2\n+Line3\n");
    }

    [Test]
    public void ChangesWithinThreeLines_ReturnsSingleHunk()
    {
        // Arrange
        var oldContent = Encoding.UTF8.GetBytes("Line1\nLine2\nLine3\nLine4\nLine5\nLine6\n");
        var newContent = Encoding.UTF8.GetBytes("Line1\nLine2\nLineX\nLine4\nLineY\nLine6\n");
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(new MemoryStream(oldContent), new MemoryStream(newContent)).ToList();

        // Assert
        result.Should().HaveCount(1);
        HunkToString(result[0]).Should().Be("@@ -1,6 +1,6 @@\n Line1\n Line2\n-Line3\n+LineX\n Line4\n-Line5\n+LineY\n Line6\n");
    }

    [Test]
    public void ChangesWithFiveLinesApart_ReturnsTwoHunks()
    {
        // Arrange
        var oldContent = Encoding.UTF8.GetBytes("Line1\nLine2\nLine3\nLine4\nLine5\nLine6\nLine7\nLine8\nLine9\nLine10\n");
        var newContent = Encoding.UTF8.GetBytes("Line1\nLine2\nLineX\nLine4\nLine5\nLine6\nLine7\nLineY\nLine9\nLine10\n");
        var sut = new GitPatchCreator(3);

        // Act
        var result = sut.GetHunks(new MemoryStream(oldContent), new MemoryStream(newContent)).ToList();

        // Assert
        result.Should().HaveCount(2);
        HunkToString(result[0]).Should().Be("@@ -1,5 +1,5 @@\n Line1\n Line2\n-Line3\n+LineX\n Line4\n Line5\n Line6\n");
        HunkToString(result[1]).Should().Be("@@ -5,6 +5,6 @@\n Line5\n Line6\n Line7\n-Line8\n+LineY\n Line9\n Line10\n");
    }

    [Test]
    public void ChangesWithFiveLinesApart_ReturnIndentedHunk()
    {
        // Arrange
        var method = @"
    public void ShouldBeExcluded() { }

    public void SomeCSharpMethod()
    {



        // Original



    }

    public void ShouldBeExcluded() { }";
        var oldContent = Encoding.UTF8.GetBytes(method);
        var newContent = Encoding.UTF8.GetBytes(method.Replace("Original", "Modified"));
        var sut = new GitPatchCreator(10, TypeOrMemberDefinitionRegex());

        // Act
        var result = sut.GetHunks(new MemoryStream(oldContent), new MemoryStream(newContent)).ToList();

        // Assert
        using (new AssertionScope())
        {
            result.Should().HaveCount(1);
            HunkToString(result[0]).Should().Be(@"@@ -4,10 +4,10 @@
     public void SomeCSharpMethod()
     {
 
 
 
-        // Original
+        // Modified
 
 
 
     }
".ReplaceLineEndings("\n"));
        }
    }

    private static string HunkToString(Hunk hunk)
    {
        var sb = new StringBuilder();
        foreach (var (prefix, data) in hunk)
        {
            sb.Append(prefix).Append(data).Append('\n');
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*\b(public|private|protected|internal)\s+")]
    private static partial Regex TypeOrMemberDefinitionRegex();
}
