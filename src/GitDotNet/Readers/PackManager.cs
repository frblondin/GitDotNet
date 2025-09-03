using System;
using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Readers;

internal delegate IPackManager PackManagerFactory(string path);

/// <summary>Provides methods for managing Git pack files.</summary>
internal interface IPackManager : IDisposable
{
    IEnumerable<PackIndexReader> Indices { get; }

    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    /// <param name="force">Forces update even if no date has changed.</param>
    void UpdateIndices(bool force);
}

/// <summary>Provides methods for managing Git pack files.</summary>
internal class PackManager(string path, IFileSystem fileSystem, MultiPackIndexReaderFactory multiPackIndexReaderFactory, StandardPackIndexReaderFactory standardPackIndexReaderFactory, ILogger<PackManager>? logger = null) : IPackManager
{
    private readonly ConcurrentDictionary<string, PackIndexReader> _indices = new(StringComparer.Ordinal);
    private DateTime? _lastInfoPacksTimestamp;
    private PackIndexReader.MultiPack? _multiPackIndexReader;
    private bool _disposedValue;

    public IEnumerable<PackIndexReader> Indices
    {
        get
        {
            logger?.LogDebug("Accessing PackReaders property, updating packs if needed.");
            UpdateIndices(false);
            var result = _indices.Values.Where(i => !i.IsObsolete);
            return _multiPackIndexReader is not null ?
                result.Prepend(_multiPackIndexReader) :
                result;
        }
    }

    /// <summary>Updates pack readers based on the current state of pack files.</summary>
    public void UpdateIndices(bool force)
    {
        logger?.LogInformation("Updating pack indices. Force: {Force}", force);
        if (_lastInfoPacksTimestamp != null && !force)
        {
            logger?.LogDebug("Skipping pack update due to timestamp and force flag.");
            return;
        }
        var packDir = fileSystem.Path.Combine(path, "pack");
        var multiPackFile = fileSystem.Path.Combine(packDir, "multi-pack-index");
        RefreshMultiPackIndexReader(multiPackFile);

        var validPackNames = AddFromPackDir(packDir);
        MarkPacksAsObsolete(validPackNames);
        _lastInfoPacksTimestamp = DateTime.Now;
        logger?.LogDebug("Pack update complete. Valid packs: {ValidPacks}", string.Join(",", validPackNames));
    }

    private void RefreshMultiPackIndexReader(string multiPackFile)
    {
        if (_multiPackIndexReader?.HasBeenModified ?? false)
        {
            _multiPackIndexReader.IsObsolete = true;
            _multiPackIndexReader = null;
        }
        if (fileSystem.File.Exists(multiPackFile) && _multiPackIndexReader is null)
        {
            LoadMultiPackIndexReader(multiPackFile);
        }
    }

    private void LoadMultiPackIndexReader(string multiPackFile)
    {
        try
        {
            _multiPackIndexReader = multiPackIndexReaderFactory(multiPackFile);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error loading multi-pack index file: {MultiPackFile}", multiPackFile);
            _multiPackIndexReader = null;
        }
    }

    private HashSet<string> AddFromPackDir(string packDir)
    {
        logger?.LogDebug("Scanning pack directory: {PackDir}", packDir);
        var validIndices = new HashSet<string>();
        var indices = fileSystem.Directory.Exists(packDir) ? fileSystem.Directory.GetFiles(packDir, "*.idx") : [];
        foreach (var index in indices.Where(IsNotAlreadyIncludedInMultiPackIndex))
        {
            validIndices.Add(index);
            AddMissingIndexReader(index);
            logger?.LogDebug("Pack index file found: {Index}", index);
        }
        return validIndices;
    }

    private bool IsNotAlreadyIncludedInMultiPackIndex(string index)
    {
        var name = fileSystem.Path.GetFileNameWithoutExtension(index);
        if (_multiPackIndexReader?.PackReaders.Any(r => fileSystem.Path.GetFileNameWithoutExtension(r.Path).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false)
        {
            logger?.LogDebug("Pack index file {Index} is already included in multi-pack-index, skipping.", name);
            return false;
        }
        return true;
    }

    private void AddMissingIndexReader(string index)
    {
        _indices.GetOrAdd(index, i => standardPackIndexReaderFactory(i));
    }

    private void MarkPacksAsObsolete(HashSet<string> validPackNames)
    {
        foreach (var key in _indices.Keys.ToArray())
        {
            if (validPackNames.Contains(key))
            {
                continue;
            }
            _indices[key].IsObsolete = true;
            logger?.LogInformation("Marked index pack reader as obsolete: {Key}", key);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            logger?.LogDebug("Disposing PackManager and all created index pack readers.");
            _multiPackIndexReader?.Dispose();
            foreach (var index in _indices?.Values ?? [])
            {
                index.Dispose();
                logger?.LogDebug("Disposed index pack reader: {Index}", index);
            }
            _disposedValue = true;
        }
    }

    ~PackManager()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}