using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace GitDotNet;

public partial interface IGitConnection
{
    /// <summary>Provides configuration options for a Git connection.</summary>
    public class Options
    {
        /// <summary>
        /// Gets or sets the threshold for considering a rename or move operation (0.9 by default).
        /// The value should be between 0 and 1, where 1 means identical and 0 means completely different.
        /// </summary>
        public float RenameThreshold { get; set; } = 0.9f;

        /// <summary>
        /// Gets or sets how long the cache entry can be inactive (e.g. not accessed) before it will be removed.
        /// </summary>
        public TimeSpan? SlidingCacheExpiration { get; set; } = TimeSpan.FromMilliseconds(100);

        internal void ApplyTo(ICacheEntry entry, object? value, CancellationToken token)
        {
            if (value is not null)
            {
                entry.SetSize(1);
                entry.AddExpirationToken(new CancellationChangeToken(token));
                if (SlidingCacheExpiration.HasValue)
                {
                    entry.SetSlidingExpiration(SlidingCacheExpiration.Value);
                }
            }
            else
            {
                entry.Dispose();
                return;
            }
        }
    }

}