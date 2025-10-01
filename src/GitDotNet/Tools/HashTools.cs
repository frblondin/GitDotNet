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
    /// <exception cref="ArgumentNullException">Thrown when hex is null.</exception>
    /// <exception cref="FormatException">Thrown when the hexadecimal string format is invalid.</exception>
    /// <exception cref="ArgumentException">Thrown when the length of the hexadecimal string is invalid.</exception>
    [ExcludeFromCodeCoverage]
    public static byte[] HexToByteArray(this string hex)
    {
        if (hex == null)
            throw new ArgumentNullException(nameof(hex));
        
        if (string.IsNullOrWhiteSpace(hex))
            throw new FormatException("Hexadecimal string cannot be empty or whitespace.");
        
        if (hex.Length % 2 != 0)
            throw new ArgumentException("Invalid length of the hexadecimal string.");
            
        // Git allows partial hashes with minimum 4 characters (2 bytes)
        if (hex.Length < 4)
            throw new ArgumentException("Hash must be at least 4 characters long.");

        try
        {
            return Enumerable.Range(0, hex.Length / 2).Select(x => Convert.ToByte(hex.Substring(x * 2, 2), 16)).ToArray();
        }
        catch (FormatException)
        {
            throw new FormatException($"Invalid hexadecimal format: {hex}");
        }
        catch (OverflowException)
        {
            throw new FormatException($"Invalid hexadecimal format: {hex}");
        }
    }
}
