using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using GitDotNet.Readers;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Factory delegate for creating a <see cref="GitConnection"/> instance.</summary>
/// <param name="path">The path to the Git repository.</param>
/// <returns>A new instance of <see cref="GitConnection"/>.</returns>
public delegate GitConnection GitConnectionProvider(string path);

/// <summary>Represents a Git repository.</summary>
public partial class GitConnection : IDisposable
{
    private readonly Lazy<IObjectResolver> _objects;
    private readonly BranchRefReader _branchRefReader;
    private readonly Lazy<Index> _index;
    private readonly IDisposable _lock;
    private readonly ITreeComparer _comparer;
    private readonly TransformationComposerFactory _transformationComposerFactory;
    private readonly IFileSystem _fileSystem;
    private bool _disposedValue;

    internal GitConnection(string path,
                           RepositoryInfoFactory infoFactory,
                           ObjectsFactory objectsFactory,
                           BranchRefReaderFactory branchRefReaderFactory,
                           IndexFactory indexFactory,
                           ITreeComparer comparer,
                           TransformationComposerFactory transformationComposerFactory,
                           RepositoryLockerFactory repositoryLockFactory,
                           IFileSystem fileSystem)
    {
        if (!fileSystem.Directory.Exists(path)) throw new DirectoryNotFoundException($"Directory not found: {path}.");

        Info = infoFactory(path);
        _comparer = comparer;
        _transformationComposerFactory = transformationComposerFactory;
        _objects = new(() => objectsFactory(Info.Path, Info.Config.UseCommitGraph));
        _branchRefReader = branchRefReaderFactory(this);
        _index = new(() => indexFactory(Info.Path, Objects));
        _lock = repositoryLockFactory(Info.Path).GetLock();
        _fileSystem = fileSystem;
    }

    /// <summary>Gets the <see cref="RepositoryInfo"/> instance associated with the repository.</summary>
    public RepositoryInfo Info { get; }

    /// <summary>Gets the <see cref="Objects"/> instance associated with the repository.</summary>
    public IObjectResolver Objects => _objects.Value;

    /// <summary>Gets the <see cref="Index"/> instance associated with the repository.</summary>
    [ExcludeFromCodeCoverage]
    public Index Index => _index.Value;

    /// <summary>Gets the reference of the HEAD.</summary>
    public Branch Head
    {
        get
        {
            var headContent = _fileSystem.File.ReadAllText(Path.Combine(Info.Path, "HEAD")).Trim();
            var match = HeadRefRegex().Match(headContent);
            if (!match.Success)
            {
                throw new InvalidOperationException("Invalid HEAD reference format.");
            }
            return Branches[match.Groups[1].Value];
        }
    }

    /// <summary>Gets the list of remotes.</summary>
    public Remote.List Remotes => new(
        Info.Config.GetNamedSections("remote").ToImmutableDictionary(b => b, b => new Remote(b, this)));

    /// <summary>Gets the list of local and remote branches.</summary>
    /// <returns>A list of branch names.</returns>
    public Branch.List Branches => _branchRefReader.GetBranches();

    /// <summary>Gets the commit hash for a given committish reference, handling ~ and ^ navigation.</summary>
    /// <param name="committish">The committish reference.</param>
    /// <returns>The commit hash as a byte array.</returns>
    public async Task<CommitEntry> GetCommittishAsync(string committish)
    {
        var reference = committish;
        var matches = ChainedRelativeRefRegex().Matches(committish);
        if (matches.Count > 0)
        {
            reference = matches[0].Groups["ref"].Value;
            var commit = await GetReferenceTipAsync(reference);

            foreach (Match match in matches)
            {
                var op = match.Groups["op"].Value;
                var num = string.IsNullOrEmpty(match.Groups["num"].Value) ? 1 : int.Parse(match.Groups["num"].Value);

                commit = await TraverseCommitAsync(commit, op, num);
            }
            return commit;
        }
        else
        {
            return await GetReferenceTipAsync(reference);
        }

        async Task<CommitEntry> GetReferenceTipAsync(string reference) => reference switch
        {
            "HEAD" => await Head.GetTipAsync(),
            _ when HashId.TryParse(reference, out var id) => await Objects.GetAsync<CommitEntry>(id),
            _ => await Branches[reference].GetTipAsync(),
        };
    }

    private static async Task<CommitEntry> TraverseCommitAsync(CommitEntry commit, string op, int num)
    {
        if (op == "~")
        {
            for (int i = 0; i < num; i++)
            {
                var parents = await commit.GetParentsAsync();
                commit = parents[0];
            }
        }
        else if (op == "^")
        {
            var parents = await commit.GetParentsAsync();
            commit = parents[num - 1];
        }

        return commit;
    }

    /// <summary>Compares two <see cref="TreeEntry"/> instances recursively.</summary>
    /// <param name="old">The old <see cref="TreeEntry"/> instance.</param>
    /// <param name="new">The new <see cref="TreeEntry"/> instance.</param>
    /// <returns>A list of changes between the two <see cref="TreeEntry"/> instances.</returns>
    public virtual async Task<IList<Change>> CompareAsync(TreeEntry old, TreeEntry @new) =>
        await _comparer.CompareAsync(old, @new);

