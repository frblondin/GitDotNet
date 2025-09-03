using System.Buffers.Binary;
using System.IO.Abstractions;
using System.Text;
using GitDotNet.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal abstract partial class PackIndexReader
{
#pragma warning disable CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    internal class MultiPack(string path, PackReaderFactory packReaderFactory, FileOffsetStreamReaderFactory offsetStreamReaderFactory, IFileSystem fileSystem, IMemoryCache cache, ILogger<MultiPack>? logger = null)
        : PackIndexReader(path, packReaderFactory, offsetStreamReaderFactory, fileSystem, cache, logger)
#pragma warning restore CS9107 // Parameter is captured into the state of the enclosing type and its value is also passed to the base constructor. The value might be captured by the base class as well.
    {
        private long _sortedObjectNamesOffset = -1, _fanOutTableOffset = -1, _packFilePositionOffset = -1, _packFilePositionLongOffset = -1;

        protected override int HeaderLength => 12;

        override protected long FanOutTableOffset => _fanOutTableOffset;

        protected override long SortedObjectNamesOffset => _sortedObjectNamesOffset;

        protected override long PackFilePositionOffset => _packFilePositionOffset;

        override protected long PackFilePositionLongOffset => _packFilePositionLongOffset;

        protected override int ReadVersion(Stream stream)
        {
            // First 4 bytes are signature, next byte is version
            stream.Seek(4, SeekOrigin.Begin);
            return stream.ReadByte();
        }

        protected override int ReadHashLength(Stream stream)
        {
            var b = stream.ReadByte();
            return b switch
            {
                1 => 20,
                2 => 32,
                _ => throw new InvalidDataException("Invalid commit-graph hash version.")
            };
        }

        protected override IList<(string Path, Lazy<PackReader> Reader)> ReadPacks(Stream stream)
        {
            // 1-byte number of "chunks"
            var chunkCount = stream.ReadByte();

            // 1-byte number of base multi-pack-index files:
            // This value is currently always zero.
            stream.ReadByte();

            // 4-byte number of pack files
            var bytes = new byte[4];
            stream.ReadExactly(bytes);
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes);

            var (packNameOffset, length) = ReadChunks(stream, chunkCount);

            stream.Seek(packNameOffset, SeekOrigin.Begin);
            return ReadReaders(stream, count, length);
        }

        private (long PackNameOffset, int PackNameLength) ReadChunks(Stream stream, int chunkCount)
        {
            var packNameOffset = -1L;
            var chunkData = new byte[12];
            for (int i = 0; i < chunkCount; i++)
            {
                stream.ReadExactly(chunkData);
                var chunkId = Encoding.UTF8.GetString(chunkData, 0, 4);
                var offset = BinaryPrimitives.ReadInt64BigEndian(chunkData.AsSpan(4, 8));
                switch (chunkId)
                {
                    case "PNAM": packNameOffset = offset; break;
                    case "OIDF": _fanOutTableOffset = offset; break;
                    case "OIDL": _sortedObjectNamesOffset = offset; break;
                    case "OOFF": _packFilePositionOffset = offset; break;
                    case "LOFF": _packFilePositionLongOffset = offset; break;
                }
            }

            if (packNameOffset == -1 || _fanOutTableOffset == -1 || _sortedObjectNamesOffset == -1 || _packFilePositionOffset == -1)
            {
                throw new InvalidDataException("Missing required chunk(s) in multi-pack index.");
            }

            // Length of packNameOffset is closest greater chunk offset retrieved above - packNameOffset
            var offsetFollowingPackNameChunk = new[] { _fanOutTableOffset, _sortedObjectNamesOffset, _packFilePositionOffset, _packFilePositionLongOffset }
                .Where(o => o != -1 && o > packNameOffset)
                .OrderBy(o => o)
                .First();

            return (packNameOffset, (int)(offsetFollowingPackNameChunk - packNameOffset));
        }

        private List<(string Path, Lazy<PackReader> Reader)> ReadReaders(Stream stream, int count, int length)
        {
            var readers = new List<(string Path, Lazy<PackReader> Reader)>(count);
            var buffer = new byte[512];
            stream.ReadExactly(buffer);
            int byteIndex = 0, start = 0;
            for (int i = 0; i < count; i++)
            {
                var packFilePath = ReadUntilByte(length, buffer, ref start, ref byteIndex);
                if (!_fileSystem.File.Exists(packFilePath))
                {
                    throw new FileNotFoundException($"Pack file '{packFilePath}' not found for multi-pack index '{Path}'.");
                }
                readers.Add((packFilePath, new Lazy<PackReader>(() => packReaderFactory(packFilePath))));
            }
            return readers;
        }

        private string ReadUntilByte(int length, byte[] buffer, ref int start, ref int byteIndex)
        {
            while (buffer[byteIndex] != 0 && byteIndex < length)
            {
                byteIndex++;
            }
            var raw = Encoding.ASCII.GetString(buffer, start, byteIndex - start);
            var packName = _fileSystem.Path.ChangeExtension(raw, "pack");
            var packPath = _fileSystem.Path.Combine(_fileSystem.Path.GetDirectoryName(Path) ?? string.Empty, packName);
            start = ++byteIndex;
            return packPath;
        }

        protected override async Task<(PackReader, long)> GetPackFileOffsetAsync(int index, Stream stream)
        {
            var offset = new byte[8];
            stream.Seek(PackFilePositionOffset + index * 8, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(offset.AsMemory(0, 8)).ConfigureAwait(false);

            var packIndex = BinaryPrimitives.ReadInt32BigEndian(offset.AsSpan(0, 4));

            var result = (long)BinaryPrimitives.ReadInt32BigEndian(offset.AsSpan(4, 4));
            result = await CalculateLargePackFileOffset(index, stream, offset, result).ConfigureAwait(false);

            return (PackReaders[packIndex - 1].Reader.Value, result);
        }
    }
}