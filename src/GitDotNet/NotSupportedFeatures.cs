using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;

namespace GitDotNet;
/// <summary>Exception thrown when a Git repository uses unsupported features.</summary>
[ExcludeFromCodeCoverage]
public static class NotSupportedFeatures
{
    internal static void ThrowIfNeeded(RepositoryInfo info, IFileSystem fileSystem)
    {
        var featuresFound = new List<string>();

        CheckPromisorPacksFeature(info, fileSystem, featuresFound);
        CheckAlternatesFeature(info, fileSystem, featuresFound);
        CheckReftableFeature(info, fileSystem, featuresFound);
        CheckUnsupportedExtensions(info, featuresFound);

        // If any unsupported features were found, throw an exception
        if (featuresFound.Count > 0)
        {
            var message = featuresFound.Count == 1
                ? $"This repository uses an unsupported Git feature: {featuresFound[0]}. GitDotNet may not work correctly with this repository."
                : $"This repository uses unsupported Git features: {string.Join(", ", featuresFound)}. GitDotNet may not work correctly with this repository.";

            throw new NotSupportedException(message);
        }
    }

    private static void CheckPromisorPacksFeature(RepositoryInfo info, IFileSystem fileSystem, List<string> featuresFound)
    {
        // Promisor packs are indicated by .promisor files in the objects/pack directory
        var objectsPackPath = fileSystem.Path.Combine(info.Path, "objects", "pack");
        if (fileSystem.Directory.Exists(objectsPackPath))
        {
            var promisorFiles = fileSystem.Directory.GetFiles(objectsPackPath, "*.promisor", SearchOption.TopDirectoryOnly);
            if (promisorFiles.Length > 0)
            {
                featuresFound.Add("Promisor Packs (partial clone)");
            }
        }
    }

    private static void CheckAlternatesFeature(RepositoryInfo info, IFileSystem fileSystem, List<string> featuresFound)
    {
        // These are specified in objects/info/alternates file
        var alternatesPath = fileSystem.Path.Combine(info.Path, "objects", "info", "alternates");
        if (fileSystem.File.Exists(alternatesPath))
        {
            featuresFound.Add("Alternate Object DBs");
        }

        // Check for HTTP alternates (less common variant)
        var httpAlternatesPath = fileSystem.Path.Combine(info.Path, "objects", "info", "http-alternates");
        if (fileSystem.File.Exists(httpAlternatesPath))
        {
            featuresFound.Add("HTTP Alternate Object DBs");
        }
    }

    private static void CheckReftableFeature(RepositoryInfo info, IFileSystem fileSystem, List<string> featuresFound)
    {
        // This is indicated by extensions.refstorage = reftable in config
        // or the presence of a reftable directory
        try
        {
            var refstorage = info.Config.GetProperty("extensions", "refstorage", throwIfNull: false);
            if (string.Equals(refstorage, "reftable", StringComparison.OrdinalIgnoreCase))
            {
                featuresFound.Add("Reftable reference storage");
            }
        }
        catch (KeyNotFoundException)
        {
            // extensions section doesn't exist, which is fine
        }

        // Also check for reftable directory
        var reftablePath = fileSystem.Path.Combine(info.Path, "reftable");
        if (fileSystem.Directory.Exists(reftablePath))
        {
            featuresFound.Add("Reftable reference storage");
        }
    }

    private static void CheckUnsupportedExtensions(RepositoryInfo info, List<string> featuresFound)
    {
        try
        {
            var coreSection = info.Config.GetSection("core", throwIfNull: false);
            if (coreSection != null && coreSection.TryGetValue("repositoryformatversion", out var version) &&
                    int.TryParse(version, out var versionNumber) && versionNumber > 1)
            {
                featuresFound.Add($"Repository format version {versionNumber} (only version 0 and 1 are supported)");
            }

            var extensionsSection = info.Config.GetSection("extensions", throwIfNull: false);
            if (extensionsSection != null)
            {
                foreach (var extension in extensionsSection)
                {
                    // Check for known unsupported extensions
                    switch (extension.Key.ToLowerInvariant())
                    {
                        case "objectformat" when !string.Equals(extension.Value, "sha1", StringComparison.OrdinalIgnoreCase):
                            featuresFound.Add($"Object format '{extension.Value}' (only SHA-1 is supported)");
                            break;
                        case "worktreeconfig" when string.Equals(extension.Value, "true", StringComparison.OrdinalIgnoreCase):
                            featuresFound.Add("Worktree-specific configuration");
                            break;
                        case "partialclone":
                            featuresFound.Add($"Partial clone with remote '{extension.Value}'");
                            break;
                    }
                }
            }
        }
        catch (KeyNotFoundException)
        {
            // Config sections don't exist, which is fine for basic repositories
        }
    }
}
