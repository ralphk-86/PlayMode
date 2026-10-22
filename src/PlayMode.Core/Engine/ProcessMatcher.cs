using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Engine;

public sealed record MatchContext(int SelfPid, int SessionId, IReadOnlyCollection<string> NeverTouch);

public static class ProcessMatcher
{
    /// <summary>Processes Windows needs; never closed or suspended, whatever a rule says.</summary>
    public static readonly IReadOnlySet<string> BuiltInNeverTouch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "LsaIso", "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "ctfmon", "taskhostw",
        "audiodg", "conhost", "dllhost", "RuntimeBroker", "WmiPrvSE", "spoolsv", "MsMpEng", "NisSrv",
        "SecurityHealthService", "SecurityHealthSystray", "StartMenuExperienceHost", "ShellExperienceHost",
        "SearchHost", "TextInputHost", "LockApp", "PlayMode", "playmode",
    };

    static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive wildcard match (* and ?) of the whole text.</summary>
    public static bool Like(string text, string pattern) =>
        Patterns.GetOrAdd(pattern, p => new Regex(
            "^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(text);

    public static IReadOnlyList<string> PatternsFor(AppRule rule) => rule.Kind switch
    {
        AppKind.Wsl => ["vmmemWSL"],
        AppKind.Tailscale => [],
        _ => [.. rule.Processes.Select(StripExe)],
    };

    public static bool IsNeverTouch(string name, MatchContext ctx) =>
        BuiltInNeverTouch.Contains(name) || ctx.NeverTouch.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static List<ProcessInfo> Match(AppRule rule, IEnumerable<ProcessInfo> processes, MatchContext ctx)
    {
        var patterns = PatternsFor(rule);
        if (patterns.Count == 0)
            return [];
        return [.. processes.Where(p =>
            p.Id != ctx.SelfPid
            // Only our own session: service-owned copies (e.g. Everything's) are the elevated helper's job.
            // WSL's VM process lives in session 0.
            && (rule.Kind == AppKind.Wsl || p.SessionId == ctx.SessionId)
            && !IsNeverTouch(p.Name, ctx)
            && patterns.Any(pattern => Like(p.Name, pattern))
            && (rule.PathLike is null || (p.Path is not null && Like(p.Path, rule.PathLike))))];
    }

    static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
