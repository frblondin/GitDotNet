using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal delegate LfsReader LfsReaderFactory(string path);

internal partial class LfsReader : LooseReader
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LfsReader>? _logger;

    public LfsReader(string path, IFileSystem fileSystem, ILogger<LfsReader>? logger = null)
        : base(path, fileSystem, logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    protected override int NominalHexStringLength => 64;

    protected override string GetObjectFolder(string hexString) =>
        _fileSystem.Path.Combine(hexString[..2], hexString[2..4]);

    protected override string GetFileName(string hexString) => hexString;

    public override (EntryType Type, Func<Stream>? DataProvider, long Length) TryLoad(string hexString)
    {
        _logger?.LogDebug("LfsReader.TryLoad called for {HexString}", hexString);
        var objectPath = GetObjectPath(_fileSystem, hexString);
        if (!_fileSystem.File.Exists(objectPath))
        {
            _logger?.LogDebug("LFS object {HexString} not found at path {ObjectPath}.", hexString, objectPath);
            return (default, default, -1);
        }
        _logger?.LogDebug("LFS object {HexString} found at path {ObjectPath}", hexString, objectPath);
        return (EntryType.Blob, () => _fileSystem.File.OpenReadAsynchronous(objectPath), -1);
    }

    public Stream Load(string sha256)
    {
        _logger?.LogDebug("LfsReader.Load called for {Sha256}", sha256);
        var (_, provider, _) = TryLoad(sha256);
        if (provider is null)
        {
            _logger?.LogWarning("Object {Sha256} could not be found in LFS.", sha256);
            throw new InvalidOperationException($"Object {sha256} could not be found in LFS. Make sure to run 'git lfs fetch' or 'git lfs fetch --all' for all branches and commits.");
        }
        _logger?.LogDebug("LFS object {Sha256} loaded successfully", sha256);
        return provider.Invoke();
    }
}