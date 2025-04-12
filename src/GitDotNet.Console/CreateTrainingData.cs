using System.Text.Json;
using System.Text.RegularExpressions;
using GitDotNet.Console.Tools;
using GitDotNet.Tools;
using Microsoft.Extensions.Hosting;
using static GitDotNet.Console.Tools.InfoInput;
using static System.Console;

namespace GitDotNet.Console;

public class CreateTrainingData(GitConnectionProvider factory) : BackgroundService
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
        using var stream = File.Open(file, System.IO.FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonlWriter(stream);

        int addedCommits = 0;
        int skippedCommits = 0;
        int commitsInError = 0;

        await foreach (var logEntry in connection.GetLogAsync(start,
            LogOptions.Default with { SortBy = LogTraversal.FirstParentOnly }))
        {
            SetCursorPosition(0, CursorTop);
            Write($"Added commits: {addedCommits}, Excluded commits: {skippedCommits}, Commits in error: {commitsInError}");

            if (stoppingToken.IsCancellationRequested)
                break;

            var commit = await logEntry.GetCommitAsync();
            if (logEntry.ParentIds.Any() && !exclusion.IsMatch(commit.Message))
            {
                var patch = await GetPatch(connection, commit);

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

    private static async Task<PooledMemoryStream> GetPatch(GitConnection connection, CommitEntry commit)
    {
        var result = new PooledMemoryStream();
        await connection.CompareAsync((await commit.GetParentsAsync())[0], commit, result, 10);
        result.Position = 0;
        return result;
    }

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