using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using System.Xml;
using GitDotNet.Tools;
using GitDotNet.Writers;

namespace GitDotNet;

internal delegate ITransformationComposerInternal TransformationComposerFactory(string repositoryPath);

[DebuggerDisplay("Count = {Count}")]
internal class TransformationComposer(string repositoryPath, FastInsertWriterFactory FastInsertWriterFactory, IFileSystem fileSystem)
    : ITransformationComposerInternal
{
    internal Dictionary<GitPath, (TransformationType ChangeType, Stream? Stream, FileMode? FileMode)> Changes { get; } = [];

    public int Count => Changes.Count;

    public ITransformationComposer AddOrUpdate(GitPath path, string data, FileMode? fileMode = null) =>
        AddOrUpdate(path, Encoding.UTF8.GetBytes(data), fileMode);

    public ITransformationComposer AddOrUpdate(GitPath path, byte[] data, FileMode? fileMode = null) =>
        AddOrUpdate(path, new MemoryStream(data), fileMode);

    public ITransformationComposer AddOrUpdate(GitPath path, Stream stream, FileMode? fileMode = null)
    {
        Changes[path] = (TransformationType.AddOrModified, stream, fileMode);
        return this;
    }

    public ITransformationComposer Remove(GitPath path)
    {
        Changes[path] = (TransformationType.Removed, default, null);
        return this;
    }

    public async Task<HashId> CommitAsync(string branch, CommitEntry commit, CommitOptions? options)
    {
        var updateBranch = options?.UpdateBranch ?? true;
        var importBranch = updateBranch ? CheckFullReferenceName(branch) : $"refs/gitdotnetfastimport/{Guid.NewGuid()}";
        using var data = await WriteData(importBranch, commit);
        data.Seek(0, SeekOrigin.Begin);
        var markFile = GetMarkDownPath(repositoryPath, fileSystem);
        try
        {
            GitCliCommand.Execute(repositoryPath, $@"fast-import --export-marks=""{markFile}""", data);
            return new HashId(FindCommitIdInMarkFile(markFile));
        }
        finally
        {
            PostCommitCleanUp(updateBranch, importBranch, markFile);
        }
    }

    private static string FindCommitIdInMarkFile(string markFile)
    {
        const string linePrefix = ":1 ";
        var line = File.ReadLines(markFile)
            .FirstOrDefault(l => l.StartsWith(linePrefix, StringComparison.Ordinal)) ??
            throw new InvalidOperationException("Could not locate commit id in fast-import mark file.");
        return line[linePrefix.Length..].Trim();
    }

    private void PostCommitCleanUp(bool updateBranch, string importBranch, string markFile)
    {
        fileSystem.File.Delete(markFile);
        if (!updateBranch)
        {
            GitCliCommand.Execute(repositoryPath, $"git branch -D {importBranch}", throwOnError: false);
        }
    }

    private static string CheckFullReferenceName(string name)
    {
        if (!name.StartsWith("refs/")) throw new ArgumentException("Branch should use a full reference name.", nameof(name));
        return name;
    }

    private async Task<PooledMemoryStream> WriteData(string branch, CommitEntry commit)
    {
        var result = new PooledMemoryStream();
        using var writer = FastInsertWriterFactory(result);
        await writer.WriteHeaderAsync(branch, commit);
        writer.WriteTransformations(this);
        return result;
    }

    private static string GetMarkDownPath(string repositoryPath, IFileSystem fileSystem)
    {
        var folder = fileSystem.Path.Combine(repositoryPath, "temp");
        fileSystem.Directory.CreateDirectory(folder);
        var markFile = fileSystem.Path.Combine(folder, fileSystem.Path.GetRandomFileName());
        return markFile;
    }

    internal enum TransformationType
    {
        AddOrModified,
        Removed
    }
}

internal interface ITransformationComposerInternal : ITransformationComposer
{
    Task<HashId> CommitAsync(string branch, CommitEntry commit, CommitOptions? options);
}

/// <summary>Provides methods to compose transformations on a Git repository.</summary>
public interface ITransformationComposer
{
    /// <summary>Gets the number of changes in the composer.</summary>
    int Count { get; }

    /// <summary>Adds or updates a file in the repository with the specified path and data.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="data">The data to write to the file.</param>
    /// <param name="fileMode">The file mode of a Git tree entry item.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(GitPath path, string data, FileMode? fileMode = null);

    /// <summary>Adds or updates a file in the repository with the specified path and data.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="data">The data to write to the file.</param>
    /// <param name="fileMode">The file mode of a Git tree entry item.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(GitPath path, byte[] data, FileMode? fileMode = null);

    /// <summary>Adds or updates a file in the repository with the specified path and stream.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="stream">The stream containing the data to write to the file.</param>
    /// <param name="fileMode">The file mode of a Git tree entry item.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(GitPath path, Stream stream, FileMode? fileMode = null);

    /// <summary>Removes a file from the repository with the specified path.</summary>
    /// <remarks>Add '/' at the end of the path to indicate that folder and children should be removed.</remarks>
    /// <param name="path">The path of the file to remove.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer Remove(GitPath path);
}
