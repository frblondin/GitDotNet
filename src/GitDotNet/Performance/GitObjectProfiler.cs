using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Performance;

/// <summary>
/// Performance profiler for detailed Git object operations analysis
/// </summary>
internal sealed class GitObjectProfiler : IDisposable
{
    private readonly ILogger? _logger;
    private readonly Dictionary<string, ProfileData> _profiles = new();
    private readonly object _lock = new();
    private bool _isEnabled;

    public GitObjectProfiler(ILogger? logger = null, bool enabled = false)
    {
        _logger = logger;
        _isEnabled = enabled;
    }

    /// <summary>
    /// Enables or disables profiling
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
        if (enabled)
        {
            _logger?.LogInformation("Git object profiling enabled");
        } 
        else
        {
            _logger?.LogInformation("Git object profiling disabled");
        }
    }

    /// <summary>
    /// Starts profiling an operation
    /// </summary>
    public ProfiledOperation StartOperation(string operationName, 
        [CallerMemberName] string? memberName = null,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        if (!_isEnabled)
        {
            return new ProfiledOperation(null, null);
        }

        var fullOperationName = $"{Path.GetFileNameWithoutExtension(filePath)}.{memberName}:{lineNumber}:{operationName}";
        var stopwatch = Stopwatch.StartNew();
        
        _logger?.LogTrace("Starting operation: {Operation}", fullOperationName);
        
        return new ProfiledOperation(this, new OperationContext(fullOperationName, stopwatch));
    }

    /// <summary>
    /// Completes a profiled operation
    /// </summary>
    internal void CompleteOperation(OperationContext context, Dictionary<string, object>? metadata = null)
    {
        if (!_isEnabled || context == null)
            return;

        context.Stopwatch.Stop();
        var duration = context.Stopwatch.Elapsed;

        lock (_lock)
        {
            if (!_profiles.TryGetValue(context.OperationName, out var profile))
            {
                profile = new ProfileData(context.OperationName);
                _profiles[context.OperationName] = profile;
            }

            profile.RecordExecution(duration, metadata);
        }

        _logger?.LogTrace("Completed operation: {Operation} in {Duration}ms", 
            context.OperationName, duration.TotalMilliseconds);
    }

    /// <summary>
    /// Gets performance report for all profiled operations
    /// </summary>
    public string GetPerformanceReport()
    {
        if (!_isEnabled)
            return "Profiling is disabled";

        lock (_lock)
        {
            if (_profiles.Count == 0)
                return "No profiling data available";

            var report = new System.Text.StringBuilder();
            report.AppendLine("Git Object Profiler Report:");
            report.AppendLine("=" + new string('=', 50));

            var sortedProfiles = _profiles.Values
                .OrderByDescending(p => p.TotalTime)
                .ToList();

            foreach (var profile in sortedProfiles)
            {
                report.AppendLine(profile.ToString());
                report.AppendLine();
            }

            return report.ToString();
        }
    }

    /// <summary>
    /// Clears all profiling data
    /// </summary>
    public void ClearProfilingData()
    {
        lock (_lock)
        {
            _profiles.Clear();
        }
        _logger?.LogInformation("Profiling data cleared");
    }

    public void Dispose()
    {
        if (_isEnabled && _profiles.Count > 0)
        {
            _logger?.LogInformation("Final profiling report:\n{Report}", GetPerformanceReport());
        }
        ClearProfilingData();
    }

    internal record OperationContext(string OperationName, Stopwatch Stopwatch);

    private class ProfileData
    {
        public string OperationName { get; }
        public int ExecutionCount { get; private set; }
        public TimeSpan TotalTime { get; private set; }
        public TimeSpan MinTime { get; private set; } = TimeSpan.MaxValue;
        public TimeSpan MaxTime { get; private set; }
        public List<Dictionary<string, object>> MetadataHistory { get; } = new();

        public ProfileData(string operationName)
        {
            OperationName = operationName;
        }

        public void RecordExecution(TimeSpan duration, Dictionary<string, object>? metadata)
        {
            ExecutionCount++;
            TotalTime += duration;
            
            if (duration < MinTime)
                MinTime = duration;
            if (duration > MaxTime)
                MaxTime = duration;

            if (metadata != null)
            {
                MetadataHistory.Add(new Dictionary<string, object>(metadata));
            }
        }

        public TimeSpan AverageTime => ExecutionCount > 0 ? 
            TimeSpan.FromTicks(TotalTime.Ticks / ExecutionCount) : TimeSpan.Zero;

        public override string ToString()
        {
            return $"""
                Operation: {OperationName}
                  Executions: {ExecutionCount}
                  Total Time: {TotalTime.TotalMilliseconds:F2}ms
                  Average Time: {AverageTime.TotalMilliseconds:F2}ms
                  Min Time: {(MinTime == TimeSpan.MaxValue ? 0 : MinTime.TotalMilliseconds):F2}ms
                  Max Time: {MaxTime.TotalMilliseconds:F2}ms
                """;
        }
    }
}

/// <summary>
/// Represents a profiled operation that can be disposed to complete timing
/// </summary>
public readonly struct ProfiledOperation : IDisposable
{
    private readonly GitObjectProfiler? _profiler;
    private readonly GitObjectProfiler.OperationContext? _context;
    private readonly Dictionary<string, object> _metadata = new();

    internal ProfiledOperation(GitObjectProfiler? profiler, GitObjectProfiler.OperationContext? context)
    {
        _profiler = profiler;
        _context = context;
    }

    /// <summary>
    /// Adds metadata to the profiled operation
    /// </summary>
    public ProfiledOperation WithMetadata(string key, object value)
    {
        _metadata[key] = value;
        return this;
    }

    /// <summary>Completes the profiled operation and records its metrics.</summary>
    public void Dispose()
    {
        _profiler?.CompleteOperation(_context!, _metadata.Count > 0 ? _metadata : null);
    }
}