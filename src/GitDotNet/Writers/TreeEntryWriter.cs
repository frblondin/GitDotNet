using System.Text;

namespace GitDotNet.Writers;
internal static class TreeEntryWriter
{
    internal static byte[] Write(IEnumerable<TreeEntryItem> items)
    {
        using var stream = new MemoryStream();

        foreach (var item in items)
        {
            // Write mode (octal string without leading zeros)
            var modeBytes = Encoding.ASCII.GetBytes(item.Mode.ToString());
            stream.Write(modeBytes);

            // Write space separator
            stream.WriteByte(0x20);

            // Write filename
            var nameBytes = Encoding.UTF8.GetBytes(item.Name);
            stream.Write(nameBytes);

            // Write null terminator
            stream.WriteByte(0x00);

            // Write 20-byte SHA-1 hash
            var hashBytes = item.Id.Hash.ToArray();
            stream.Write(hashBytes);
        }

        return stream.ToArray();
    }
}
