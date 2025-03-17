using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;

namespace GitDotNet.Readers;

internal delegate ConfigReader ConfigReaderFactory(string path);

/// <summary>Represents a reader for the Git configuration file (.git/config).</summary>
public class ConfigReader
{
    private readonly IFileSystem _fileSystem;
    private readonly IImmutableDictionary<string, IImmutableDictionary<string, string>> _sections;

    internal ConfigReader(string path, IFileSystem fileSystem)
    {
        Path = path;
        _fileSystem = fileSystem;
        _sections = LoadConfig();
    }

    /// <summary>Gets the path to the Git configuration file.</summary>
    public string Path { get; }

    protected virtual ImmutableDictionary<string, IImmutableDictionary<string, string>> LoadConfig()
    {
        if (!_fileSystem.File.Exists(Path))
        {
            throw new FileNotFoundException($"Config file not found: {Path}");
        }

        var lines = _fileSystem.File.ReadAllLines(Path);
        var sectionsBuilder = ImmutableDictionary.CreateBuilder<string, IImmutableDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        var currentSectionBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith(";") || trimmedLine.StartsWith("#"))
            {
                continue; // Skip empty lines and comments
            }

            if (trimmedLine.StartsWith('[') && trimmedLine.EndsWith(']'))
            {
                CreateNewSection(sectionsBuilder, ref currentSection, ref currentSectionBuilder, trimmedLine);
            }
            else if (currentSection != null)
            {
                AddToSection(currentSectionBuilder, trimmedLine);
            }
        }

        if (currentSection != null)
        {
            sectionsBuilder[currentSection] = currentSectionBuilder.ToImmutable();
        }

        return sectionsBuilder.ToImmutable();
    }

    private static void CreateNewSection(ImmutableDictionary<string, IImmutableDictionary<string, string>>.Builder sectionsBuilder, ref string? currentSection, ref ImmutableDictionary<string, string>.Builder currentSectionBuilder, string trimmedLine)
    {
        if (currentSection != null)
        {
            sectionsBuilder[currentSection] = currentSectionBuilder.ToImmutable();
        }

        currentSection = trimmedLine[1..^1].Trim();
        currentSectionBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddToSection(ImmutableDictionary<string, string>.Builder currentSectionBuilder, string trimmedLine)
    {
        var separatorIndex = trimmedLine.IndexOf('=');
        if (separatorIndex > 0)
        {
            var key = trimmedLine[..separatorIndex].Trim();
            var value = trimmedLine[(separatorIndex + 1)..].Trim();
            currentSectionBuilder[key] = value;
        }
    }

    /// <summary>Gets the value of a property in a specified section.</summary>
    /// <param name="section">The section name.</param>
    /// <param name="property">The property name.</param>
    /// <param name="throwIfNull">Indicates whether to throw an exception if the property is not found.</param>
    /// <returns>The property value, or null if not found and throwIfNull is false.</returns>
    public string? GetProperty(string section, string property, bool throwIfNull = true)
    {
        if (_sections.TryGetValue(section, out var properties) && properties.TryGetValue(property, out var value))
        {
            return value;
        }
        if (throwIfNull)
        {
            throw new KeyNotFoundException($"Property '{property}' in section '{section}' not found.");
        }
        return null;
    }

    /// <summary>Internal variable identifying the repository format and layout version. See
    /// <see href="https://git-scm.com/docs/gitrepository-layout"/>.</summary>
    [ExcludeFromCodeCoverage]
    public virtual string? RepositoryFormatVersion => GetProperty("core", "repositoryformatversion");

    /// <summary>
    /// If true this repository is assumed to be bare and has no working directory associated with it.
    /// If this is the case a number of commands that require a working directory will be disabled, such as
    /// <see href="https://git-scm.com/docs/git-add"/> or <see href="https://git-scm.com/docs/git-merge"/>.
    /// </summary>
    /// <remarks>
    /// This setting is automatically guessed by <see href="https://git-scm.com/docs/git-clone"/> or
    /// <see href="https://git-scm.com/docs/git-init"/> when the repository was created.By default a
    /// repository that ends in "/.git" is assumed to be not bare (bare = false), while all other repositories are assumed to be bare(bare = true).
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public virtual bool IsBare => GetProperty("core", "bare")?.Equals("true") ?? false;

    /// <summary>
    /// Internal variable which enables various workarounds to enable Git to work better on filesystems that are
    /// not case sensitive, like APFS, HFS+, FAT, NTFS, etc. For example, if a directory listing finds "makefile"
    /// when Git expects "Makefile", Git will assume it is really the same file, and continue to remember it as "Makefile".
    /// </summary>
    /// <remarks>
    /// The default is false, except <see href="https://git-scm.com/docs/git-clone"/> or
    /// <see href="https://git-scm.com/docs/git-init"/> will probe and set core.ignoreCase true if appropriate when the repository is created.
    /// Git relies on the proper configuration of this variable for your operating and file system.Modifying this value may result in unexpected behavior.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public virtual bool IgnoreCase => GetProperty("core", "ignorecase")?.Equals("true") ?? false;

    /// <summary>Gets the user name from the user section.</summary>
    [ExcludeFromCodeCoverage]
    public virtual string UserName => GetProperty("user", "name")!;

    /// <summary>Gets the user email from the user section.</summary>
    [ExcludeFromCodeCoverage]
    public virtual string UserEmail => GetProperty("user", "email")!;
    /// <summary>
    /// Setting this variable to "true" is the same as setting the text attribute to "auto" on all files and core.eol to "crlf".
    /// Set to true if you want to have CRLF line endings in your working directory and the repository has LF line endings.
    /// This variable can be set to input, in which case no output conversion is performed.
    /// </summary>
    /// <remarks>
    /// See <see href="https://git-scm.com/docs/git-config#Documentation/git-config.txt-coreautocrlf"/>.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public virtual string? UseAutoCrlf => GetProperty("core", "autocrlf", throwIfNull: false);

    /// <summary>
    /// If true, then git will read the commit-graph file (if it exists) to parse the graph structure of commits.
    /// Defaults to true.
    /// </summary>
    /// <remarks>
    /// See <see href="https://git-scm.com/docs/git-commit-graph"/>.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public virtual bool UseCommitGraph => GetProperty("core", "commitGraph", throwIfNull: false)?.Equals("true") ?? true;

    /// <summary>Gets all properties of a specified section.</summary>
    /// <param name="section">The section name.</param>
    /// <param name="throwIfNull">Indicates whether to throw an exception if the section is not found.</param>
    /// <returns>A dictionary of properties, or null if the section is not found and throwIfNull is false.</returns>
    [ExcludeFromCodeCoverage]
    public IImmutableDictionary<string, string>? GetSection(string section, bool throwIfNull = true)
    {
        if (_sections.TryGetValue(section, out var properties))
        {
            return properties;
        }
        if (throwIfNull)
        {
            throw new KeyNotFoundException($"Section '{section}' not found.");
        }
        return null;
    }

    /// <summary>Gets all properties of a named section (e.g., remote, branch).</summary>
    /// <param name="section">The section name.</param>
    /// <param name="name">The name of the section.</param>
    /// <param name="throwIfNull">Indicates whether to throw an exception if the section is not found.</param>
    /// <returns>A dictionary of properties, or null if the section is not found and throwIfNull is false.</returns>
    [ExcludeFromCodeCoverage]
    public IImmutableDictionary<string, string>? GetNamedSection(string section, string name, bool throwIfNull = true)
    {
        var fullSectionName = $"{section} \"{name}\"";
        return GetSection(fullSectionName, throwIfNull);
    }

    /// <summary>Gets all named sections of a specified section (e.g., remote, branch).</summary>
    [ExcludeFromCodeCoverage]
    public IEnumerable<string> GetNamedSections(string section) =>
        from k in _sections.Keys
        where k.StartsWith(section + " \"")
        select k.Substring(section.Length + 2, k.Length - section.Length - 3);

    [ExcludeFromCodeCoverage]
    internal Signature CreateSignature() => new(UserName, UserEmail, DateTimeOffset.Now);
}

