using System.IO.Abstractions;
using GitDotNet.Readers;
using GitDotNet.Tools;

namespace GitDotNet;

internal delegate RepositoryInfo RepositoryInfoFactory(string path);

/// <summary>Provides high level information about this repository.</summary>
public class RepositoryInfo : IRepositoryInfo
{
    private readonly Lazy<string> _path;
    private readonly Lazy<ConfigReader> _config;
    private readonly Lazy<CurrentOperationReader> _operationReader;
    private readonly IFileSystem _fileSystem;

    internal RepositoryInfo(string path, ConfigReaderFactory configReaderFactory, CurrentOperationReaderFactory operationReaderFactory, IFileSystem fileSystem, GitCliCommand cliCommand)
    {
        _path = new Lazy<string>(() => cliCommand.GetAbsoluteGitPath(path) ?? throw new InvalidOperationException("Not a git repository."));
        _config = new(() => configReaderFactory(fileSystem.Path.Combine(Path, "config")));
        _operationReader = new(() => operationReaderFactory(this));
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    public virtual string Path => _path.Value;

    /// <inheritdoc/>
    public virtual string RootFilePath => Config.IsBare ?
        throw new InvalidOperationException("A bare repository does not contain files.") :
        (_fileSystem.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("No root path could be found."));

    /// <inheritdoc/>
    public ConfigReader Config => _config.Value;

    /// <inheritdoc/>
    public CurrentOperation CurrentOperation => _operationReader.Value.Read();

    /// <summary>Gets the git normalized path of a file.</summary>
    /// <param name="fullPath">The full path of file or directory.</param>
    public GitPath GetRepositoryPath(string fullPath) =>
        new(fullPath.Replace('\\', '/').Replace(RootFilePath.Replace('\\', '/'), "").Trim('/'));
}

/// <summary>
/// Defines an interface for repository information, providing properties for the repository's path and root file path.
/// </summary>
public interface IRepositoryInfo
{
    /// <summary>Gets the canonicalized absolute path of the repository.</summary>
    string Path { get; }

    /// <summary>Gets the root path of the git repository containing files.</summary>
    string RootFilePath { get; }

    /// <summary>Gets the <see cref="ConfigReader"/> instance associated with the repository.</summary>
    ConfigReader Config { get; }

    /// <summary>Gets the current operation being performed.</summary>
    CurrentOperation CurrentOperation { get; }
}