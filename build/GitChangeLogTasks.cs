using Nuke.Common.Tools.Git;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal static class GitChangeLogTasks
{
    public static bool AnyTag()
    {
        var any = GitTasks
            .Git("tag")
            .Any(x => !string.IsNullOrWhiteSpace(x.Text));
        if (!any)
        {
            Serilog.Log.Information("No tag yet");
        }
        return any;
    }

    public static IEnumerable<string> ChangedFilesSinceLastTag()
    {
        var lastTag = GitTasks
            .Git("describe --tags --abbrev=0")
            .Select(x => x.Text)
            .FirstOrDefault();
        Serilog.Log.Information("Found most recent tag '{LastTag}'", lastTag);

        var result = lastTag != null ? GitTasks
            .Git($"diff --name-only {lastTag}..HEAD")
            .Select(x => x.Text)
            .ToList() : null;
        Serilog.Log.Information("Found {ModifiedFilesCount} changes since last tag", result?.Count);

        return result ?? Enumerable.Empty<string>();
    }

    public static IEnumerable<string> CommitsSinceLastTag()
    {
        var logCommits = "";
        try
        {
            var lastTag = GitTasks
                .Git("describe --tags --abbrev=0")
                .Select(x => x.Text)
                .FirstOrDefault();
            logCommits = $"{lastTag}..HEAD";
            Serilog.Log.Information("Found most recent tag '{LastTag}'", lastTag);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Couldn't find last tag.");
        }

        var result = GitTasks
            .Git($"log --pretty=format:%s {logCommits}")
            .Select(x => x.Text)
            .ToList();
        Serilog.Log.Information("Found {ModifiedFilesCount} changes since last tag", result?.Count);
        return result;
    }

    public static IEnumerable<string> GetModifiedLinesSinceLastTag(string path)
    {
        var lastTag = GitTasks
            .Git("describe --tags --abbrev=0")
            .Select(x => x.Text)
            .FirstOrDefault();
        Serilog.Log.Information("Found most recent tag '{LastTag}'", lastTag);

        var result = lastTag != null ? GitTasks
            .Git($"diff {lastTag}..HEAD -- {path}")
            .Where(x => x.Text.StartsWith("+"))
            .Select(x => x.Text.Substring(1).Trim())
            .ToList() : null;
        Serilog.Log.Information("Found {ModifiedLinesCount} changes since last tag in file {path}", result?.Count, path);

        return result ?? Enumerable.Empty<string>();
    }
}
