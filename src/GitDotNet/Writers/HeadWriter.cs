using System.IO.Abstractions;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Writers;
internal delegate HeadWriter HeadWriterFactory(IRepositoryInfo info);

internal class HeadWriter(IRepositoryInfo info, IFileSystem fileSystem, ILogger<HeadWriter>? logger = null)
{
    public void UpdateHead(string branch)
    {
        if (info.CurrentOperation != CurrentOperation.None)
        {
            logger?.LogError("Cannot update HEAD to {Branch} while operation {CurrentOperation} is ongoing", branch, info.CurrentOperation);
            throw new InvalidOperationException("Cannot update HEAD while an operation is ongoing.");
        }
        
        var path = fileSystem.Path.Combine(info.Path, "HEAD");
        var refContent = $"ref: {branch}";
        
        logger?.LogInformation("Updating HEAD to point to {Branch} at path {HeadPath}", branch, path);
        
        File.WriteAllText(path, refContent);
        
        logger?.LogDebug("Successfully updated HEAD file with content: {RefContent}", refContent);
    }
}
