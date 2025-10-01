using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace GitDotNet.Resilience;

/// <summary>
/// Retry policy configuration for Git operations
/// </summary>
public class RetryPolicyOptions
{
    /// <summary>
    /// Maximum number of retry attempts (default: 3)
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay between retries (default: 100ms)
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum delay between retries (default: 5 seconds)
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Backoff multiplier for exponential backoff (default: 2.0)
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Whether to add jitter to retry delays (default: true)
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Exceptions that should trigger retries
    /// </summary>
    public HashSet<Type> RetryableExceptions { get; set; } = new()
    {
        typeof(IOException),
        typeof(UnauthorizedAccessException),
        typeof(DirectoryNotFoundException),
        typeof(FileNotFoundException),
        typeof(TimeoutException)
    };

    /// <summary>
    /// Exceptions that should never trigger retries
    /// </summary>
    public HashSet<Type> NonRetryableExceptions { get; set; } = new()
    {
        typeof(ArgumentException),
        typeof(ArgumentNullException),
        typeof(InvalidOperationException),
        typeof(ObjectDisposedException)
    };
}

/// <summary>
/// Advanced error handling and retry policy engine for Git operations
/// </summary>
internal sealed class GitOperationResilience
{
    private readonly RetryPolicyOptions _options;
    private readonly ILogger? _logger;
    private readonly Random _jitterRandom = new();

    public GitOperationResilience(RetryPolicyOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes an operation with retry policy
    /// </summary>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        Exception? lastException = null;

        while (attempt <= _options.MaxRetries)
        {
            try
            {
                _logger?.LogTrace("Executing {Operation}, attempt {Attempt}/{MaxAttempts}", 
                    operationName, attempt + 1, _options.MaxRetries + 1);

                var result = await operation().ConfigureAwait(false);
                
                if (attempt > 0)
                {
                    _logger?.LogInformation(
                        "Operation {Operation} succeeded on attempt {Attempt}", 
                        operationName, attempt + 1);
                }

                return result;
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt))
            {
                lastException = ex;
                attempt++;

                var delay = CalculateDelay(attempt);
                
                _logger?.LogWarning(
                    "Operation {Operation} failed on attempt {Attempt}: {Error}. Retrying in {Delay}ms",
                    operationName, attempt, ex.Message, delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Non-retryable exception
                _logger?.LogError(
                    "Operation {Operation} failed with non-retryable exception: {Error}",
                    operationName, ex.Message);

                throw new GitOperationException(
                    $"Operation '{operationName}' failed with non-retryable exception", 
                    ex);
            }
        }

        // All retries exhausted
        _logger?.LogError(
            "Operation {Operation} failed after {Attempts} attempts. Final error: {Error}",
            operationName, attempt, lastException?.Message);

        throw new GitOperationException(
            $"Operation '{operationName}' failed after {attempt} attempts", 
            lastException);
    }

    /// <summary>
    /// Executes an operation with retry policy (void return)
    /// </summary>
    public async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true; // Dummy return value
        }, operationName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines if an exception should trigger a retry
    /// </summary>
    private bool ShouldRetry(Exception exception, int currentAttempt)
    {
        if (currentAttempt >= _options.MaxRetries)
            return false;

        var exceptionType = exception.GetType();

        // Check non-retryable exceptions first
        if (_options.NonRetryableExceptions.Any(type => type.IsAssignableFrom(exceptionType)))
        {
            _logger?.LogDebug("Exception {ExceptionType} is non-retryable", exceptionType.Name);
            return false;
        }

        // Check retryable exceptions
        if (_options.RetryableExceptions.Any(type => type.IsAssignableFrom(exceptionType)))
        {
            _logger?.LogDebug("Exception {ExceptionType} is retryable", exceptionType.Name);
            return true;
        }

        // Special handling for specific exceptions
        return exception switch
        {
            HttpRequestException httpEx when IsTransientHttpError(httpEx) => true,
            SocketException => true,
            TaskCanceledException => false, // Usually timeout, but don't retry
            OperationCanceledException => false,
            _ => false
        };
    }

    /// <summary>
    /// Determines if an HTTP error is transient and retryable
    /// </summary>
    private static bool IsTransientHttpError(HttpRequestException httpException)
    {
        var message = httpException.Message.ToLowerInvariant();
        return message.Contains("timeout") ||
               message.Contains("connection") ||
               message.Contains("network") ||
               message.Contains("temporary");
    }

    /// <summary>
    /// Calculates delay for the next retry attempt
    /// </summary>
    private TimeSpan CalculateDelay(int attempt)
    {
        if (attempt <= 0)
            return TimeSpan.Zero;

        // Exponential backoff: baseDelay * (multiplier ^ (attempt - 1))
        var exponentialDelay = TimeSpan.FromMilliseconds(
            _options.BaseDelay.TotalMilliseconds * Math.Pow(_options.BackoffMultiplier, attempt - 1));

        // Cap at maximum delay
        var cappedDelay = exponentialDelay > _options.MaxDelay ? _options.MaxDelay : exponentialDelay;

        // Add jitter if enabled
        if (_options.UseJitter)
        {
            var jitterFactor = _jitterRandom.NextDouble() * 0.5 + 0.75; // 75% to 125%
            cappedDelay = TimeSpan.FromMilliseconds(cappedDelay.TotalMilliseconds * jitterFactor);
        }

        return cappedDelay;
    }
}

