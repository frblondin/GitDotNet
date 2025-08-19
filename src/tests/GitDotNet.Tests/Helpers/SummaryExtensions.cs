using System.Linq.Expressions;
using BenchmarkDotNet.Reports;

namespace BenchmarkDotNet.Extensions;
internal static class SummaryExtensions
{
    public static TimeSpan GetActionMeanDuration<T>(this Summary summary, Expression<Action<T>> actionExp)
        where T : class =>
        TimeSpan.FromTicks((long)summary.GetReportFor(actionExp).ResultStatistics!.Mean / 100);
}
