using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GitDotNet.Indexing.LiteDb.Converters;

internal class HashIdConversion : ValueConverter<HashId, byte[]>
{
    public HashIdConversion()
        : base(
            v => v.Hash.ToArray(),
            v => new HashId(v))
    {
    }
}
