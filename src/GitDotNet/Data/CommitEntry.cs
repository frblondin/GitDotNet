using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a Git commit entry.</summary>
public record class CommitEntry : Entry
{
    private const string PendingReading = "<pending reading>";

    private readonly IObjectResolver _objectResolver;
    private readonly Lazy<byte[]> _data;
    internal Lazy<Content> _content;
    private readonly HashId? _treeId;
    private TreeEntry? _tree;
    internal IList<HashId>? _parentIds;
    private readonly DateTimeOffset? _commitTime;
    private IList<CommitEntry>? _parents;

    internal CommitEntry(HashId id, byte[] data, IObjectResolver objectResolver, HashId? treeId = null)
        : this(id, new Lazy<byte[]>(() => data), objectResolver, treeId)
    {
    }

    internal CommitEntry(HashId id, Lazy<byte[]> data, IObjectResolver objectResolver, HashId? treeId = null, IList<HashId>? parents = null, DateTimeOffset? commitTime = null)
        : base(EntryType.Commit, id, [])
    {
        _content = new(Parse);
        _data = data;
        _objectResolver = objectResolver;
        _treeId = treeId;
        _parentIds = parents;
        _commitTime = commitTime;
    }

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    protected internal override byte[] Data => _data.Value;

    /// <summary>Gets the author of the commit.</summary>
    public Signature? Author => _content.Value.Author;

    /// <summary>Gets the committer of the commit.</summary>
    public Signature? Committer => _content.Value.Committer;

    internal DateTimeOffset? CommitTime => _commitTime ?? Committer?.Timestamp;

    /// <summary>Gets the commit message.</summary>
    public string Message => _content.Value.Message;

    /// <summary>Gets the tree entry associated with the commit.</summary>
    [ExcludeFromCodeCoverage]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TreeEntry? RootTree => AsyncHelper.RunSync(GetRootTreeAsync);

    /// <summary>Gets the parent commits of the current commit.</summary>
    [ExcludeFromCodeCoverage]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IList<CommitEntry> Parents
    {
        get => AsyncHelper.RunSync(GetParentsAsync);
        init => _parents = value;
    }

    /// <summary>Asynchronously gets the tree entry associated with the commit.</summary>
    /// <returns>The tree entry associated with the commit.</returns>
    [ExcludeFromCodeCoverage]
    public async Task<TreeEntry> GetRootTreeAsync() => _tree ??=
        _treeId != null || !string.IsNullOrEmpty(_content.Value.Tree) ?
        await _objectResolver.GetAsync<TreeEntry>(_treeId ?? _content.Value.Tree.HexToByteArray()) :
        throw new InvalidOperationException("Cannot get tree from a new empty commit.");

    /// <summary>Asynchronously gets the parent commits of the current commit.</summary>
    /// <returns>A list of parent commits.</returns>
    public async Task<IList<CommitEntry>> GetParentsAsync()
    {
        return _parents ??= await LookupParents();

        async Task<IList<CommitEntry>> LookupParents()
        {
            _parentIds ??= _content.Value.Parents;
            var parents = ImmutableList.CreateBuilder<CommitEntry>();
            foreach (var parent in _parentIds)
            {
                var commit = await _objectResolver.GetAsync<CommitEntry>(parent);
                parents.Add(commit);
            }
            return parents.ToImmutable();
        }
    }

    private Content Parse()
    {
        var index = Array.IndexOf(Data, (byte)0);

        // Read the commit content
        var commitContent = Encoding.UTF8.GetString(Data, 0, index != -1 ? index : Data.Length);
        var lines = commitContent.AsSpan();

        string? tree = null, author = null, committer = null;
        var parents = ImmutableList.CreateBuilder<HashId>();
        var message = new StringBuilder();
        var headerCompleted = false;

        while (lines.Length > 0)
        {
            var lineEndIndex = lines.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line;

            if (lineEndIndex == -1)
            {
                line = lines;
                lines = [];
            }
            else
            {
                line = lines[..lineEndIndex];
                lines = lines[(lineEndIndex + 1)..];
            }

            if (!headerCompleted && !ProcessHeaderLine(line, ref tree, ref author, ref committer, parents))
            {
                headerCompleted = true;
                continue;
            }

            if (headerCompleted)
            {
                if (message.Length > 0) message.AppendLine();
                message.Append(line);
            }
        }

        if (tree is null) throw new InvalidOperationException("Invalid commit entry: missing tree.");
        return new(tree, Signature.Parse(author), Signature.Parse(committer), parents.ToImmutable(), message.ToString());
    }

    private static bool ProcessHeaderLine(ReadOnlySpan<char> line, ref string? tree, ref string? author, ref string? committer, ImmutableList<HashId>.Builder parents)
    {
        if (line.StartsWith("tree "))
        {
            tree = line[5..].ToString();
        }
        else if (line.StartsWith("author "))
        {
            author = line[7..].ToString();
        }
        else if (line.StartsWith("committer "))
        {
            committer = line[10..].ToString();
        }
        else if (line.StartsWith("parent "))
        {
            parents.Add(line[7..].ToString().HexToByteArray());
        }
        return line.Length != 0;
    }

    /// <inheritdoc/>>
    [ExcludeFromCodeCoverage]
    protected override bool PrintMembers(StringBuilder builder)
    {
        base.PrintMembers(builder);
        builder.Append(", ");
        builder.Append("Message = ").Append(Message).Append(", ");
        builder.Append("TreeId = ").Append(_treeId).Append(", ");
        if (_parentIds != null)
        {
            builder.Append("ParentIds = [");
            var first = true;
            foreach (var parent in _parentIds)
            {
                if (!first) builder.Append(", ");
                builder.Append(parent.ToString());
                first = false;
            }
            builder.Append("], ");
        }
        else
        {
            builder.Append("ParentIds = ").Append(PendingReading).Append(", ");
        }
        builder.Append("Author = ").Append(_content.IsValueCreated ? _content.Value.Author : PendingReading).Append(", ");
        builder.Append("Committer = ").Append(_content.IsValueCreated ? _content.Value.Committer : PendingReading);
        builder.Append("Data = ").Append(_data.IsValueCreated ? $"{_data.Value.Length} bytes" : PendingReading).Append(", ");
        return true;
    }

    internal record class Content(string Tree, Signature? Author, Signature? Committer, IList<HashId> Parents, string Message);
}