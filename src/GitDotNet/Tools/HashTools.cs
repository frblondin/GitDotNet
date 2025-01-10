using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace GitDotNet.Tools;

/// <summary>Provides utility methods for converting between hexadecimal strings and byte arrays.</summary>
public static partial class HashTools
{
    /// <summary>Converts a hexadecimal string to a byte array.</summary>
    /// <param name="hex">The hexadecimal string to convert.</param>
    /// <returns>A byte array representing the hexadecimal string.</returns>
    /// <exception cref="ArgumentException">Thrown when the length of the hexadecimal string is invalid.</exception>
    [ExcludeFromCodeCoverage]
    public static byte[] HexToByteArray(this string hex)
    {
        if (hex.Length % 2 != 0)
            throw new ArgumentException("Invalid length of the hexadecimal string.");

        return Enumerable.Range(0, hex.Length / 2).Select(x => Convert.ToByte(hex.Substring(x * 2, 2), 16)).ToArray();
    }
}
