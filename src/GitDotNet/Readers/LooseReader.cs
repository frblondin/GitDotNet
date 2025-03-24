using System.Buffers;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;

namespace GitDotNet.Readers;

internal delegate LooseReader LooseReaderFactory(string path);

internal class LooseReader(string path, IFileSystem fileSystem)
{
    private const int HeaderMaxLength = 10;
    private const int SizeMaxLength = 64;
    private const byte SpaceChar = 0x20;
    private static readonly byte[] _commitHeader = Encoding.ASCII.GetBytes("commit");
    private static readonly byte[] _blobHeader = Encoding.ASCII.GetBytes("blob");
    private static readonly byte[] _treeHeader = Encoding.ASCII.GetBytes("tree");
    private static readonly byte[] _tagHeader = Encoding.ASCII.GetBytes("tag");

    protected virtual int NominalHexStringLength => 40;

    public virtual (EntryType Type, Func<Stream>? DataProvider, long Length) TryLoad(string hexString)
    {
        var objectPath = GetObjectPath(fileSystem, hexString);
        if (!fileSystem.File.Exists(objectPath))
        {
            return (default, default, -1);
        }

        using var stream = GetZLibStream(objectPath);
        var position = 0;
        var type = GetType(stream, ref position);
        var length = GetLength(stream, ref position);
        return (type, CreateStreamProvider(objectPath, position), length);
    }

    private Func<Stream> CreateStreamProvider(string objectPath, int position) =>
        () =>
        {
            var stream = GetZLibStream(objectPath);
            for (int i = 0; i < position; i++)
            {
                stream.ReadNonEosByteOrThrow();
            }
            return stream;
        };

    private ZLibStream GetZLibStream(string objectPath)
    {
        var stream = fileSystem.File.OpenReadAsynchronous(objectPath);
        return new ZLibStream(stream, CompressionMode.Decompress);
    }

    private static EntryType GetType(ZLibStream zlibStream, ref int position) =>
        ReadUntilStopChar(zlibStream, SpaceChar, HeaderMaxLength, (buffer, length) =>
        {
            if (AreBuffersEqual(buffer, _commitHeader, length))
                return EntryType.Commit;
            if (AreBuffersEqual(buffer, _blobHeader, length))
                return EntryType.Blob;
            if (AreBuffersEqual(buffer, _treeHeader, length))
                return EntryType.Tree;
            if (AreBuffersEqual(buffer, _tagHeader, length))
                return EntryType.Tag;

            throw new InvalidOperationException("Unknown object type.");
        }, ref position);

    private static int GetLength(ZLibStream stream, ref int position) =>
        ReadUntilStopChar(stream, 0, SizeMaxLength, (buffer, length) =>
        {
            var lengthString = Encoding.ASCII.GetString(buffer, 0, length);
            return int.Parse(lengthString);
        }, ref position);

    protected string GetObjectPath(IFileSystem fileSystem, string hexString)
    {
        string objectPath;
        var folder = fileSystem.Path.Combine(path, GetObjectFolder(hexString));
        var fileName = GetFileName(hexString);
        if (fileSystem.Path.Exists(folder) && hexString.Length < NominalHexStringLength)
        {
            var files = fileSystem.Directory.GetFiles(folder, $"{fileName}*");
            if (files.Length > 1)
                throw new AmbiguousHashException();
            objectPath = files.Length == 1 ? files[0] : "";
        }
        else
        {
            objectPath = fileSystem.Path.Combine(folder, fileName);
        }

        return objectPath;
    }

    protected virtual string GetObjectFolder(string hexString) => hexString[..2];

    protected virtual string GetFileName(string hexString) => hexString[2..];

    private static bool AreBuffersEqual(byte[] buffer1, byte[] buffer2, int length)
    {
        if (buffer1.Length < length || buffer2.Length < length)
            return false;
        for (var i = 0; i < length; i++)
        {
            if (buffer1[i] != buffer2[i])
                return false;
        }
        return true;
    }

    private static TResult ReadUntilStopChar<TResult>(Stream stream,
                                                      byte stopChar,
                                                      int maxLength,
                                                      Func<byte[], int, TResult> parser,
                                                      ref int position)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            var bytesRead = 0;
            while (true)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(bytesRead, maxLength);
                var b = stream.ReadNonEosByteOrThrow();
                position++;
                if (b == stopChar)
                    break;
                buffer[bytesRead++] = b;
            }
            return parser(buffer, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
