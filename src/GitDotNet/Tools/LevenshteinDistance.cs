using System.Text;

namespace GitDotNet.Tools;

internal static class LevenshteinDistance
{
    internal static float ComputeSimilarity(byte[] a, byte[] b, float similarityThreshold = 1.0f)
    {
        var maxLen = Math.Max(GetCharCount(a), GetCharCount(b));
        if (maxLen == 0) return 1.0f;
        return 1.0f - (float)ComputeDistance(a, b, similarityThreshold) / maxLen;
    }

    internal static int ComputeDistance(byte[] a, byte[] b, float similarityThreshold = 1.0f)
    {
        if (a == null || a.Length == 0) return b == null || b.Length == 0 ? 0 : GetCharCount(b);
        if (b == null || b.Length == 0) return GetCharCount(a);

        var aLength = GetCharCount(a);
        var bLength = GetCharCount(b);
        var matrix = new int[aLength + 1, bLength + 1];

        for (var i = 0; i <= aLength; i++) matrix[i, 0] = i;
        for (var j = 0; j <= bLength; j++) matrix[0, j] = j;

        int aByteIndex = 0;
        for (var i = 1; i <= aLength; i++)
        {
            var aChar = GetCharAt(a, ref aByteIndex);
            int bByteIndex = 0;
            for (var j = 1; j <= bLength; j++)
            {
                var bChar = GetCharAt(b, ref bByteIndex);
                var cost = aChar == bChar ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[aLength, bLength];
    }

    private static int GetCharCount(byte[] bytes) => Encoding.UTF8.GetCharCount(bytes);

    private static char GetCharAt(byte[] bytes, ref int byteIndex)
    {
        var byteCount = bytes[byteIndex] switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            _ => 4
        };
        var result = new char[byteCount];
        Encoding.UTF8.GetChars(bytes, byteIndex, byteCount, result, 0);
        byteIndex += byteCount;
        return result[0];
    }
}
