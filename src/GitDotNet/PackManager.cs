using System.Collections.Concurrent;
using System.IO.Abstractions;
using GitDotNet.Readers;

namespace GitDotNet;

internal delegate IPackManager PackManagerFactory(string path);

/// <summary>Provides methods for managing Git pack files.</summary>
internal interface IPackManager : IDisposable
{
    IEnumerable<PackReader> PackReaders { get; }

    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    void UpdatePacksIfNeeded();
}

/// <summary>Provides methods for managing Git pack files.</summary>
internal class PackManager(string path, IFileSystem fileSystem, PackReaderFactory packReaderFactory) : IPackManager
{
    private readonly ConcurrentDictionary<string, Lazy<PackReader>> _packReaders = new(StringComparer.Ordinal);
    private DateTime? _lastInfoPacksTimestamp;

    public IEnumerable<PackReader> PackReaders
    {
        get
        {
            UpdatePacksIfNeeded();
            return from reader in _packReaders.Values
                   where !reader.Value.IsObsolete
                   select reader.Value;
        }
    }

    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    public void UpdatePacksIfNeeded()
    {
        var packDir = fileSystem.Path.Combine(path, "pack");
        var infoPacksPath = fileSystem.Path.Combine(path, "info", "packs");
        DateTime? currentTimestamp = fileSystem.File.Exists(infoPacksPath) ?
            fileSystem.File.GetLastWriteTimeUtc(infoPacksPath) :
            (fileSystem.Directory.Exists(packDir) ?
            fileSystem.Directory.GetFiles(packDir, "*.pack")
                .Select(file => fileSystem.File.GetLastWriteTimeUtc(file))
                .DefaultIfEmpty(DateTime.MinValue)
                .Max() is var maxTime && maxTime != DateTime.MinValue ? maxTime : null :
                null);
        
        if (_lastInfoPacksTimestamp != null && _lastInfoPacksTimestamp == currentTimestamp) 
            return;

        var validPackNames = fileSystem.File.Exists(infoPacksPath) ?
            AddFromInfoPacks(infoPacksPath, packDir) :
            fileSystem.Directory.Exists(packDir) ? AddFromPackDir(packDir) : [];

        MarkPacksAsObsolete(validPackNames);
        _lastInfoPacksTimestamp = currentTimestamp;
    }

    /// <summary>Disposes all pack readers that have been created.</summary>
    public void Dispose()
    {
        if (_packReaders == null) return;
        
        foreach (var pack in _packReaders.Values)
        {
            if (pack.IsValueCreated)
            {
                pack.Value.Dispose();
            }
        }
    }

    private HashSet<string> AddFromInfoPacks(string infoPacksPath, string packDir)
    {
        var validPackNames = new HashSet<string>();
        var lines = fileSystem.File.ReadAllLinesShared(infoPacksPath);
        foreach (var line in lines)
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "P" && parts[1].EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
            {
                var packFile = fileSystem.Path.Combine(packDir, parts[1]);
                var packName = fileSystem.Path.GetFileNameWithoutExtension(packFile);
                validPackNames.Add(packName);
                AddMissingPackReader(packName, packFile);
            }
        }
        return validPackNames;
    }

    private HashSet<string> AddFromPackDir(
        string packDir)
    {
        var validPackNames = new HashSet<string>();
        var packFiles = fileSystem.Directory.GetFiles(packDir, "*.pack");
        foreach (var packFile in packFiles)
        {
            var packName = fileSystem.Path.GetFileNameWithoutExtension(packFile);
            validPackNames.Add(packName);
            AddMissingPackReader(packName, packFile);
        }
        return validPackNames;
    }

    private void AddMissingPackReader(string packName, string packFile)
    {
        _packReaders.TryAdd(packName, new(() => packReaderFactory(packFile)));
    }

    private void MarkPacksAsObsolete(HashSet<string> validPackNames)
    {
        foreach (var key in _packReaders.Keys.ToArray())
        {
            if (!validPackNames.Contains(key))
            {
                if (_packReaders[key].IsValueCreated)
                    _packReaders[key].Value.IsObsolete = true;
            }
        }
    }
}