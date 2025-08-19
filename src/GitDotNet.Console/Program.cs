using System.Reflection;
using GitDotNet.Console.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Type? hostedServiceType = null;
var backgroundServices = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.IsSubclassOf(typeof(BackgroundService)))
    .ToList();
for (int i = 0; i < backgroundServices.Count; i++)
{
    Console.WriteLine($"{i + 1}. {backgroundServices[i].Name}");
}
InfoInput.InputData($"Enter your choice (1 to {backgroundServices.Count})", i =>
{
    if (int.TryParse(i, out var selectedIndex) &&
        selectedIndex >= 1 && selectedIndex <= backgroundServices.Count)
    {
        hostedServiceType = backgroundServices[selectedIndex - 1];
        return true;
    }
    return false;
});

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddLogging(config => config
        .SetMinimumLevel(LogLevel.Warning)
        .ClearProviders()
        .AddDebug()) // Debug output windows
    .AddMemoryCache(o => o.SizeLimit = 100_000_000)
    .AddGitDotNet(o => o.SlidingCacheExpiration = TimeSpan.FromSeconds(5))
    .AddSingleton(typeof(IHostedService), hostedServiceType!);

using var host = builder.Build();
await host.RunAsync();