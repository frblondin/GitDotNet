using System.ComponentModel.DataAnnotations.Schema;

namespace GitDotNet.Indexing.LiteDb.Data;

[Table("IndexedBlobs")]
internal class IndexedBlob
{
    public required HashId Id { get; set; }
}
