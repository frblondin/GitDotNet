using System.Diagnostics;
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
[DebuggerDisplay("{Info.Path,nq}")]
public partial class GitConnection : IDisposable
{
    private readonly Lazy<IObjectResolver> _objects;
    private readonly BranchRefReader _branchRefReader;
    private readonly Lazy<Index> _index;
    private readonly IRepositoryLocker _lock;
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
        _lock = repositoryLockFactory(Info.Path);
        _index = new(() => indexFactory(Info, Objects, _lock));
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
        Info,
        Info.Config.GetNamedSections("remote").ToDictionary(b => b, b => new Remote(b, Info)));

    /// <summary>Gets the list of local and remote branches.</summary>
    /// <returns>A list of branch names.</returns>
    public Branch.List Branches => new(Info, _branchRefReader);

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
    public virtual async Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry @new) =>
        await _comparer.CompareAsync(old, @new);

    /// <summary>Compares two <see cref="CommitEntry"/> instances by comparing their associated tree entries.</summary>
    /// <param name="old">The old <see cref="CommitEntry"/> instance.</param>
    /// <param name="new">The new <see cref="CommitEntry"/> instance.</param>
    /// <param name="patch">The stream to write the patch to.</param>
    /// <param name="unified">Generate diffs with n lines of context.</param>
    /// <param name="indentedHunkStart">The regular expression to match the start of an indented hunk.</param>
    /// <returns></returns>
    public virtual async Task<IList<Change>> CompareAsync(CommitEntry? old, CommitEntry @new, Stream? patch = null, int unified = GitPatchCreator.DefaultUnified, Regex? indentedHunkStart = null)
    {
        var oldTree = old != null ? await old.GetRootTreeAsync() : null;
        var newTree = await @new.GetRootTreeAsync();
        var result = await CompareAsync(oldTree, newTree);
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
    public virtual async Task<IList<Change>> CompareAsync(string? old, string @new, Stream? patch = null, int unified = GitPatchCreator.DefaultUnified)
    {
        var oldCommit = old != null ? await GetCommittishAsync(old) : null;
        var newCommit = await GetCommittishAsync(@new);
        return await CompareAsync(oldCommit, newCommit, patch, unified);
    }

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

    /// <summary>
    /// Creates a directory at the specified path and clones a repository into it. The cloning can be done as a bare
    /// repository if specified.
    /// </summary>
    /// <param name="path">Specifies the location where the repository will be cloned.</param>
    /// <param name="url">The (possibly remote) repository to clone from.</param>
    /// <param name="options">Specifies additional options.</param>
    public static void Clone(string path, string url, CloneOptions? options = null)
    {
        Directory.CreateDirectory(path);
        var argument = options?.IsBare ?? false ? "--bare " : string.Empty;
        argument += options?.BranchName != null ? $"--branch {options.BranchName} " : string.Empty;
        argument += options?.RecurseSubmodules ?? false ? $"----recurse-submodules " : string.Empty;
        GitCliCommand.Execute(path, $"clone {argument} {url} .");
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

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^ref:\s*(.+)$")]
    private static partial Regex HeadRefRegex();
    [GeneratedRegex(@"(?<ref>^[^~^]+)(?<op>[~^])(?<num>\d*)")]
    private static partial Regex ChainedRelativeRefRegex();
}
