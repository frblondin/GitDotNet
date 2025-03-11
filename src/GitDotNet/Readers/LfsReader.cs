using System.IO.Abstractions;

namespace GitDotNet.Readers;

internal delegate LfsReader LfsReaderFactory(string path);

internal partial class LfsReader(string path, IFileSystem fileSystem) : LooseReader(path, fileSystem)
{
    protected override int NominalHexStringLength => 64;

    protected override string GetObjectFolder(string hexString) =>
        fileSystem.Path.Combine(hexString[..2], hexString[2..4]);

    protected override string GetFileName(string hexString) => hexString;

    public override (EntryType Type, Func<Stream>? DataProvider, long Length) TryLoad(string hexString)
    {
        var objectPath = GetObjectPath(fileSystem, hexString);
        if (!fileSystem.File.Exists(objectPath))
        {
            return (default, default, -1);
        }

        return (EntryType.Blob, () => fileSystem.File.OpenReadAsynchronous(objectPath), -1);
    }

    public Stream Load(string sha256)
    {
        var (_, provider, _) = TryLoad(sha256);
        if (provider is null)
        {
            throw new InvalidOperationException($"Object {sha256} could not be found in LFS. Make sure to run 'git lfs fetch' or 'git lfs fetch --all' for all branches and commits.");
        }
        return provider.Invoke();
    }
}