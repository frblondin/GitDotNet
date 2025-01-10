using System.Text;
using System.Text.RegularExpressions;

namespace GitDotNet;

/// <summary>Represents a Git blob entry, which contains file data.</summary>
public partial record class BlobEntry : Entry
{
    private static readonly byte[] _lfsPointerSignature = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\noid sha256:");

    private string? _text;
    private bool? _isText;
    private bool? _isLfs;
    private string? _lfsSha256;
    private readonly Func<string, Stream> _lfsDataProvider;

    internal BlobEntry(HashId id, byte[] data, Func<string, Stream> lfsDataProvider)
        : base(EntryType.Blob, id, data)
    {
        _lfsDataProvider = lfsDataProvider;
    }

    /// <summary>Gets a value indicating whether the Git entry is an LFS pointer file.</summary>
    public bool IsLfs => _isLfs ??=
        Data.Length >= _lfsPointerSignature.Length &&
        Data.AsSpan(0, _lfsPointerSignature.Length).SequenceEqual(_lfsPointerSignature);

    /// <summary>Reads the data of the Git entry.</summary>
    /// <returns>The stream containing the data of the Git entry.</returns>
    public Stream OpenRead() => IsLfs ? GetLfsContent() : new MemoryStream(Data);

    /// <summary>Gets a value indicating whether the blob contains text data.</summary>
    public bool IsText => _isText ??= !OpenRead().HasAnyNullCharacter();

    /// <summary>Gets the text content of the blob, decoded as a UTF-8 string.</summary>
    public string? GetText()
    {
        return _text ??= IsText ? ReadText() : null;

        string ReadText()
        {
            using var stream = OpenRead();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    private Stream GetLfsContent()
    {
        var sha = _lfsSha256 ??= GetLfsSha();
        return _lfsDataProvider(sha);
    }

    private string GetLfsSha()
    {
        var content = Encoding.UTF8.GetString(Data);
        var match = LfsPointerFormatRegEx().Match(content);
        if (!match.Success)
        {
            throw new NotSupportedException("The LFS pointer file format is not valid.");
        }
        var sha = match.Groups["sha256"].Value;
        return sha;
    }

    /// <summary>Gets the log of commits from the specified reference.</summary>
    /// <param name="branch">The branch to start from.</param>
    /// <param name="options">The options for the log.</param>
    /// <returns>An asynchronous enumerable of commit entries.</returns>
    public IAsyncEnumerable<CommitEntry> GetLogAsync(Branch branch, LogOptions? options = null)
    {
        var tip = branch.Tip ?? throw new NotSupportedException("Branch has no tip commit.");
        return branch.Connection.GetLogImplAsync(tip.Id.ToString(), options, this);
    }

    [GeneratedRegex(@"version https://git-lfs.github.com/spec/v1\noid sha256:(?<sha256>[a-f0-9]{64})\nsize (?<size>\d+)", RegexOptions.Compiled)]
    private static partial Regex LfsPointerFormatRegEx();
}
