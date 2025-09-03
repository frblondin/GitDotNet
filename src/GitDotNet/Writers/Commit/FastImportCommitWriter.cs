using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using GitDotNet.Tools;

namespace GitDotNet.Writers.Commit;

[ExcludeFromCodeCoverage]
internal class FastImportCommitWriter(string repositoryPath, FastInsertWriterFactory fastInsertWriterFactory, IFileSystem fileSystem)
{
    public async Task<HashId> CommitAsync(TransformationComposer composer, string branch, CommitEntry commit, CommitOptions? options)
    {
        var updateBranch = options?.UpdateBranch ?? true;
        var importBranch = updateBranch ? Branch.CheckFullReferenceName(branch) : $"refs/gitdotnetfastimport/{Guid.NewGuid()}";
        using var data = await WriteData(composer, importBranch, commit).ConfigureAwait(false);
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

    private async Task<PooledMemoryStream> WriteData(TransformationComposer composer, string branch, CommitEntry commit)
    {
        var result = new PooledMemoryStream();
        using var writer = fastInsertWriterFactory(result);
        await writer.WriteHeaderAsync(branch, commit).ConfigureAwait(false);
        writer.WriteTransformations(composer);
        return result;
    }

    private static string GetMarkDownPath(string repositoryPath, IFileSystem fileSystem)
    {
        var folder = fileSystem.Path.Combine(repositoryPath, "temp");
        fileSystem.Directory.CreateDirectory(folder);
        var markFile = fileSystem.Path.Combine(folder, fileSystem.Path.GetRandomFileName());
        return markFile;
    }
}
