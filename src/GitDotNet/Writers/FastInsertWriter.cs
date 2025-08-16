using System.Text;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;

internal delegate FastInsertWriter FastInsertWriterFactory(Stream stream);

internal sealed class FastInsertWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly ILogger<FastInsertWriter>? _logger;

    public FastInsertWriter(Stream stream, ILogger<FastInsertWriter>? logger = null)
    {
        _writer = new StreamWriter(stream, leaveOpen: true) { NewLine = "\n" };
        _logger = logger;
    }

    public void WriteTransformations(TransformationComposer composer)
    {
        _logger?.LogInformation("Writing {Count} transformations.", composer.Changes.Count);
        try
        {
            foreach (var (path, (changeType, stream, fileMode)) in composer.Changes)
            {
                _logger?.LogDebug("Writing transformation: {ChangeType} for path {Path}", changeType, path);
                Write(path, changeType, stream, fileMode);
            }
        }
        finally
        {
            foreach (var (_, (_, stream, _)) in composer.Changes)
            {
                stream?.Dispose();
                _logger?.LogDebug("Disposed transformation stream.");
            }
        }
    }

    private void Write(GitPath path, TransformationComposer.TransformationType changeType, Stream? stream, FileMode? fileMode)
    {
        switch (changeType)
        {
            case TransformationComposer.TransformationType.AddOrModified:
                ArgumentNullException.ThrowIfNull(stream);

                if (fileMode?.Type == ObjectType.GitLink)
                {
                    _logger?.LogDebug("Writing GitLink add/modify for {Path}", path);
                    WriteGitLogAddOrModified(path, stream, fileMode);
                }
                else
                {
                    _logger?.LogDebug("Writing regular blob add/modify for {Path}", path);
                    WriteRegularBlobAddOrModified(path, stream, fileMode);
                }
                break;
            case TransformationComposer.TransformationType.Removed:
                _logger?.LogDebug("Writing removal for {Path}", path);
                _writer.WriteLine($"D {path}");
                break;
            default:
                _logger?.LogWarning("Unknown transformation type: {ChangeType} for {Path}", changeType, path);
                throw new ArgumentOutOfRangeException(nameof(changeType), changeType, null);
        }
    }

    private void WriteGitLogAddOrModified(GitPath path, Stream stream, FileMode fileMode)
    {
        _writer.Write($"M {fileMode} ");

        // Flush the writer to ensure the stream is written at the correct position
        _writer.Flush();

        stream.CopyTo(_writer.BaseStream);
        _writer.WriteLine($" {path}");
    }

    private void WriteRegularBlobAddOrModified(GitPath path, Stream stream, FileMode? fileMode)
    {
        _writer.WriteLine($"M {fileMode ?? FileMode.RegularFile} inline {path}");
        _writer.WriteLine($"data {stream!.Length}");

        // Flush the writer to ensure the stream is written at the correct position
        _writer.Flush();

        stream.CopyTo(_writer.BaseStream);
    }

    public async Task WriteHeaderAsync(string branch, CommitEntry commit)
    {
        _logger?.LogInformation("Writing header for branch: {Branch}, commit: {CommitId}", branch, commit.Id);
        await _writer.WriteLineAsync($"commit {branch}").ConfigureAwait(false);
        await _writer.WriteLineAsync($"mark :1").ConfigureAwait(false);
        if (commit.Author is not null) await WriteSignatureAsync("author", commit.Author).ConfigureAwait(false);
        if (commit.Committer is not null) await WriteSignatureAsync("committer", commit.Committer).ConfigureAwait(false);
        await _writer.WriteLineAsync($"data {Encoding.UTF8.GetByteCount(commit.Message)}").ConfigureAwait(false);
        await _writer.WriteLineAsync(commit.Message).ConfigureAwait(false);
        await WriteParentCommitsAsync(await commit.GetParentsAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task WriteSignatureAsync(string type, Signature signature) => await _writer.WriteLineAsync(
        $"{type} " +
        $"{signature.Name} " +
        $"<{signature.Email}> " +
        $"{signature.Timestamp.ToUnixTimeSeconds()} " +
        $"{signature.Timestamp.Offset.Minutes:+0000;-0000}").ConfigureAwait(false);

    private async Task WriteParentCommitsAsync(IList<CommitEntry> parents)
    {
        if (parents.Count >= 1)
        {
            await _writer.WriteLineAsync($"from {parents[0].Id}").ConfigureAwait(false);
            if (parents.Count >= 2)
            {
                await _writer.WriteLineAsync($"merge {parents[1].Id}").ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _logger?.LogDebug("Disposing FastInsertWriter.");
        _writer?.Dispose();
    }
}
