namespace GitDotNet;

/// <summary>
/// The exception that is thrown when an ambiguous abbreviated hash is encountered,
/// meaning more than one object matches the hash.
/// </summary>
public class AmbiguousHashException()
    : InvalidOperationException("Ambiguous abbreviated hash, more than one object match.")
{
    internal static void CheckForAmbiguousHashMatch(HashId id, int alreadyFound, byte[] hashBuffer, int i)
    {
        if (id.CompareTo(hashBuffer.AsSpan(0, id.Hash.Count)) == 0 && i != alreadyFound)
        {
            throw new AmbiguousHashException();
        }
    }
}