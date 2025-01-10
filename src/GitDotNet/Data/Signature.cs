using System.Text;

namespace GitDotNet;

/// <summary>
/// Represents a Git signature, which includes the name, email, and timestamp of the author or committer.
/// </summary>
/// <param name="Name">The name of the author or committer.</param>
/// <param name="Email">The email of the author or committer.</param>
/// <param name="Timestamp">The timestamp of the signature.</param>
public record class Signature(string Name, string Email, DateTimeOffset Timestamp)
{
    internal static Signature? Parse(string? content)
    {
        if (content is null) return null;
        var span = content.AsSpan();

        var emailStart = span.IndexOf('<');
        if (emailStart == -1) throw new InvalidOperationException("Invalid git signature format.");
        var name = span[..emailStart].Trim().ToString();

        span = span[(emailStart + 1)..];
        var emailEnd = span.IndexOf('>');
        if (emailEnd == -1) throw new InvalidOperationException("Invalid git signature format.");
        var email = span[..emailEnd].ToString();

        span = span[(emailEnd + 1)..].Trim();
        var timestampEnd = span.IndexOf(' ');
        if (timestampEnd == -1) throw new InvalidOperationException("Invalid git signature format.");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(span[..timestampEnd]));

        var offset = ParseTimeZoneOffset(span[(timestampEnd + 1)..].ToString());

        return new Signature(name, email, timestamp.ToOffset(offset));
    }

    private static TimeSpan ParseTimeZoneOffset(ReadOnlySpan<char> offset)
    {
        if (offset.Length != 5 || (offset[0] != '+' && offset[0] != '-'))
        {
            throw new InvalidOperationException("Invalid time zone offset format.");
        }

        var sign = offset[0] == '+' ? 1 : -1;
        var hours = int.Parse(offset.Slice(1, 2));
        var minutes = int.Parse(offset.Slice(3, 2));

        return new TimeSpan(hours * sign, minutes * sign, 0);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var result = new StringBuilder();
        PrintMembers(result);
        return result.ToString();
    }
}
