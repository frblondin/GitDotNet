using System.Collections.Immutable;
using System.IO.Abstractions;
using GitDotNet.Readers;

namespace GitDotNet;

/// <summary>Provides methods for managing Git pack files.</summary>
internal interface IPackManager
{
    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    /// <param name="objectsPath">The path to the objects directory.</param>
    /// <param name="currentPackReaders">The current pack readers dictionary.</param>
    /// <param name="lastInfoPacksTimestamp">The last timestamp of the info/packs file.</param>
    /// <param name="packReaderFactory">Factory to create pack readers.</param>
    /// <returns>A tuple containing the updated pack readers and the new timestamp.</returns>
    (ImmutableDictionary<string, Lazy<PackReader>> PackReaders, DateTime? Timestamp) UpdatePacksIfNeeded(
        string objectsPath,
        ImmutableDictionary<string, Lazy<PackReader>> currentPackReaders,
        DateTime? lastInfoPacksTimestamp,
        PackReaderFactory packReaderFactory);

    /// <summary>Disposes all pack readers that have been created.</summary>
    /// <param name="packReaders">The pack readers to dispose.</param>
    void DisposePacks(ImmutableDictionary<string, Lazy<PackReader>>? packReaders);
}

/// <summary>Provides methods for managing Git pack files.</summary>
internal class PackManager(IFileSystem fileSystem) : IPackManager
{
    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    /// <param name="objectsPath">The path to the objects directory.</param>
    /// <param name="currentPackReaders">The current pack readers dictionary.</param>
    /// <param name="lastInfoPacksTimestamp">The last timestamp of the info/packs file.</param>
    /// <param name="packReaderFactory">Factory to create pack readers.</param>
    /// <returns>A tuple containing the updated pack readers and the new timestamp.</returns>
    public (ImmutableDictionary<string, Lazy<PackReader>> PackReaders, DateTime? Timestamp) UpdatePacksIfNeeded(
        string objectsPath,
        ImmutableDictionary<string, Lazy<PackReader>> currentPackReaders,
        DateTime? lastInfoPacksTimestamp,
        PackReaderFactory packReaderFactory)
    {
        var packDir = fileSystem.Path.Combine(objectsPath, "pack");
        var infoPacksPath = fileSystem.Path.Combine(objectsPath, "info", "packs");
        DateTime? currentTimestamp = fileSystem.File.Exists(infoPacksPath) ? fileSystem.File.GetLastWriteTimeUtc(infoPacksPath) : null;
        
        if (lastInfoPacksTimestamp != null && lastInfoPacksTimestamp == currentTimestamp) 
            return (currentPackReaders, lastInfoPacksTimestamp);

        var newPackReaders = currentPackReaders.ToBuilder();
        var validPackNames = fileSystem.File.Exists(infoPacksPath) ?
            GetValidPackNamesFromInfoPacks(infoPacksPath, packDir, newPackReaders, packReaderFactory) :
            fileSystem.Directory.Exists(packDir) ? GetValidPackNamesFromPackDir(packDir, newPackReaders, packReaderFactory) : [];

        RemoveObsoletePackReaders(newPackReaders, validPackNames);
        return (newPackReaders.ToImmutable(), currentTimestamp);
    }

    /// <summary>Disposes all pack readers that have been created.</summary>
    /// <param name="packReaders">The pack readers to dispose.</param>
    public void DisposePacks(ImmutableDictionary<string, Lazy<PackReader>>? packReaders)
    {
        if (packReaders == null) return;
        
        foreach (var pack in packReaders.Values)
        {
            if (pack.IsValueCreated) pack.Value.Dispose();
        }
    }

    private HashSet<string> GetValidPackNamesFromInfoPacks(
        string infoPacksPath, 
        string packDir, 
        IDictionary<string, Lazy<PackReader>> packReaders,
        PackReaderFactory packReaderFactory)
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
                AddMissingPackReader(packReaders, packName, packFile, packReaderFactory);
            }
        }
        return validPackNames;
    }

    private HashSet<string> GetValidPackNamesFromPackDir(
        string packDir, 
        IDictionary<string, Lazy<PackReader>> packReaders,
        PackReaderFactory packReaderFactory)
    {
        var validPackNames = new HashSet<string>();
        var packFiles = fileSystem.Directory.GetFiles(packDir, "*.pack");
        foreach (var packFile in packFiles)
        {
            var packName = fileSystem.Path.GetFileNameWithoutExtension(packFile);
            validPackNames.Add(packName);
            AddMissingPackReader(packReaders, packName, packFile, packReaderFactory);
        }
        return validPackNames;
    }

    private static void AddMissingPackReader(
        IDictionary<string, Lazy<PackReader>> packReaders, 
        string packName, 
        string packFile,
        PackReaderFactory packReaderFactory)
    {
        if (!packReaders.ContainsKey(packName))
        {
            packReaders[packName] = new(() => packReaderFactory(packFile));
        }
    }

    private static void RemoveObsoletePackReaders(IDictionary<string, Lazy<PackReader>> packReaders, HashSet<string> validPackNames)
    {
        foreach (var key in packReaders.Keys.ToArray())
        {
            if (!validPackNames.Contains(key))
            {
                if (packReaders[key].IsValueCreated)
                    packReaders[key].Value.Dispose();
                packReaders.Remove(key);
            }
        }
    }
}