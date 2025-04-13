using System.Text.Json;
using System.Text.RegularExpressions;
using GitDotNet.Console.Tools;
using GitDotNet.Tools;
using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public partial class CreateTrainingData(GitConnectionProvider factory) : BackgroundService
{
    private const string SystemMessage = "You are a coding assistant. You transform git patches and documentation into complete code implementations.";
    private const string AssistantMessage = "[Expected full code implementation based on the patch]";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = InputData(RepositoryPathInput, Directory.Exists);
        var file = InputData("Result path");
        var exclusion = new Regex(InputData(@"Exclusion list", @default: "(fix\\:|\\[BUG\\]|Merge)"), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var start = InputData(@"Start", @default: "HEAD");
        using var connection = factory.Invoke(path);
        var rootFolder = connection.Info.GetRepositoryPath(path);
        using var stream = File.Open(file, System.IO.FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonlWriter(stream);

        int total = 0, addedCommits = 0, skippedCommits = 0, noInterestingFiles = 0, commitsInError = 0;

        await foreach (var logEntry in connection.GetLogAsync(start,
            LogOptions.Default with { SortBy = LogTraversal.FirstParentOnly, Start = DateTimeOffset.Now.AddYears(-1) })
            .WithCancellation(stoppingToken))
        {
            SetCursorPosition(0, CursorTop);
            Write($"Total processed: {total}, Added commits: {addedCommits}, Excluded commits: {skippedCommits}, No interesting files: {noInterestingFiles}, Commits in error: {commitsInError}");

            total++;
            var commit = await logEntry.GetCommitAsync();
            if (logEntry.ParentIds.Any() && !exclusion.IsMatch(commit.Message))
            {
                var parents = await commit.GetParentsAsync();
                var changes = (await connection.CompareAsync(parents[0], commit))
                    .Where(c => rootFolder.Contains(c.NewPath ?? c.OldPath!))
                    .ToList();
                if (changes.Count == 0)
                {
                    noInterestingFiles++;
                    continue;
                }

                var patch = new PooledMemoryStream();
                await new GitPatchCreator(20, TypeOrMemberDefinitionStartRegex()).WriteAsync(patch, parents[0], commit, changes);
                patch.Position = 0;

                try
                {
                    WriteCommitInstruction(writer, patch);
                    writer.StartNewJsonlObject();

                    addedCommits++;
                }
                catch (Exception ex)
                {
                    writer.RollbackJsonlObject();

                    SetCursorPosition(0, CursorTop);
                    WriteLine($"Error while processing {logEntry.Id}: {ex.Message}.");

                    commitsInError++;
                }
            }
            else
            {
                skippedCommits++;
            }
        }

        WriteLine();

        await StopAsync(stoppingToken);
    }

    private static void WriteCommitInstruction(Utf8JsonlWriter writer, PooledMemoryStream patch)
    {
        writer.JsonWriter.WriteStartObject();
        writer.JsonWriter.WriteStartArray("messages");
        WriteMessage(writer.JsonWriter, "system", SystemMessage);
        WriteMessage(writer.JsonWriter, "user", patch.GetByteSpan());
        WriteMessage(writer.JsonWriter, "assistant", AssistantMessage);
        writer.JsonWriter.WriteEndArray();
        writer.JsonWriter.WriteEndObject();

    }

    [GeneratedRegex(@"^\s*\b(public|private|protected|internal)\s+")]
    private static partial Regex TypeOrMemberDefinitionStartRegex();

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, ReadOnlySpan<byte> content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }
}