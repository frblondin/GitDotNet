using FluentAssertions;

namespace GitDotNet.Tests.Data;

public class SignatureTests
{
    [Test]
    public void ShouldParseSignatureCorrectly()
    {
        // Arrange
        var content = "John Doe <john.doe@example.com> 1627846267 +0200";

        // Act
        var signature = Signature.Parse(content);

        // Assert
        signature.Should().NotBeNull();
        signature?.Name.Should().Be("John Doe");
        signature?.Email.Should().Be("john.doe@example.com");
        signature?.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1627846267).ToOffset(TimeSpan.FromHours(2)));
    }

    [Test]
    public void ShouldReturnNullForNullContent()
    {
        // Arrange
        string? content = null;

        // Act
        var signature = Signature.Parse(content);

        // Assert
        signature.Should().BeNull();
    }

    [Test]
    public void ShouldThrowForInvalidFormat()
    {
        // Arrange
        var content = "Invalid format";

        // Act
        Action act = () => Signature.Parse(content);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid git signature format.");
    }

    [Test]
    public void ShouldThrowForMissingEmail()
    {
        // Arrange
        var content = "John Doe 1627846267 +0200";

        // Act
        Action act = () => Signature.Parse(content);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid git signature format.");
    }

    [Test]
    public void ShouldThrowForMissingTimestamp()
    {
        // Arrange
        var content = "John Doe <john.doe@example.com>";

        // Act
        Action act = () => Signature.Parse(content);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid git signature format.");
    }

    [Test]
    public void ShouldThrowForInvalidTimeZoneOffset()
    {
        // Arrange
        var content = "John Doe <john.doe@example.com> 1627846267 0200";

        // Act
        Action act = () => Signature.Parse(content);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid time zone offset format.");
    }
}
