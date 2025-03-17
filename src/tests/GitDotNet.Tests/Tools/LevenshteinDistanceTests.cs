using GitDotNet.Tools;
using FluentAssertions;
using System.Text;

namespace GitDotNet.Tests.Tools;

[TestFixture]
public class LevenshteinDistanceTests
{
    [TestCase("", "", 0)]
    [TestCase("a", "a", 0)]
    [TestCase("a", "b", 1)]
    [TestCase("kitten", "sitting", 3)]
    [TestCase("flaw", "lawn", 2)]
    [TestCase("intention", "execution", 5)]
    [TestCase("مرحبا", "سلام", 5)]
    public void ShouldReturnCorrectDistance(string s, string t, int expected)
    {
        // Act
        var result = LevenshteinDistance.ComputeDistance(Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetBytes(t));

        // Assert
        result.Should().Be(expected);
    }

    [TestCase("", "", 1.0f)]
    [TestCase("a", "a", 1.0f)]
    [TestCase("a", "b", 0.0f)]
    [TestCase("kitten", "sitting", 0.571f)]
    [TestCase("flaw", "lawn", 0.5f)]
    public void ShouldReturnCorrectSimilarity(string s, string t, float expected)
    {
        // Act
        var result = LevenshteinDistance.ComputeSimilarity(Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetBytes(t));

        // Assert
        result.Should().BeApproximately(expected, 0.001f);
    }
}
