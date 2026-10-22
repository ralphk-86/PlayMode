using System.Text.RegularExpressions;
using PlayMode.Core.Engine;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Discovery;

/// <summary>Facts about live processes that only the OS can answer.</summary>
public interface IProcessDetails
{
    bool HasWindow(int pid);

    /// <summary>The exe's file description, e.g. "Visual Studio Code" for Code.exe.</summary>
    string? Describe(string path);

    string? AppUserModelId(int pid);
}

/// <summary>Turns a process list into apps: Code.exe ×12 becomes one "Visual Studio Code".</summary>
public static partial class AppGrouper
{
    /// <param name="roots">
    /// Folders that hold many unrelated apps (Program Files, AppData\Local, ~\.local\bin...). An app's folder is
    /// the first folder below its root, plus any deeper non-version folders; processes in the same or nested app
    /// folders are one app.
    /// </param>
    public static IReadOnlyList<RunningApp> Group(
        IEnumerable<ProcessInfo> processes,
        IReadOnlyCollection<string> roots,
        string windowsDir,
        Catalog catalog,
        MatchContext ctx,
        IProcessDetails details,
        IReadOnlyList<StartupEntry> startup)
    {
        var candidates = processes.Where(p =>
                p.Id != ctx.SelfPid
                && p.SessionId == ctx.SessionId
                && p.Path is not null
                && !ProcessMatcher.IsNeverTouch(p.Name, ctx)
                && !catalog.Ignore.Processes.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                && !catalog.Ignore.Paths.Any(like => ProcessMatcher.Like(p.Path, like))
                // Windows' own programs only when the catalog knows them (e.g. AMD's tools live in System32).
                && (!IsUnder(p.Path, windowsDir) || catalog.Apps.Any(a => ProcessMatcher.Match(a.Rule, [p], ctx).Count > 0)))
            .ToList();

        var groups = new List<(string Key, string? Folder, string FolderLike, List<ProcessInfo> Procs)>();
        foreach (var (proc, folder, like) in candidates
            .Select(p => (p, Folder(p.Path!, roots, windowsDir)))
            .Select(x => (x.p, x.Item2.Folder, x.Item2.Like))
            .OrderBy(x => x.Folder?.Length ?? int.MaxValue))
        {
            var group = folder is null
                ? groups.FirstOrDefault(g => g.Folder is null && string.Equals(g.Key, proc.Path, StringComparison.OrdinalIgnoreCase))
                : groups.FirstOrDefault(g => g.Folder is not null && IsUnderOrSame(folder, g.Folder));
            if (group.Procs is null)
                groups.Add((folder ?? proc.Path!, folder, like, [proc]));
            else
                group.Procs.Add(proc);
        }

        var apps = new List<RunningApp>();
        foreach (var (_, folder, like, procs) in groups)
        {
            var entry = startup.FirstOrDefault(s => BelongsTo(s, folder, procs, roots, windowsDir));
            var main = procs
                .OrderByDescending(p => entry is not null && (Same(p.Path, entry.Exe) || Same(p.Name, entry.Launches)))
                .ThenByDescending(p => details.HasWindow(p.Id))
                .ThenBy(p => p.Path!.Count(c => c == '\\'))
                .ThenByDescending(p => procs.Where(q => Same(q.Name, p.Name)).Sum(q => q.WorkingSet))
                .First();
            apps.Add(new RunningApp
            {
                Title = Nice(details.Describe(main.Path!)) ?? main.Name,
                Processes = [main, .. procs.Where(p => p != main).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)],
                Folder = folder,
                FolderLike = like,
                HasWindow = procs.Any(p => details.HasWindow(p.Id)),
                AppUserModelId = details.AppUserModelId(main.Id),
                Startup = entry,
            });
        }
        return [.. apps.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The app folder for an exe, and a portable wildcard for it.</summary>
    internal static (string? Folder, string Like) Folder(string exePath, IReadOnlyCollection<string> roots, string windowsDir)
    {
        var dir = Path.GetDirectoryName(exePath) ?? exePath;
        var root = roots.Where(r => IsUnderOrSame(dir, r)).OrderByDescending(r => r.Length).FirstOrDefault()
            ?? Path.GetPathRoot(dir) ?? dir;
        var below = dir.Length > root.Length ? dir[root.Length..].Trim('\\').Split('\\') : [];

        if (below.Length == 0 || IsUnder(exePath, windowsDir))
        {
            // A lone exe among unrelated ones (~\.local\bin\claude.exe): it's its own app.
            var tail = dir.TrimEnd('\\').Split('\\').TakeLast(2);
            return (null, $@"*\{string.Join('\\', tail)}\*");
        }

        var kept = new List<string> { below[0] };
        kept.AddRange(below.Skip(1).TakeWhile(s => !IsVersion(s)));
        return (Path.Combine([root, .. kept]), $@"*\{string.Join('\\', kept)}\*");
    }

    static bool BelongsTo(StartupEntry entry, string? folder, List<ProcessInfo> procs, IReadOnlyCollection<string> roots, string windowsDir)
    {
        if (entry.Exe is null || !Path.IsPathRooted(entry.Exe))
            return false;
        if (procs.Any(p => Same(p.Path, entry.Exe)))
            return true;
        if (entry.Launches is not null && procs.Any(p => Same(p.Name, entry.Launches)))
            return true;
        // Launchers in a parent folder: Discord\Update.exe for Discord\app-1.0\Discord.exe.
        var entryFolder = Folder(entry.Exe, roots, windowsDir).Folder;
        return folder is not null && entryFolder is not null && (IsUnderOrSame(folder, entryFolder) || IsUnderOrSame(entryFolder, folder))
            && procs.Any(p => Same(p.Name, entry.ProcessName));
    }

    // "app-1.0.9187", "132.0.0.0", "v4.2.1", "version-11f155f3450f4c8e"
    [GeneratedRegex(@"\d+\.\d+|^version-[0-9a-f]+$|^app-\d", RegexOptions.IgnoreCase)]
    private static partial Regex VersionFolder();

    static bool IsVersion(string segment) => VersionFolder().IsMatch(segment);

    static string? Nice(string? description)
    {
        var text = description?.Trim();
        if (text is not null && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];   // some apps describe themselves as "SnippingTool.exe"
        if (text is not null && text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static bool IsUnderOrSame(string path, string folder) =>
        string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || IsUnder(path, folder);

    static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
