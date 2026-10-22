using System.Text.RegularExpressions;

namespace PlayMode.Core.Discovery;

public static partial class CommandLine
{
    /// <summary>
    /// Splits a startup command into the program and its arguments. Handles quoted paths, unquoted paths with
    /// spaces (<c>C:\Program Files\X\x.exe -flag</c>, which Windows tolerates) and launcher stubs.
    /// </summary>
    public static (string? Exe, string? Args, string? Launches) Split(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
            return (null, null, null);

        string exe, rest;
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            exe = end < 0 ? command[1..] : command[1..end];
            rest = end < 0 ? "" : command[(end + 1)..];
        }
        else if (ExeEnd().Match(command) is { Success: true } m)
        {
            exe = command[..m.Length];
            rest = command[m.Length..];
        }
        else
        {
            var space = command.IndexOf(' ', StringComparison.Ordinal);
            exe = space < 0 ? command : command[..space];
            rest = space < 0 ? "" : command[space..];
        }

        rest = rest.Trim();
        var launches = ProcessStart().Match(rest) is { Success: true } p ? p.Groups[1].Value : null;
        return (exe.Trim(), rest.Length == 0 ? null : rest, launches);
    }

    // Shortest prefix ending in .exe followed by a space or the end.
    [GeneratedRegex(@"^.*?\.exe(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExeEnd();

    // Squirrel installers (Discord, Postman, GitHub Desktop...) start the real app through Update.exe.
    [GeneratedRegex(@"--processStart\s+""?([^""\s]+?)(?:\.exe)?""?(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStart();
}

/// <summary>Turns machine paths into ones that work for any user, and back.</summary>
public static class EnvPaths
{
    static readonly string[] Variables = ["LOCALAPPDATA", "APPDATA", "USERPROFILE", "ProgramFiles(x86)", "ProgramFiles", "ProgramData", "SystemRoot"];

    public static IReadOnlyList<(string Variable, string Value)> Current { get; } =
        [.. Variables
            .Select(v => (v, Environment.GetEnvironmentVariable(v)))
            .Where(p => !string.IsNullOrEmpty(p.Item2))
            .Select(p => (p.v, p.Item2!.TrimEnd('\\')))];

    /// <summary>C:\Users\me\AppData\Local\X → %LOCALAPPDATA%\X (the most specific variable wins).</summary>
    public static string Collapse(string path, IReadOnlyList<(string Variable, string Value)>? env = null)
    {
        foreach (var (variable, value) in (env ?? Current).OrderByDescending(e => e.Value.Length))
        {
            if (path.Length > value.Length && path.StartsWith(value, StringComparison.OrdinalIgnoreCase) && path[value.Length] == '\\')
                return $"%{variable}%{path[value.Length..]}";
        }
        return path;
    }
}
