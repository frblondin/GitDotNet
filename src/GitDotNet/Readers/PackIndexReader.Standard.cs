using System.Buffers.Binary;
using System.IO.Abstractions;
using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal abstract partial class PackIndexReader
{
#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    internal class Standard(string path, PackReaderFactory packReaderFactory, FileOffsetStreamReaderFactory offsetStreamReaderFactory, IFileSystem fileSystem, IMemoryCache cache, ILogger<Standard>? logger = null)
        : PackIndexReader(path, packReaderFactory, offsetStreamReaderFactory, fileSystem, cache, logger)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    {
        protected override int HeaderLength => 8;

        protected override long FanOutTableOffset => (long)HeaderLength;

        protected override long SortedObjectNamesOffset => Version switch
        {
            2 => HeaderLength + FanOutTableSize * 4,
            _ => throw new NotSupportedException($"Version {Version} is not supported.")
        };

        private long Crc32ValuesOffset => Version switch
        {
            2 => SortedObjectNamesOffset + Count * HashLength,
            _ => throw new NotSupportedException($"Version {Version} is not supported.")
        };

        protected override long PackFilePositionOffset => Version switch
        {
            2 => Crc32ValuesOffset + Count * 4,
            _ => throw new NotSupportedException($"Version {Version} is not supported.")
        };

        protected override long PackFilePositionLongOffset => Version switch
        {
            2 => PackFilePositionOffset + Count * (HashLength + 4),
            _ => throw new NotSupportedException($"Version {Version} is not supported.")
        };

        protected override int ReadVersion(Stream stream)
        {
            var bytes = new byte[4];
            stream.ReadExactly(bytes);
            var version = 1;
            // Version is set only if bytes equal 255, 116, 79, 99
            if (bytes[0] == 255 && bytes[1] == 116 && bytes[2] == 79 && bytes[3] == 99)
            {
                stream.ReadExactly(bytes);
                version = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4));
            }
            else
            {
                // In version 1 of pack files, the index does not have a header
                stream.Seek(0, SeekOrigin.Begin);
            }

            return version;
        }

        protected override int ReadHashLength(Stream stream)
        {
            // Do nothing, does not exist in standard index, only in multi pack index
            return 20;
        }

        protected override IList<(string Path, Lazy<PackReader> Reader)> ReadPacks(Stream stream) =>
            // Only one pack
            [(fileSystem.Path.ChangeExtension(Path, "pack"),
            new(() => packReaderFactory(fileSystem.Path.ChangeExtension(Path, "pack"))))];

        protected override async Task<(PackReader, long)> GetPackFileOffsetAsync(int index, Stream stream)
        {
            var offset = new byte[8];
            stream.Seek(PackFilePositionOffset + index * 4, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(offset.AsMemory(0, 4)).ConfigureAwait(false);

            var result = (long)BinaryPrimitives.ReadInt32BigEndian(offset.AsSpan(0, 4));
            result = await CalculateLargePackFileOffset(index, stream, offset, result).ConfigureAwait(false);

            return (PackReaders.Single().Reader.Value, result);
        }
    }
}