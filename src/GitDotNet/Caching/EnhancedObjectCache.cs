using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GitDotNet.Caching;

/// <summary>
/// Advanced caching options for ObjectResolver with TTL and eviction policies
/// </summary>
public class ObjectResolverCacheOptions
{
    /// <summary>
    /// Default TTL for cached objects (default: 5 minutes)
    /// </summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TTL for small objects (under 1KB, default: 10 minutes)
    /// </summary>
    public TimeSpan SmallObjectTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// TTL for large objects (over 1MB, default: 2 minutes)
    /// </summary>
    public TimeSpan LargeObjectTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum cache size in bytes (default: 100MB)
    /// </summary>
    public long MaxCacheSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Small object size threshold in bytes (default: 1KB)
    /// </summary>
    public long SmallObjectThreshold { get; set; } = 1024;

    /// <summary>
    /// Large object size threshold in bytes (default: 1MB)
    /// </summary>
    public long LargeObjectThreshold { get; set; } = 1024 * 1024;

    /// <summary>
    /// Priority for different object types
    /// </summary>
    public Dictionary<EntryType, CacheItemPriority> TypePriorities { get; set; } = new()
    {
        { EntryType.Commit, CacheItemPriority.High },
        { EntryType.Tree, CacheItemPriority.High },
        { EntryType.Blob, CacheItemPriority.Normal },
        { EntryType.Tag, CacheItemPriority.High }
    };

    /// <summary>
    /// Whether to enable cache statistics tracking
    /// </summary>
    public bool EnableStatistics { get; set; } = true;

    /// <summary>
    /// Compaction threshold - when to trigger cache cleanup (default: 80%)
    /// </summary>
    public double CompactionThreshold { get; set; } = 0.8;
}

