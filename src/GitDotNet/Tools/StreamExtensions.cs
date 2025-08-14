using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace System.IO;

internal static class StreamExtensions
{
    [ExcludeFromCodeCoverage]
    public static byte ReadNonEosByteOrThrow(this Stream stream)
    {
        var b = stream.ReadByte();
        if (b == -1)
        {
            throw new InvalidOperationException("Unexpected end of stream.");
        }
        return (byte)b;
    }

    public static async Task<byte[]> ToArrayAsync(this Stream stream, long length)
    {
        var result = new byte[length];
        int position = 0;
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(result.AsMemory(position, (int)remaining)).ConfigureAwait(false);
            if (read == 0) break;
            remaining -= read;
            position += read;
        }
        return result;
    }

    public static bool HasAnyNullCharacter(this Stream stream, bool leaveOpen = false, int analyzedBytes = 8_000)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(analyzedBytes);
        try
        {
            int bytesRead = stream.Read(buffer, 0, analyzedBytes);
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!leaveOpen)
            {
                stream.Close();
            }
        }
    }
}
