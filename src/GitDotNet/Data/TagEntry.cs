using System.Text;
using GitDotNet.Tools;

namespace GitDotNet;

/// <summary>Represents a Git tag entry.</summary>
public record class TagEntry : Entry
{
    private readonly IObjectResolver _objectResolver;
    private readonly Lazy<Content> _content;
    private Entry? _target;

    internal TagEntry(HashId id, byte[] data, IObjectResolver objectResolver)
        : base(EntryType.Tag, id, data)
    {
        _objectResolver = objectResolver;
        _content = new(Parse);
    }

    /// <summary>Gets the type of the object that the tag points to.</summary>
    public EntryType TargetType => _content.Value.Type;

    /// <summary>Gets the tag name.</summary>
    public string Tag => _content.Value.Tag;

    /// <summary>Gets the tagger's signature.</summary>
    public Signature? Tagger => _content.Value.Tagger;

    /// <summary>Gets the tag message.</summary>
    public string Message => _content.Value.Message;

    /// <summary>Asynchronously gets the target object associated with the tag.</summary>
    /// <returns>The target object associated with the tag.</returns>
    public async Task<Entry> GetTargetAsync() =>
        _target ??= await _objectResolver.GetAsync<Entry>(_content.Value.Object.HexToByteArray());

    private Content Parse()
    {
        var index = 0;
        string? obj = null, type = null, tag = null, tagger = null;
        var message = new StringBuilder();

        while (index < Data.Length)
        {
            var lineEndIndex = Array.IndexOf(Data, (byte)'\n', index);
            if (lineEndIndex == -1) lineEndIndex = Data.Length;

            var line = Encoding.UTF8.GetString(Data, index, lineEndIndex - index);
            index = lineEndIndex + 1;

            if (line.StartsWith("object "))
            {
                obj = line[7..];
            }
            else if (line.StartsWith("type "))
            {
                type = line[5..];
            }
            else if (line.StartsWith("tag "))
            {
                tag = line[4..];
            }
            else if (line.StartsWith("tagger "))
            {
                tagger = line[7..];
            }
            else if (line.Length > 0 || message.Length > 0)
            {
                if (message.Length > 0)
                {
                    message.AppendLine();
                }
                message.Append(line);
            }
        }

        if (obj is null) throw new InvalidOperationException("Invalid tag entry: missing object.");
        if (type is null) throw new InvalidOperationException("Invalid tag entry: missing type.");
        if (tag is null) throw new InvalidOperationException("Invalid tag entry: missing tag.");

        return new Content(obj, ParseEntryType(type), tag, Signature.Parse(tagger), message.ToString());
    }

    private static EntryType ParseEntryType(string type) => type switch
    {
        "commit" => EntryType.Commit,
        "tree" => EntryType.Tree,
        "blob" => EntryType.Blob,
        "tag" => EntryType.Tag,
        _ => throw new InvalidOperationException($"Invalid tag entry type: {type}")
    };

    private record class Content(string Object, EntryType Type, string Tag, Signature? Tagger, string Message);
}