/// <summary>
/// Enhanced cache wrapper with advanced TTL and eviction policies
/// </summary>
internal class EnhancedObjectCache : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ObjectResolverCacheOptions _options;
    private readonly ILogger? _logger;
    private readonly Timer _compactionTimer;
    private long _currentCacheSize;
    private long _totalHits;
    private long _totalMisses;
    private readonly object _statsLock = new();

    public EnhancedObjectCache(IMemoryCache cache, IOptions<ObjectResolverCacheOptions> options, ILogger? logger = null)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;

        // Set up periodic compaction
        _compactionTimer = new Timer(PerformCompaction, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Gets or creates a cached entry with enhanced TTL logic
    /// </summary>
    public async Task<T?> GetOrCreateAsync<T>(object key, Func<ICacheEntry, Task<T?>> factory) where T : class
    {
        var cacheKey = $"enhanced:{key}";
        
        if (_cache.TryGetValue(cacheKey, out T? cachedValue))
        {
            RecordHit();
            _logger?.LogTrace("Cache hit for key: {Key}", key);
            return cachedValue;
        }

        RecordMiss();
        _logger?.LogTrace("Cache miss for key: {Key}", key);

        // Create cache entry with custom options
        using var entry = _cache.CreateEntry(cacheKey);
        var value = await factory(entry).ConfigureAwait(false);

        if (value != null)
        {
            ConfigureCacheEntry(entry, value);
        }

        return value;
    }

    /// <summary>
    /// Configures cache entry with TTL and priority based on object characteristics
    /// </summary>
    private void ConfigureCacheEntry<T>(ICacheEntry entry, T value) where T : class
    {
        long objectSize = EstimateObjectSize(value);
        var ttl = CalculateTtl(value, objectSize);
        var priority = DeterminePriority(value, objectSize);

        entry.AbsoluteExpirationRelativeToNow = ttl;
        entry.Priority = priority;
        entry.Size = objectSize;

        // Track size
        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
        {
            EvictionCallback = OnEviction,
            State = objectSize
        });

        Interlocked.Add(ref _currentCacheSize, objectSize);

        _logger?.LogTrace(
            "Cached object: size={Size} bytes, ttl={Ttl}ms, priority={Priority}",
            objectSize, ttl.TotalMilliseconds, priority);
    }

    /// <summary>
    /// Calculates TTL based on object type and size
    /// </summary>
    private TimeSpan CalculateTtl<T>(T value, long size) where T : class
    {
        // Adjust TTL based on size
        if (size <= _options.SmallObjectThreshold)
        {
            return _options.SmallObjectTtl;
        }
        if (size >= _options.LargeObjectThreshold)
        {
            return _options.LargeObjectTtl;
        }

        return _options.DefaultTtl;
    }

    /// <summary>
    /// Determines cache priority based on object type and size
    /// </summary>
    private CacheItemPriority DeterminePriority<T>(T value, long size) where T : class
    {
        // Check if it's an UnlinkedEntry with specific type
        if (value is UnlinkedEntry entry && _options.TypePriorities.TryGetValue(entry.Type, out var typePriority))
        {
            return typePriority;
        }

        // Large objects get lower priority
        if (size >= _options.LargeObjectThreshold)
        {
            return CacheItemPriority.Low;
        }

        return CacheItemPriority.Normal;
    }

    /// <summary>
    /// Estimates object size for cache management
    /// </summary>
    private static long EstimateObjectSize<T>(T value) where T : class
    {
        return value switch
        {
            UnlinkedEntry entry => 32 + entry.Data.LongLength, // Hash + metadata + data
            string str => str.Length * 2, // Unicode characters
            byte[] bytes => bytes.LongLength,
            _ => 256 // Default estimate
        };
    }

    /// <summary>
    /// Handles cache eviction to track size
    /// </summary>
    private void OnEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is long size)
        {
            Interlocked.Add(ref _currentCacheSize, -size);
        }

        _logger?.LogTrace("Cache eviction: key={Key}, reason={Reason}, size={Size}", key, reason, state);
    }

    /// <summary>
    /// Performs cache compaction when threshold is reached
    /// </summary>
    private void PerformCompaction(object? state)
    {
        var currentSize = Interlocked.Read(ref _currentCacheSize);
        var threshold = (long)(_options.MaxCacheSizeBytes * _options.CompactionThreshold);

        if (currentSize > threshold)
        {
            _logger?.LogInformation(
                "Cache compaction triggered: current={CurrentSize} bytes, threshold={Threshold} bytes",
                currentSize, threshold);

            // Force compaction by reducing memory pressure
            if (_cache is MemoryCache memoryCache)
            {
                memoryCache.Compact(1.0); // Remove 100% of expired items and low priority items
            }
        }
    }

    /// <summary>
    /// Records cache hit for statistics
    /// </summary>
    private void RecordHit()
    {
        if (_options.EnableStatistics)
        {
            Interlocked.Increment(ref _totalHits);
        }
    }

    /// <summary>
    /// Records cache miss for statistics
    /// </summary>
    private void RecordMiss()
    {
        if (_options.EnableStatistics)
        {
            Interlocked.Increment(ref _totalMisses);
        }
    }

    /// <summary>
    /// Gets cache statistics
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        var hits = Interlocked.Read(ref _totalHits);
        var misses = Interlocked.Read(ref _totalMisses);
        var size = Interlocked.Read(ref _currentCacheSize);

        return new CacheStatistics
        {
            TotalHits = hits,
            TotalMisses = misses,
            HitRate = hits + misses > 0 ? (double)hits / (hits + misses) : 0.0,
            CurrentSizeBytes = size,
            MaxSizeBytes = _options.MaxCacheSizeBytes
        };
    }

    public void Dispose()
    {
        _compactionTimer?.Dispose();
        
        if (_options.EnableStatistics)
        {
            var stats = GetStatistics();
            _logger?.LogInformation(
                "Cache statistics - Hits: {Hits}, Misses: {Misses}, Hit Rate: {HitRate:P2}, Size: {Size} bytes",
                stats.TotalHits, stats.TotalMisses, stats.HitRate, stats.CurrentSizeBytes);
        }
    }
}

/// <summary>
/// Cache performance statistics
/// </summary>
public class CacheStatistics
{
    /// <summary>Gets or sets the total number of cache hits.</summary>
    public long TotalHits { get; set; }
    /// <summary>Gets or sets the total number of cache misses.</summary>
    public long TotalMisses { get; set; }
    /// <summary>Gets or sets the cache hit rate as a percentage (0.0 to 1.0).</summary>
    public double HitRate { get; set; }
    /// <summary>Gets or sets the current cache size in bytes.</summary>
    public long CurrentSizeBytes { get; set; }
    /// <summary>Gets or sets the maximum cache size in bytes.</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>Returns a string representation of the cache statistics.</summary>
    /// <returns>A formatted string containing cache performance metrics.</returns>
    public override string ToString()
    {
        return $"""
            Cache Statistics:
            - Total Hits: {TotalHits:N0}
            - Total Misses: {TotalMisses:N0}
            - Hit Rate: {HitRate:P2}
            - Current Size: {CurrentSizeBytes:N0} bytes
            - Max Size: {MaxSizeBytes:N0} bytes
            - Utilization: {(double)CurrentSizeBytes / MaxSizeBytes:P2}
            """;
    }
}