using System.IO.Abstractions;
using GitDotNet.Readers;
using GitDotNet.Tools;

namespace GitDotNet;

internal delegate RepositoryInfo RepositoryInfoFactory(string path);

/// <summary>Provides high level information about this repository.</summary>
public class RepositoryInfo
{
    private readonly Lazy<string> _path;
    private readonly Lazy<ConfigReader> _config;
    private readonly IFileSystem _fileSystem;

    internal RepositoryInfo(string path, ConfigReaderFactory configReaderFactory, IFileSystem fileSystem)
    {
        _path = new Lazy<string>(() => GitCliCommand.GetAbsoluteGitPath(path) ?? throw new InvalidOperationException("Not a git repository."));
        _config = new(() => configReaderFactory(fileSystem.Path.Combine(Path, "config")));
        _fileSystem = fileSystem;
    }

    /// <summary>Gets the canonicalized absolute path of the repository.</summary>
    public virtual string Path => _path.Value;

    /// <summary>Gets the root path of the git repository containing files.</summary>
    public virtual string RootFilePath => Config.IsBare ?
        throw new InvalidOperationException("A bare repository does not contain files.") :
        (_fileSystem.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("No root path could be found."));

    /// <summary>Gets the <see cref="ConfigReader"/> instance associated with the repository.</summary>
    public ConfigReader Config => _config.Value;

    /// <summary>Gets the git normalized path of a file.</summary>
    /// <param name="fullPath">The full path of file or directory.</param>
    public GitPath GetRepositoryPath(string fullPath) =>
        new(fullPath.Replace('\\', '/').Replace(RootFilePath.Replace('\\', '/'), "").Trim('/'));
}
