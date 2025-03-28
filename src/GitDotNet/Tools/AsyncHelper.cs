using System.Diagnostics.CodeAnalysis;

[ExcludeFromCodeCoverage]
internal static class AsyncHelper
{
    private static readonly TaskFactory _taskFactory = new(CancellationToken.None,
                                                             TaskCreationOptions.None,
                                                             TaskContinuationOptions.None,
                                                             TaskScheduler.Default);

    public static TResult RunSync<TResult>(Func<Task<TResult>> func) => _taskFactory
        .StartNew(func)
        .Unwrap()
        .GetAwaiter()
        .GetResult();

    public static void RunSync(Func<Task> func) => _taskFactory
        .StartNew<Task>(func)
        .Unwrap()
        .GetAwaiter()
        .GetResult();
}