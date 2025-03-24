using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Abstractions;
using GitDotNet.Tools;
using GitDotNet.Writers;

namespace GitDotNet;

internal delegate ITransformationComposerInternal TransformationComposerFactory(string repositoryPath);

[DebuggerDisplay("Count = {Count}")]
internal class TransformationComposer(string repositoryPath, FastInsertWriterFactory FastInsertWriterFactory, IFileSystem fileSystem)
    : ITransformationComposerInternal
{
    internal Dictionary<string, (TransformationType ChangeType, Stream? Stream)> Changes { get; } = [];

    public int Count => Changes.Count;

    public ITransformationComposer AddOrUpdate(string path, byte[] data) =>
        AddOrUpdate(path, new MemoryStream(data));

    public ITransformationComposer AddOrUpdate(string path, Stream stream)
    {
        Changes[path] = (TransformationType.AddOrModified, stream);
        return this;
    }

    public ITransformationComposer Remove(string path)
    {
        Changes[path] = (TransformationType.Removed, default);
        return this;
    }

    public async Task<HashId> CommitAsync(string branch, CommitEntry commit, CommitOptions? options)
    {
        CheckFullReferenceName(branch);

        using var data = await WriteData(branch, commit);
        data.Seek(0, SeekOrigin.Begin);
        var markFile = GetMarkDownPath(repositoryPath, fileSystem);
        try
        {
            GitCliCommand.Execute(repositoryPath, $@"fast-import --export-marks=""{markFile}""", data);
            const string linePrefix = ":1 ";
            var line = File.ReadLines(markFile)
                .FirstOrDefault(l => l.StartsWith(linePrefix, StringComparison.Ordinal)) ??
                throw new InvalidOperationException("Could not locate commit id in fast-import mark file.");
            var result = line[linePrefix.Length..].Trim();

            if (!(options?.UpdateBranch ?? true))
            {
                await RevertToPreviousCommitAsync(repositoryPath, branch, commit);
            }

            return new HashId(result);
        }
        finally
        {
            fileSystem.File.Delete(markFile);
        }
    }

    private static async Task RevertToPreviousCommitAsync(string repositoryPath, string branch, CommitEntry commit)
    {
        var parents = await commit.GetParentsAsync();
        GitCliCommand.Execute(repositoryPath, $@"update-ref {branch} {parents[0].Id}");
    }

    private static void CheckFullReferenceName(string name)
    {
        if (!name.StartsWith("refs/")) throw new ArgumentException("Branch should use a full reference name.", nameof(name));
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
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(string path, byte[] data);

    /// <summary>Adds or updates a file in the repository with the specified path and stream.</summary>
    /// <param name="path">The path of the file to add or update.</param>
    /// <param name="stream">The stream containing the data to write to the file.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer AddOrUpdate(string path, Stream stream);

    /// <summary>Removes a file from the repository with the specified path.</summary>
    /// <remarks>Add '/' at the end of the path to indicate that folder and children should be removed.</remarks>
    /// <param name="path">The path of the file to remove.</param>
    /// <returns>The current instance of <see cref="ITransformationComposer"/>.</returns>
    ITransformationComposer Remove(string path);
}
