using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using GitDotNet.Readers;
using GitDotNet.Writers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitDotNet;

/// <summary>Factory delegate for creating a <see cref="GitConnection"/> instance.</summary>
/// <param name="path">The path to the Git repository.</param>
/// <returns>A new instance of <see cref="GitConnection"/>.</returns>
public delegate IGitConnection GitConnectionProvider(string path);

/// <summary>Represents a Git repository.</summary>
[DebuggerDisplay("{Info.Path,nq}")]
internal partial class GitConnectionInternal : IGitConnection
{
    private readonly Lazy<IObjectResolver> _objects;
    private readonly IBranchRefReader _branchRefReader;
    private readonly IBranchRefWriter _branchRefWriter;
    private readonly HeadWriter _headWriter;
    private readonly StashRefReader _stashReader;
    private readonly Lazy<Index> _index;
    private readonly ITreeComparer _comparer;
    private readonly TransformationComposerFactory _transformationComposerFactory;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<GitConnectionInternal>? _logger;
    private bool _disposedValue;

    internal GitConnectionInternal(string path,
        IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetService<ILogger<GitConnectionInternal>>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        if (!_fileSystem.Directory.Exists(path))
        {
            _logger?.LogWarning("Directory not found: {Path}.", path);
            throw new DirectoryNotFoundException($"Directory not found: {path}.");
        }
        Info = serviceProvider.GetRequiredService<RepositoryInfoFactory>().Invoke(path);
        NotSupportedFeatures.ThrowIfNeeded(Info, _fileSystem);
        _comparer = serviceProvider.GetRequiredService<ITreeComparer>();
        _transformationComposerFactory = serviceProvider.GetRequiredService<TransformationComposerFactory>();
        _objects = new(() => serviceProvider.GetRequiredService<ObjectResolverFactory>().Invoke(Info.Path, Info.Config.UseCommitGraph));
        _branchRefReader = serviceProvider.GetRequiredService<BranchRefReaderFactory>().Invoke(this);
        _branchRefWriter = serviceProvider.GetRequiredService<BranchRefWriterFactory>().Invoke(Info, _branchRefReader);
        _headWriter = serviceProvider.GetRequiredService<HeadWriterFactory>().Invoke(Info);
        _stashReader = serviceProvider.GetRequiredService<StashRefReaderFactory>().Invoke(this);
        _index = new(() => serviceProvider.GetRequiredService<IndexFactory>().Invoke(Info, Objects));
        _logger?.LogDebug("GitConnection initialized for path: {Path}", path);
    }

    public RepositoryInfo Info { get; }

    public IObjectResolver Objects => _objects.Value;

    [ExcludeFromCodeCoverage]
    public Index Index => _index.Value;

    public Branch Head
    {
        get
        {
            var path = _fileSystem.Path.Combine(Info.Path, "HEAD");
            var content = _fileSystem.File.ReadAllText(path).Trim();
            var match = HeadRefRegex().Match(content);
            if (match.Success)
            {
                return Branches[match.Groups[1].Value];
            }
            if (HashId.TryParse(content, out var headId))
            {
                return new DetachedHead(this, headId);
            }
            throw new InvalidOperationException(@$"Invalid HEAD reference format: ""{content}""");
        }
    }

    public Remote.List Remotes => new(
        Info,
        Info.Config.GetNamedSections("remote").ToDictionary(b => b, b => new Remote(b, Info)));

    public Branch.List Branches => new(_branchRefReader, _branchRefWriter);

    public async Task<CommitEntry> GetCommittishAsync(string committish) =>
        await GetCommittishAsync<CommitEntry>(committish, c => c.ParentIds).ConfigureAwait(false);

    internal async Task<T> GetCommittishAsync<T>(string committish, Func<T, IList<HashId>> parentProvider)
        where T : Entry
    {
        var reference = committish;
        var matches = ChainedRelativeRefRegex().Matches(committish);
        if (matches.Count > 0)
        {
            reference = matches[0].Groups["ref"].Value;
            var commitId = GetReferenceTip(reference);
            var commit = await Objects.GetAsync<T>(commitId).ConfigureAwait(false);

            foreach (var groups in matches.Select(m => m.Groups))
            {
                var op = groups["op"].Value;
                var num = string.IsNullOrEmpty(groups["num"].Value) ? 1 : int.Parse(groups["num"].Value);

                var traversed = TraverseCommit(commitId, parentProvider(commit), op, num);
                if (traversed != commitId)
                {
                    commit = await Objects.GetAsync<T>(traversed).ConfigureAwait(false);
                }
            }
            return commit;
        }
        else
        {
            var commitId = GetReferenceTip(reference);
            return await Objects.GetAsync<T>(commitId).ConfigureAwait(false);
        }

        HashId GetReferenceTip(string r) => r switch
        {
            "HEAD" => Head.Tip ?? throw new InvalidOperationException("Head has not tip commit."),
            _ when HashId.TryParse(r, out var id) => id,
            _ => Branches[r].Tip ?? throw new InvalidOperationException($"Branch {r} has not tip commit."),
        };
    }

    private static HashId TraverseCommit(HashId id, IList<HashId> parents, string op, int num)
    {
        var result = id;
        if (op == "~")
        {
            for (int i = 0; i < num; i++)
            {
                result = parents[0];
            }
        }
        else if (op == "^")
        {
            result = parents[num - 1];
        }

        return result;
    }

    public virtual async Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry @new) =>
        await _comparer.CompareAsync(old, @new).ConfigureAwait(false);

    public virtual async Task<IList<Change>> CompareAsync(CommitEntry? old, CommitEntry @new)
    {
        var oldTree = old != null ? await old.GetRootTreeAsync().ConfigureAwait(false) : null;
        var newTree = await @new.GetRootTreeAsync().ConfigureAwait(false);
        return await CompareAsync(oldTree, newTree).ConfigureAwait(false);
    }

    public virtual async Task<IList<Change>> CompareAsync(string? old, string @new)
    {
        var oldCommit = old != null ? await GetCommittishAsync(old).ConfigureAwait(false) : null;
        var newCommit = await GetCommittishAsync(@new).ConfigureAwait(false);
        return await CompareAsync(oldCommit, newCommit).ConfigureAwait(false);
    }

    public virtual async Task<IReadOnlyList<Stash>> GetStashesAsync() =>
        await _stashReader.GetStashesAsync().ConfigureAwait(false);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (_objects?.IsValueCreated ?? false)
                _objects.Value.Dispose();

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    ~GitConnectionInternal()
    {
        Dispose(disposing: false);
    }

    [GeneratedRegex(@"^ref:\s*(.+)$")]
    private static partial Regex HeadRefRegex();
    [GeneratedRegex(@"(?<ref>^[^~^]+)(?<op>[~^])(?<num>\d*)")]
    private static partial Regex ChainedRelativeRefRegex();
}
