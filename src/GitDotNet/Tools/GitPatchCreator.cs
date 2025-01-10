using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using spkl.Diffs;
using Hunk = System.Collections.Generic.List<(string Prefix, string Data)>;

namespace GitDotNet.Tools;

internal partial class GitPatchCreator(int unified = GitPatchCreator.DefaultUnified, Regex? indentedHunkStart = null)
{
    internal const int DefaultUnified = 3;

    public async Task CreatePatchAsync(Stream stream, CommitEntry start, CommitEntry end, IList<Change> changes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(unified, 2);

        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
        {
            NewLine = "\n"
        };

        GetHeader(writer, start, end);
        GetDiffStat(writer, changes);

        var indexLine = $"index {start.Id.ToString()[..7]}..{end.Id.ToString()[..7]}";
        foreach (var change in changes)
        {
            await CreatePatchAsync(writer, change, indexLine);
        }
    }

    private static void GetHeader(StreamWriter writer, CommitEntry start, CommitEntry end)
    {
        var author = end.Author ?? throw new InvalidOperationException("Author is required.");
        writer.WriteLine($"From {start.Id} {author.Timestamp:ddd MMM dd HH:mm:ss yyyy}");
        writer.WriteLine($"From: {author.Name} <{author.Email}>");
        writer.WriteLine($"Date: {author.Timestamp:ddd, dd MMM yyyy HH:mm:ss +0000}");
        writer.WriteLine($"Subject: [PATCH] {end.Message}");
        writer.WriteLine();
    }

    private static void GetDiffStat(StreamWriter writer, IList<Change> changes)
    {
        writer.Write(' ');
        var modifications = changes.Count(c => c.Type == ChangeType.Modified || c.Type == ChangeType.Renamed);
        var additions = changes.Count(c => c.Type == ChangeType.Added);
        var deletions = changes.Count(c => c.Type == ChangeType.Removed);
        bool empty = true;
        if (modifications > 0 || (additions == 0 && deletions == 0))
        {
            writer.Write($"{modifications} {(modifications > 1 ? "files" : "file")} changed");
            empty = false;
        }
        if (additions > 0)
        {
            if (!empty) writer.Write(", ");
            writer.Write($"{additions} {(additions > 1 ? "insertions(+)" : "insertion(+)")}");
        }
        if (deletions > 0)
        {
            if (!empty) writer.Write(", ");
            writer.Write($"{deletions} {(deletions > 1 ? "deletions(-)" : "deletion(-)")}");
        }
        writer.WriteLine();
        writer.WriteLine();
    }

    private async Task CreatePatchAsync(StreamWriter writer, Change change, string indexLine)
    {
        WritePath(writer, "--- a/", change.OldPath);
        WritePath(writer, "+++ b/", change.NewPath);
        writer.WriteLine($"{indexLine} {change.New?.Mode ?? change.Old?.Mode}");

        // Hunks
        var oldBlob = change.Old is not null ? await change.Old.GetEntryAsync<BlobEntry>() : null;
        var newBlob = change.New is not null ? await change.New.GetEntryAsync<BlobEntry>() : null;
        if ((oldBlob?.IsText ?? true) && (newBlob?.IsText ?? true))
        {
            var hunks = GetHunks(oldBlob?.OpenRead(), newBlob?.OpenRead());
            foreach (var hunk in hunks)
            {
                WriteHunk(writer, hunk);
            }
        }
        else
        {
            writer.WriteLine("Binary files differ");
        }
    }

    private static void WriteHunk(StreamWriter writer, Hunk hunk)
    {
        foreach (var (prefix, data) in hunk)
        {
            writer.Write(prefix);
            writer.WriteLine(data);
        }
    }

    private static void WritePath(StreamWriter writer, string prefix, GitPath? path)
    {
        writer.Write(prefix);
        if (path is null)
        {
            writer.WriteLine("dev/null");
        }
        else
        {
            path.AppendTo(writer);
            writer.WriteLine();
        }
    }

