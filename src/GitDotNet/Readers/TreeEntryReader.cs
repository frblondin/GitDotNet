using System.Collections.Immutable;
using System.Text;

namespace GitDotNet.Readers;
internal static class TreeEntryReader
{
    internal static IList<TreeEntryItem> Parse(byte[] data, IObjectResolver objectResolver)
    {
        var items = ImmutableList.CreateBuilder<TreeEntryItem>();
        int index = 0;

        while (index < data.Length)
        {
            // Read the file mode, before space character
            var modeStart = index;
            index = Array.IndexOf(data, (byte)0x20, index);
            var mode = Encoding.ASCII.GetString(data, modeStart, index - modeStart);
            index++; // Skip the space character

            // Read the file name, before null terminator
            var nameStart = index;
            index = Array.IndexOf(data, (byte)0x00, index);
            var name = Encoding.UTF8.GetString(data, nameStart, index - nameStart);
            index++; // Skip the null terminator

            // Read the SHA-1 hash
            var hash = data.AsSpan(index, 20).ToArray();
            index += 20;

            var item = new TreeEntryItem(new FileMode(mode), name, hash,
                async () => await objectResolver.GetAsync<Entry>(hash));
            items.Add(item);
        }

        return items.ToImmutable();
    }
}
