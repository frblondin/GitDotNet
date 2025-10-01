using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Performance;

/// <summary>
/// Performance metrics and monitoring for ObjectResolver operations
/// </summary>
internal sealed class ObjectResolverMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _objectReadsCounter;
    private readonly Counter<long> _cacheHitsCounter;
    private readonly Counter<long> _cacheMissesCounter;
    private readonly Histogram<double> _objectReadDuration;
    private readonly Histogram<long> _objectSizeHistogram;
    private readonly Counter<long> _packReadsCounter;
    private readonly Counter<long> _looseReadsCounter;
    private readonly Counter<long> _deltaReconstructionsCounter;
    private readonly Histogram<double> _deltaReconstructionDuration;
    private readonly ILogger? _logger;

    public ObjectResolverMetrics(ILogger? logger = null)
    {
        _logger = logger;
        _meter = new Meter("GitDotNet.ObjectResolver", "1.0.0");
        
        _objectReadsCounter = _meter.CreateCounter<long>(
            "gitdotnet.objectresolver.reads.total",
            "operations",
            "Total number of object reads");
            
        _cacheHitsCounter = _meter.CreateCounter<long>(
            "gitdotnet.objectresolver.cache.hits.total",
            "operations", 
            "Total number of cache hits");
            
        _cacheMissesCounter = _meter.CreateCounter<long>(
            "gitdotnet.objectresolver.cache.misses.total",
            "operations",
            "Total number of cache misses");
            
        _objectReadDuration = _meter.CreateHistogram<double>(
            "gitdotnet.objectresolver.read.duration",
            "milliseconds",
            "Duration of object read operations");
            
        _objectSizeHistogram = _meter.CreateHistogram<long>(
            "gitdotnet.objectresolver.object.size",
            "bytes",
            "Size distribution of read objects");
            
        _packReadsCounter = _meter.CreateCounter<long>(
            "gitdotnet.objectresolver.pack.reads.total",
            "operations",
            "Total number of pack file reads");
            
        _looseReadsCounter = _meter.CreateCounter<long>(
            "gitdotnet.objectresolver.loose.reads.total", 
            "operations",
            "Total number of loose object reads");
            
        _deltaReconstructionsCounter = _meter.CreateCounter<long>(
            "gitdotnet.objectresolver.delta.reconstructions.total",
            "operations",
            "Total number of delta reconstructions");
            
        _deltaReconstructionDuration = _meter.CreateHistogram<double>(
            "gitdotnet.objectresolver.delta.reconstruction.duration",
            "milliseconds", 
            "Duration of delta reconstruction operations");
    }

    /// <summary>
    /// Records an object read operation with timing and size metrics
    /// </summary>
    public void RecordObjectRead(TimeSpan duration, long objectSize, string source, bool cacheHit)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("source", source),
            new("cache_hit", cacheHit)
        };

        _objectReadsCounter.Add(1, tags);
        _objectReadDuration.Record(duration.TotalMilliseconds, tags);
        _objectSizeHistogram.Record(objectSize, tags);

        if (cacheHit)
        {
            _cacheHitsCounter.Add(1);
        }
        else
        {
            _cacheMissesCounter.Add(1);
        }

        if (source == "pack")
        {
            _packReadsCounter.Add(1);
        }
        else if (source == "loose")
        {
            _looseReadsCounter.Add(1);
        }

        _logger?.LogDebug(
            "Object read: duration={Duration}ms, size={Size} bytes, source={Source}, cache_hit={CacheHit}",
            duration.TotalMilliseconds, objectSize, source, cacheHit);
    }

    /// <summary>
    /// Records a delta reconstruction operation
    /// </summary>
    public void RecordDeltaReconstruction(TimeSpan duration, long resultSize)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("result_size", resultSize)
        };

        _deltaReconstructionsCounter.Add(1, tags);
        _deltaReconstructionDuration.Record(duration.TotalMilliseconds, tags);

        _logger?.LogDebug(
            "Delta reconstruction: duration={Duration}ms, result_size={ResultSize} bytes",
            duration.TotalMilliseconds, resultSize);
    }

    /// <summary>
    /// Gets current performance statistics as a formatted string
    /// </summary>
    public string GetPerformanceReport()
    {
        // Note: In a real implementation, we'd need to maintain running totals
        // This is a simplified version for demonstration
        return $"""
            ObjectResolver Performance Metrics:
            - Cache Hit Rate: Calculated from hits/total reads
            - Average Read Duration: From histogram data
            - Pack vs Loose Read Ratio: From counter data
            - Delta Reconstruction Rate: From counter data
            """;
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}

/// <summary>
/// Performance measurement helper for timing operations
/// </summary>
internal readonly struct PerformanceTiming : IDisposable
{
    private readonly Stopwatch _stopwatch;
    private readonly Action<TimeSpan> _onComplete;

    public PerformanceTiming(Action<TimeSpan> onComplete)
    {
        _onComplete = onComplete;
        _stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        _onComplete(_stopwatch.Elapsed);
    }
}