    internal List<Hunk> GetHunks(Stream? oldContent, Stream? newContent)
    {
        var oldLines = oldContent is not null ? SplitLines(oldContent) : [];
        var newLines = newContent is not null ? SplitLines(newContent) : [];
        var changes = new MyersDiff<string>(oldLines, newLines, StringComparer.Ordinal).GetResult();

        var result = new List<Hunk> ();
        var current = new Hunk();
        int oldStart = -1, newStart = -1, oldEnd = -1, newEnd = -1, oldLastLineWithDiff = -1;
        int oldLineNum = 0, newLineNum = 0;
        int unchangedLineCount = 0;

        foreach (var (type, aItem, bItem) in changes)
        {
            if (type == ResultType.Both)
            {
                unchangedLineCount++;
                if (oldStart != -1 && newStart != -1)
                {
                    current.Add((" ", oldLines[oldLineNum]));
                    if (unchangedLineCount >= unified)
                    {
                        AddHunk();
                        oldStart = newStart = oldEnd = newEnd = oldLastLineWithDiff = -1;
                    }
                }

                oldEnd = oldLineNum;
                newEnd = newLineNum;

                oldLineNum++;
                newLineNum++;
            }
            else
            {
                oldLastLineWithDiff = oldLineNum;
                unchangedLineCount = 0;
                if (oldStart == -1 && newStart == -1)
                {
                    oldStart = oldLineNum;
                    newStart = newLineNum;
                }

                oldEnd = oldLineNum;
                newEnd = newLineNum;

                if (type == ResultType.A)
                {
                    current.Add(("-", oldLines[oldLineNum]));
                    oldLineNum++;
                }
                else if (type == ResultType.B)
                {
                    current.Add(("+", newLines[newLineNum]));
                    newLineNum++;
                }
            }
        }

        AddHunk();

        void AddHunk()
        {
            if (oldStart != -1 && newStart != -1)
            {
                // Add context lines before the hunk
                var indentedHunkPos = FindIndentedHunkStart(oldLines, oldStart, out var leadingWhitespace);
                var oldContextStart = indentedHunkPos != -1 ? indentedHunkPos : Math.Max(0, oldStart - unified);
                var newContextStart = newStart - (oldStart - oldContextStart);
                for (var i = oldStart - 1; i >= oldContextStart; i--)
                {
                    current.Insert(0, (" ", oldLines[i]));
                }

                TruncateIndentedHunkEnd(current, leadingWhitespace, oldLines, ref oldEnd, ref newEnd, oldLastLineWithDiff);

                current.Insert(0, ("", $"@@ -{oldContextStart + 1},{oldEnd - oldContextStart + 1} +{newContextStart + 1},{newEnd - newContextStart + 1} @@"));
                result.Add(current);

                current = [];
                oldStart = newStart = oldEnd = newEnd = -1;
            }
        }

        return result;
    }

    private int FindIndentedHunkStart(string[] oldLines, int oldStart, out string? leadingWhitespace)
    {
        leadingWhitespace = null;
        if (indentedHunkStart is null) return -1;
        for (var i = oldStart; i >= Math.Max(0, oldStart - unified); i--)
        {
            if (i < oldLines.Length && indentedHunkStart.IsMatch(oldLines[i]))
            {
                var match = LeadingWhitespaceRegex().Match(oldLines[i]);
                leadingWhitespace = match.Value;

                return i;
            }
        }

        return -1;
    }

    private void TruncateIndentedHunkEnd(Hunk hunk, string? leadingWhitespace, string[] oldLines, ref int oldEnd, ref int newEnd, int oldLastLineWithDiff)
    {
        if (leadingWhitespace is not null)
        {
            for (var i = oldLastLineWithDiff; i < Math.Min(oldLines.Length, oldLastLineWithDiff + unified); i++)
            {
                if (oldLines[i].StartsWith(leadingWhitespace) &&
                    oldLines[i].Length > leadingWhitespace.Length &&
                    !char.IsWhiteSpace(oldLines[i][leadingWhitespace.Length]) &&

                    i < oldEnd)
                {
                    var truncatedCount = oldEnd - i;
                    newEnd -= truncatedCount;
                    oldEnd -= truncatedCount;
                    hunk.RemoveRange(hunk.Count - truncatedCount, truncatedCount);
                    break;
                }
            }
        }
    }

    private static string[] SplitLines(Stream content)
    {
        var result = new List<string>();
        var reader = new StreamReader(content, Encoding.UTF8);
        string? line = null;
        while ((line = reader.ReadLine()) is not null)
        {
            result.Add(line);
        }
        return [.. result];
    }

    private sealed class CharMemoryEqualityComparer : IEqualityComparer<ReadOnlyMemory<char>>
    {
        public static CharMemoryEqualityComparer Instance { get; } = new();

        private CharMemoryEqualityComparer() { }

        public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y) => x.Span.SequenceEqual(y.Span);

        public int GetHashCode(ReadOnlyMemory<char> mem)
        {
            var span = mem.Span;
            int result = 0;
            for (int i = 0; i < span.Length; i++)
            {
                result = HashCode.Combine(result, span[i]);
            }
            return result;
        }
    }

    [GeneratedRegex(@"^\s*")]
    private static partial Regex LeadingWhitespaceRegex();
}