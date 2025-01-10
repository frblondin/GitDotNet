using System.Reflection;
using static System.Console;

namespace GitDotNet.Console.Tools;

internal static class InfoInput
{
    public const string RepositoryPathInput = "Path of repository";

    private static readonly string _filePath;
    private static readonly Dictionary<string, string> _inputHistory;

    static InfoInput()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        _filePath = Path.ChangeExtension(assemblyPath, ".last");
        _inputHistory = LoadInputHistory();
    }

    public static string InputData(string prompt, Predicate<string>? validate = null, string @default = "")
    {
        if (_inputHistory.TryGetValue(prompt, out var savedValue))
        {
            @default = savedValue;
        }

        while (true)
        {
            var invite = string.IsNullOrEmpty(@default) ? $"{prompt}: " : $"{prompt} ({@default}): ";
            var result = System.ReadLine.Read(invite, @default);
            if (validate?.Invoke(result) ?? true)
            {
                _inputHistory[prompt] = result;
                SaveInputHistory();
                return result;
            }
            else
            {
                WriteLine($"Invalid. Please try again.");
            }
        }
    }

    private static Dictionary<string, string> LoadInputHistory()
    {
        var history = new Dictionary<string, string>();
        if (File.Exists(_filePath))
        {
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var parts = line.Split(['='], 2);
                if (parts.Length == 2)
                {
                    history[parts[0]] = parts[1];
                }
            }
        }
        return history;
    }

    private static void SaveInputHistory()
    {
        var lines = new List<string>();
        foreach (var kvp in _inputHistory)
        {
            lines.Add($"{kvp.Key}={kvp.Value}");
        }
        File.WriteAllLines(_filePath, lines);
    }
}
