using System.Text;

namespace GitDotNet.Writers;

internal delegate FastInsertWriter FastInsertWriterFactory(Stream stream);

internal class FastInsertWriter : IDisposable
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

    private void Write(string path, TransformationComposer.TransformationType changeType, Stream? stream)
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

    public async Task WriteHeaderAsync(string branch,
                                       CommitEntry commit)
    {
        _writer.WriteLine($"reset {branch}");
        _writer.WriteLine($"commit {branch}");
        _writer.WriteLine($"mark :1");
        if (commit.Author is not null) WriteSignature("author", commit.Author);
        if (commit.Committer is not null) WriteSignature("committer", commit.Committer);
        _writer.WriteLine($"data {Encoding.UTF8.GetByteCount(commit.Message)}");
        _writer.WriteLine(commit.Message);
        WriteParentCommits(await commit.GetParentsAsync());
    }

    private void WriteSignature(string type, Signature signature) => _writer.WriteLine(
        $"{type} " +
        $"{signature.Name} " +
        $"<{signature.Email}> " +
        $"{signature.Timestamp.ToUnixTimeSeconds()} " +
        $"{signature.Timestamp.Offset.Minutes:+0000;-0000}");

    private void WriteParentCommits(IList<CommitEntry> parents)
    {
        if (parents.Count >= 1)
        {
            _writer.WriteLine($"from {parents[0].Id}");
            if (parents.Count >= 2)
            {
                _writer.WriteLine($"merge {parents[1].Id}");
            }
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
    }
}
