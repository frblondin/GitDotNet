namespace GitDotNet;

/// <summary>
/// The exception that is thrown when an ambiguous abbreviated hash is encountered,
/// meaning more than one object matches the hash.
/// </summary>
public class AmbiguousHashException()
    : InvalidOperationException("Ambiguous abbreviated hash, more than one object match.");