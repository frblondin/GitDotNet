using System.Text;
using GitDotNet.Tools;
using Microsoft.Extensions.Hosting;
using static System.Console;
using static GitDotNet.Console.Tools.InfoInput;

namespace GitDotNet.Console;

public class CreateBenchmarkData(GitConnectionProvider factory, IHostApplicationLifetime host) : BackgroundService
{
    private int LineLength => 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData($"{RepositoryPathInput} for benchmark", path => !Directory.Exists(path));
        GitConnection.Create(path, isBare: true);
        var recordCount = int.Parse(InputData("Number of records to create", x => int.TryParse(x, out var count) && count > 0, "50000"));
        var recordSize = int.Parse(InputData("Size of each record in char count", x => int.TryParse(x, out var size) && size > 0, "10000"));
        using var connection = factory.Invoke(path);
        var lines = Enumerable.Range(0, recordSize / LineLength)
            .Select(j => new string((char)('a' + j % 26), LineLength) + '\n')
            .ToList();
        var random = new Random();
        var commit = await connection.CommitAsync("main",
            CreateData,
            connection.CreateCommit("Commit message",
                            [],
                            new("test", "test@corporate.com", DateTimeOffset.Now),
                            new("test", "test@corporate.com", DateTimeOffset.Now)));

        WriteLine($"Created {recordCount} records with size {recordSize} chars each in commit {commit.Id}.");
        host.StopApplication();

        void CreateData(ITransformationComposer composer)
        {
            for (int i = 0; i < recordCount; i++)
            {
                var fileName = $"test_{i}.txt";
                var builder = new StringBuilder();
                foreach (var line in lines.OrderBy(_ => random.Next()))
                {
                    builder.Append(line);
                }
                composer.AddOrUpdate(fileName, builder.ToString());
            }
        }
    }
}
