using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GitDotNet.Indexing.Realm;

[Table("Commits")]
internal class CommitContent
{
    [Key]
    public required HashId Id { get; set; }

    public required Dictionary<string, HashId> Blobs { get; set; }
}
