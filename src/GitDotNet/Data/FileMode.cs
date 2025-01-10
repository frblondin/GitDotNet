namespace GitDotNet;

/// <summary>Represents the file mode of a Git tree entry item.</summary>
public record class FileMode(int Mode)
{
    /// <summary>Initializes a new instance of the <see cref="FileMode"/> class with the specified mode.</summary>
    /// <param name="mode">The file mode as an integer.</param>
    public FileMode(string mode) : this(Convert.ToInt32(mode, 8)) { }

    /// <summary>Gets the type of the object based on the file mode.</summary>
    public ObjectType Type => (ObjectType)Mode;

    /// <summary>Gets the entry type of the object based on the file mode.</summary>
    public EntryType EntryType => Type switch
    {
        ObjectType.RegularFile => EntryType.Blob,
        ObjectType.ExecutableBlob => EntryType.Blob,
        ObjectType.Symlink => EntryType.Blob,
        ObjectType.Tree => EntryType.Tree,
        ObjectType.Gitlink => EntryType.Tree,
        _ => throw new InvalidOperationException("Invalid object type.")
    };

    /// <inheritdoc/>
    public override string ToString() => Convert.ToString(Mode, 8);
}

/// <summary>Specifies the type of a Git tree entry item.</summary>
public enum ObjectType
{
    /// <summary>Regular file (blob).</summary>
    RegularFile = 0x81A4, // 100644 in octal

    /// <summary>Executable file (blob).</summary>
    ExecutableBlob = 0x81ED, // 100755 in octal

    /// <summary>Symbolic link (blob).</summary>
    Symlink = 0xA000, // 120000 in octal

    /// <summary>Directory (tree).</summary>
    Tree = 0x4000, // 040000 in octal

    /// <summary>Gitlink (submodule).</summary>
    Gitlink = 0xE000 // 160000 in octal
}