/// <summary>
/// Exception thrown when Git operations fail after all retry attempts
/// </summary>
public class GitOperationException : Exception
{
    /// <summary>Initializes a new instance of the GitOperationException class with a specified error message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public GitOperationException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the GitOperationException class with a specified error message and a reference to the inner exception that is the cause of this exception.</summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
    public GitOperationException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Circuit breaker for Git operations to prevent cascading failures
/// </summary>
internal sealed class GitOperationCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;
    private int _failureCount;
    private DateTime _nextAttempt = DateTime.MinValue;
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private readonly object _lock = new();

    public GitOperationCircuitBreaker(int failureThreshold = 5, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _failureThreshold = failureThreshold;
        _timeout = timeout ?? TimeSpan.FromMinutes(1);
        _logger = logger;
    }

    /// <summary>
    /// Executes an operation through the circuit breaker
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string operationName)
    {
        CheckState();

        try
        {
            var result = await operation().ConfigureAwait(false);
            OnSuccess();
            return result;
        }
        catch (Exception ex)
        {
            OnFailure(operationName, ex);
            throw;
        }
    }

    private void CheckState()
    {
        lock (_lock)
        {
            if (_state == CircuitBreakerState.Open)
            {
                if (DateTime.UtcNow >= _nextAttempt)
                {
                    _state = CircuitBreakerState.HalfOpen;
                    _logger?.LogInformation("Circuit breaker transitioning to Half-Open state");
                }
                else
                {
                    throw new CircuitBreakerOpenException("Circuit breaker is open");
                }
            }
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            if (_state == CircuitBreakerState.HalfOpen)
            {
                _state = CircuitBreakerState.Closed;
                _logger?.LogInformation("Circuit breaker transitioning to Closed state");
            }
        }
    }

    private void OnFailure(string operationName, Exception exception)
    {
        lock (_lock)
        {
            _failureCount++;
            
            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitBreakerState.Open;
                _nextAttempt = DateTime.UtcNow.Add(_timeout);
                
                _logger?.LogWarning(
                    "Circuit breaker opened due to {FailureCount} failures. Operation: {Operation}, Last error: {Error}",
                    _failureCount, operationName, exception.Message);
            }
        }
    }
}

/// <summary>Represents the state of a circuit breaker.</summary>
public enum CircuitBreakerState
{
    /// <summary>The circuit breaker is closed and operations are allowed to execute.</summary>
    Closed,
    /// <summary>The circuit breaker is open and operations are blocked.</summary>
    Open,
    /// <summary>The circuit breaker is half-open and testing if operations can resume.</summary>
    HalfOpen
}

/// <summary>Exception thrown when an operation is attempted while the circuit breaker is open.</summary>
public class CircuitBreakerOpenException : Exception
{
    /// <summary>Initializes a new instance of the CircuitBreakerOpenException class with a specified error message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public CircuitBreakerOpenException(string message) : base(message)
    {
    }
}