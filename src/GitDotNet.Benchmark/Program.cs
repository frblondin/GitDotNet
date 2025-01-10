using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

//var summary = BenchmarkRunner.Run<ReadRandomBlobsBenchmark>(// new DebugInProcessConfig());
//    ManualConfig.Create(DefaultConfig.Instance)
//    .AddJob(Job.Default.WithRuntime(CoreRuntime.Core90).AsBaseline())
//    .AddJob(Job.Default.WithRuntime(CoreRuntime.Core80))
//);
var summary = BenchmarkRunner.Run<ArchiveBenchmark>(
    ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default
        .WithLaunchCount(1)
        .WithWarmupCount(0)
        .WithIterationCount(3)));
Console.WriteLine(summary);