    /// <summary>Compares two <see cref="CommitEntry"/> instances by comparing their associated tree entries.</summary>
    /// <param name="old">The old <see cref="CommitEntry"/> instance.</param>
    /// <param name="new">The new <see cref="CommitEntry"/> instance.</param>
    /// <param name="patch">The stream to write the patch to.</param>
    /// <param name="unified">Generate diffs with n lines of context.</param>
    /// <param name="indentedHunkStart">The regular expression to match the start of an indented hunk.</param>
    /// <returns></returns>
    public virtual async Task<IList<Change>> CompareAsync(CommitEntry old, CommitEntry @new, Stream? patch = null, int unified = GitPatchCreator.DefaultUnified, Regex? indentedHunkStart = null)
    {
        var result = await CompareAsync(await old.GetRootTreeAsync(), await @new.GetRootTreeAsync());
        if (patch is not null)
        {
            var patchCreator = new GitPatchCreator(unified, indentedHunkStart);
            await patchCreator.CreatePatchAsync(patch, old, @new, result);
        }
        return result;
    }

    /// <summary>Compares two commit references by comparing their associated commit entries.</summary>
    /// <param name="old">The old reference.</param>
    /// <param name="new">The new reference.</param>
    /// <param name="patch">The stream to write the patch to.</param>
    /// <param name="unified">Generate diffs with n lines of context.</param>
    /// <returns></returns>
    public virtual async Task<IList<Change>> CompareAsync(string old, string @new, Stream? patch = null, int unified = GitPatchCreator.DefaultUnified)
    {
        var oldCommit = await GetCommittishAsync(old);
        var newCommit = await GetCommittishAsync(@new);
        return await CompareAsync(oldCommit, newCommit, patch, unified);
    }

    /// <summary>Commits the changes in the transformation composer to the repository.</summary>
    /// <param name="branchName">The branch name to commit to.</param>
    /// <param name="transformations">The transformations to apply to the repository.</param>
    /// <param name="commit">The commit entry to commit.</param>
    /// <param name="updateBranch">true to update the branch reference; otherwise, false.</param>
    public async Task<CommitEntry> CommitAsync(string branchName, Func<ITransformationComposer, ITransformationComposer> transformations, CommitEntry commit, bool updateBranch = true)
    {
        var tip = Branches.TryGet(branchName, out var branch) ? await branch.GetTipAsync() : null;
        var canonicalName = Reference.LooksLikeLocalBranch(branchName) ? branchName : $"{Reference.LocalBranchPrefix}{branchName}";
        var commitWithParent = commit with { Parents = tip is null ? [] : [tip] };

        var composer = _transformationComposerFactory(Info.Path);
        transformations(composer);

        var hash = await composer.CommitAsync(canonicalName, commitWithParent, updateBranch);
        (Objects as IObjectResolverInternal)?.ReinitializePacks();
        return await Objects.GetAsync<CommitEntry>(hash);
    }

    /// <summary>Creates a new in-memory commit entry before it gets committed to repository.</summary>
    /// <param name="message">The commit message.</param>
    /// <param name="author">The author of the commit.</param>
    /// <param name="committer">The committer of the commit.</param>
    /// <returns>The new commit entry.</returns>
    public CommitEntry CreateCommit(string message, Signature? author = null, Signature? committer = null) =>
        new(HashId.Empty, [], Objects)
        {
            _content = new(new CommitEntry.Content("", author ?? Info.Config.CreateSignature(), committer ?? Info.Config.CreateSignature(), [], message))
        };

    /// <summary>Determines whether the specified path is a valid Git repository.</summary>
    /// <param name="path">The path to the repository.</param>
    /// <returns>true if the path is a valid Git repository; otherwise, false.</returns>
    public static bool IsValid(string path) => new GitCliCommand().GetAbsoluteGitPath(path) != null;

    /// <summary>Creates a new Git repository at the specified path.</summary>
    /// <param name="path">The path to the repository.</param>
    /// <param name="isBare">true to create a bare repository; otherwise, false.</param>
    public static void Create(string path, bool isBare = false)
    {
        Directory.CreateDirectory(path);
        var argument = isBare ? "--bare" : string.Empty;
        GitCliCommand.Execute(path, $"init {argument}");
    }

    /// <summary>Releases the resources used by the <see cref="GitConnection"/>.</summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                if (_objects.IsValueCreated) _objects.Value.Dispose();
                if (_index.IsValueCreated) _index.Value.Dispose();
            }
            _lock.Dispose();

            _disposedValue = true;
        }
    }

    /// <summary>Finalizes an instance of the <see cref="GitConnection"/> class.</summary>
    [ExcludeFromCodeCoverage]
    ~GitConnection()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    void IDisposable.Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^ref:\s*(.+)$")]
    private static partial Regex HeadRefRegex();
    [GeneratedRegex(@"(?<ref>^[^~^]+)(?<op>[~^])(?<num>\d*)")]
    private static partial Regex ChainedRelativeRefRegex();
}
