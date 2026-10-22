using System.Text.RegularExpressions;

namespace PlayMode.Core.Engine;

/// <summary>
/// `tailscale up` refuses to run if the node has non-default settings that the command doesn't restate,
/// and prints the exact command to use instead. We parse that suggestion so a reconnect never silently fails.
/// </summary>
public static partial class TailscaleFlags
{
    [GeneratedRegex(@"^\s*tailscale up (?<flags>.+)$", RegexOptions.Multiline)]
    private static partial Regex SuggestedCommand();

    [GeneratedRegex(@"^--[a-z][a-z0-9-]*(=[^\s;&|`$<>""']+)?$")]
    private static partial Regex SafeFlag();

    /// <returns>The suggested flags, or null if there is no suggestion or any token looks unsafe.</returns>
    public static IReadOnlyList<string>? ParseSuggestedUp(string output)
    {
        var match = SuggestedCommand().Match(output);
        if (!match.Success)
            return null;
        var tokens = match.Groups["flags"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // All or nothing: never run a partially understood command.
        return tokens.Length > 0 && tokens.All(t => SafeFlag().IsMatch(t)) ? tokens : null;
    }
}
