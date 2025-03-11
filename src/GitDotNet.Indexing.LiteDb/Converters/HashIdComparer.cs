using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GitDotNet.Indexing.LiteDb.Converters;

internal sealed class HashIdComparer : ValueComparer<HashId>
{
    public HashIdComparer()
        : base(
            (c1, c2) => c1 != null && c2 != null && c1.Equals(c2),
            c => c.GetHashCode())
    {
    }
}
