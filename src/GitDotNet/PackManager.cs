using System.Collections.Concurrent;
using System.IO.Abstractions;
using GitDotNet.Readers;
using Microsoft.Extensions.Logging;

namespace GitDotNet;

internal delegate IPackManager PackManagerFactory(string path);

/// <summary>Provides methods for managing Git pack files.</summary>
internal interface IPackManager : IDisposable
{
    IEnumerable<PackReader> PackReaders { get; }

    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    /// <param name="force">Forces update even if no date has changed.</param>
    void UpdatePacks(bool force);
}

/// <summary>Provides methods for managing Git pack files.</summary>
internal class PackManager(string path, IFileSystem fileSystem, PackReaderFactory packReaderFactory, ILogger<PackManager>? logger = null) : IPackManager
{
    private readonly ConcurrentDictionary<string, Lazy<PackReader>> _packReaders = new(StringComparer.Ordinal);
    private DateTime? _lastInfoPacksTimestamp;

    public IEnumerable<PackReader> PackReaders
    {
        get
        {
            logger?.LogDebug("Accessing PackReaders property, updating packs if needed.");
            UpdatePacks(false);
            return from reader in _packReaders.Values
                   where !reader.Value.IsObsolete
                   select reader.Value;
        }
    }

    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    public void UpdatePacks(bool force)
    {
        logger?.LogInformation("Updating packs. Force: {Force}", force);
        if (_lastInfoPacksTimestamp != null && !force)
        {
            logger?.LogDebug("Skipping pack update due to timestamp and force flag.");
            return;
        }
        var packDir = fileSystem.Path.Combine(path, "pack");
        var validPackNames = AddFromPackDir(packDir);
        MarkPacksAsObsolete(validPackNames);
        _lastInfoPacksTimestamp = DateTime.Now;
        logger?.LogDebug("Pack update complete. Valid packs: {ValidPacks}", string.Join(",", validPackNames));
    }

    private HashSet<string> AddFromPackDir(string packDir)
    {
        logger?.LogDebug("Scanning pack directory: {PackDir}", packDir);
        var validPackNames = new HashSet<string>();
        var packFiles = fileSystem.Directory.Exists(packDir) ? fileSystem.Directory.GetFiles(packDir, "*.pack") : [];
        foreach (var packFile in packFiles)
        {
            var packName = fileSystem.Path.GetFileNameWithoutExtension(packFile);
            validPackNames.Add(packName);
            AddMissingPackReader(packName, packFile);
            logger?.LogDebug("Pack file found: {PackFile} (name: {PackName})", packFile, packName);
        }
        return validPackNames;
    }

    private void AddMissingPackReader(string packName, string packFile)
    {
        if (_packReaders.TryAdd(packName, new(() => packReaderFactory(packFile))))
        {
            logger?.LogDebug("Added new pack reader for: {PackName}", packName);
        }
    }

    private void MarkPacksAsObsolete(HashSet<string> validPackNames)
    {
        foreach (var key in _packReaders.Keys.ToArray())
        {
            if (!validPackNames.Contains(key))
            {
                if (_packReaders[key].IsValueCreated)
                {
                    _packReaders[key].Value.IsObsolete = true;
                    logger?.LogInformation("Marked pack reader as obsolete: {Key}", key);
                }
            }
        }
    }

    /// <summary>Disposes all pack readers that have been created.</summary>
    public void Dispose()
    {
        logger?.LogDebug("Disposing PackManager and all created pack readers.");
        if (_packReaders == null)
            return;

        foreach (var pack in _packReaders.Values)
        {
            if (pack.IsValueCreated)
            {
                pack.Value.Dispose();
                logger?.LogDebug("Disposed pack reader: {Pack}", pack.Value);
            }
        }
    }
}