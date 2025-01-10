using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GitDotNet.Indexing.LiteDb.Converters;

internal class HashIdComparer : ValueComparer<HashId>
{
    public HashIdComparer()
        : base(
            (c1, c2) => c1.Equals(c2),
            c => c.GetHashCode())
    {
    }
}
