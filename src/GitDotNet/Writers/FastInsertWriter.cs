using System.Text;

namespace GitDotNet.Writers;

internal delegate FastInsertWriter FastInsertWriterFactory(Stream stream);

internal sealed class FastInsertWriter : IDisposable
{
    private readonly StreamWriter _writer;

    public FastInsertWriter(Stream stream)
    {
        _writer = new StreamWriter(stream, leaveOpen: true) { NewLine = "\n" };
    }

    public void WriteTransformations(TransformationComposer composer)
    {
        foreach (var (path, (changeType, stream)) in composer.Changes)
        {
            Write(path, changeType, stream);
        }
    }

    private void Write(GitPath path, TransformationComposer.TransformationType changeType, Stream? stream, FileMode? fileMode)
    {
        switch (changeType)
        {
            case TransformationComposer.TransformationType.AddOrModified:
                _writer.WriteLine($"M 100644 inline {path}");
                _writer.WriteLine($"data {stream!.Length}");

                // Flush the writer to ensure the stream is written at the correct position
                _writer.Flush();

                stream.CopyTo(_writer.BaseStream);
                break;
            case TransformationComposer.TransformationType.Removed:
                _writer.WriteLine($"D {path}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changeType), changeType, null);
        }
    }

    public async Task WriteHeaderAsync(string branch, CommitEntry commit)
    {
        await _writer.WriteLineAsync($"reset {branch}");
        await _writer.WriteLineAsync($"commit {branch}");
        await _writer.WriteLineAsync($"mark :1");
        if (commit.Author is not null) await WriteSignatureAsync("author", commit.Author);
        if (commit.Committer is not null) await WriteSignatureAsync("committer", commit.Committer);
        await _writer.WriteLineAsync($"data {Encoding.UTF8.GetByteCount(commit.Message)}");
        await _writer.WriteLineAsync(commit.Message);
        await WriteParentCommitsAsync(await commit.GetParentsAsync());
    }

    private async Task WriteSignatureAsync(string type, Signature signature) => await _writer.WriteLineAsync(
        $"{type} " +
        $"{signature.Name} " +
        $"<{signature.Email}> " +
        $"{signature.Timestamp.ToUnixTimeSeconds()} " +
        $"{signature.Timestamp.Offset.Minutes:+0000;-0000}");

    private async Task WriteParentCommitsAsync(IList<CommitEntry> parents)
    {
        if (parents.Count >= 1)
        {
            await _writer.WriteLineAsync($"from {parents[0].Id}");
            if (parents.Count >= 2)
            {
                await _writer.WriteLineAsync($"merge {parents[1].Id}");
            }
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
    }
}
