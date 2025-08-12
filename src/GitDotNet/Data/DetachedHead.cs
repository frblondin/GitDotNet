namespace GitDotNet;

/// <summary>Represents a Git detached head.</summary>
public class DetachedHead(GitConnection connection, HashId id) : Branch("HEAD (detached)", connection, () => id);