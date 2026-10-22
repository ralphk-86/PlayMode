using System.Text;
using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Discovery;

/// <summary>An app the user could add to a profile, with the rule PlayMode would use.</summary>
public sealed record AppSuggestion
{
    public required string Title { get; init; }
    public required AppRule Rule { get; init; }
    public CatalogApp? Recipe { get; init; }
    public RunningApp? App { get; init; }
    public StartupEntry? Startup { get; init; }
    public bool IsRunning { get; init; }
    public bool Installed { get; init; }
    public bool AlreadyAdded { get; init; }

    /// <summary>Services that belong with the app (e.g. Proton VPN's), offered alongside it.</summary>
    public IReadOnlyList<ServiceRule> Services { get; init; } = [];

    public string Category => Recipe?.Category ?? "Other";
}

public sealed record ServiceSuggestion
{
    public required ServiceRule Rule { get; init; }
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public bool Running { get; init; }
    public bool IsWindows { get; init; }
    public bool InCatalog { get; init; }
    public bool AlreadyAdded { get; init; }

    /// <summary>The catalog app it belongs with, if any.</summary>
    public string? BelongsTo { get; init; }
}

public static class Suggestions
{
    /// <summary>Names too common to identify an app on their own; rules for these also check the folder.</summary>
    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "python", "pythonw", "java", "javaw", "dotnet", "electron", "app", "launcher", "update", "updater",
        "helper", "server", "service", "host", "main", "client", "agent", "claude", "express", "tray",
    };

    /// <summary>
    /// Everything worth offering: what's running now, what starts with Windows, then the rest of the catalog.
    /// Each app appears once, with the catalog's recipe when it has one.
    /// </summary>
    public static IReadOnlyList<AppSuggestion> Apps(MachineScan scan, Catalog catalog, Profile profile, MatchContext ctx)
    {
        var list = new List<AppSuggestion>();
        var usedRecipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedStartup = new HashSet<StartupEntry>();

        foreach (var app in scan.Running.Where(a => !ProcessMatcher.IsNeverTouch(a.Main.Name, ctx)))
        {
            var recipe = RecipeFor(app.Processes, catalog, ctx);
            if (recipe is not null && !usedRecipes.Add(recipe.Rule.Id))
                continue;   // e.g. PowerToys and Command Palette: one app, one rule
            if (app.Startup is not null)
                usedStartup.Add(app.Startup);
            // Only borrow start details from the app the recipe is named after, not a sidekick it also covers
            // (Command Palette running on its own must not become how PowerToys is started).
            var isRecipeMain = recipe is null || (recipe.Rule.Processes.Count > 0 && ProcessMatcher.Like(app.Main.Name, recipe.Rule.Processes[0]));
            var rule = recipe is null ? Fill(Generic(app, scan), app, app.Startup)
                : isRecipeMain ? Fill(recipe.Rule, app, app.Startup)
                : recipe.Rule;
            list.Add(Suggest(rule, recipe, recipe?.Name ?? app.Title, profile, ctx, app.Processes, catalog, scan) with
            {
                App = app,
                Startup = app.Startup,
                IsRunning = true,
            });
        }

        foreach (var entry in scan.Startup.Where(e => e.Enabled && !usedStartup.Contains(e) && e.ProcessName is not null))
        {
            var fake = new ProcessInfo(0, entry.ProcessName!, entry.Exe, ctx.SessionId, 0);
            if (ProcessMatcher.IsNeverTouch(fake.Name, ctx) || catalog.Ignore.Processes.Contains(fake.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            var recipe = RecipeFor([fake], catalog, ctx);
            if (recipe is null && entry.Exe is not null && IsSystemTool(entry.Exe))
                continue;
            if (recipe is not null && !usedRecipes.Add(recipe.Rule.Id))
                continue;
            var rule = Fill(recipe?.Rule ?? Generic(entry), null, entry);
            list.Add(Suggest(rule, recipe, recipe?.Name ?? entry.Name, profile, ctx, [fake], catalog, scan) with { Startup = entry });
        }

        foreach (var recipe in catalog.Apps.Where(r => !usedRecipes.Contains(r.Rule.Id)))
        {
            var running = recipe.Rule.Kind switch
            {
                AppKind.Wsl => scan.WslRunning,
                AppKind.Tailscale => scan.TailscaleUp,
                _ => false,
            };
            list.Add(Suggest(recipe.Rule, recipe, recipe.Name, profile, ctx, [], catalog, scan) with { IsRunning = running });
        }
        return list;
    }

    public static IReadOnlyList<ServiceSuggestion> Services(MachineScan scan, Catalog catalog, Profile profile, bool includeWindows = false)
    {
        var list = new List<ServiceSuggestion>();
        foreach (var service in scan.Services.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.KeepServices.Contains(service.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            var known = catalog.FindService(service.Name);
            if (known is null && !(service.Running && (includeWindows || !service.IsWindows)))
                continue;
            list.Add(new ServiceSuggestion
            {
                Rule = known?.ToRule() ?? new ServiceRule { Name = service.Name },
                Title = known?.Title ?? service.DisplayName,
                Notes = known?.Notes,
                Running = service.Running,
                IsWindows = service.IsWindows,
                InCatalog = known is not null,
                AlreadyAdded = profile.Services.Any(s => string.Equals(s.Name, service.Name, StringComparison.OrdinalIgnoreCase)),
                BelongsTo = catalog.Apps.FirstOrDefault(a => a.Services.Contains(service.Name, StringComparer.OrdinalIgnoreCase))?.Name,
            });
        }
        return list;
    }

    /// <summary>Game launchers, voice chat and overlays on this machine that aren't on the never-touch list yet.</summary>
    public static IReadOnlyList<LeaveAloneEntry> LeaveAlone(MachineScan scan, Catalog catalog, IReadOnlyCollection<string> neverTouch)
    {
        var present = new HashSet<string>(scan.ProcessNames, StringComparer.OrdinalIgnoreCase);
        present.UnionWith(scan.Startup.Select(s => s.ProcessName).OfType<string>());
        return [.. catalog.LeaveAlone.Where(e =>
            e.Processes.Any(present.Contains)
            && !e.Processes.All(p => neverTouch.Contains(p, StringComparer.OrdinalIgnoreCase)))];
    }

    /// <summary>A first profile for a new user: known apps found on this machine, ready to review.</summary>
    public static PlayModeConfig Starter(MachineScan scan, Catalog catalog, MatchContext ctx)
    {
        var empty = new Profile { Name = "Play" };
        var picked = Apps(scan, catalog, empty, ctx)
            .Where(s => s.Recipe is not null && (s.IsRunning || s.Startup is not null || (s.Installed && s.Rule.Kind != AppKind.Process)))
            .OrderBy(s => catalog.Apps.ToList().IndexOf(s.Recipe!))   // the catalog's order: dev stack first
            .ToList();
        var services = picked.SelectMany(s => s.Services)
            .Concat(Services(scan, catalog, empty).Where(s => s.InCatalog && s.Running && s.BelongsTo is null).Select(s => s.Rule))
            .Where(rule => scan.Services.Any(s => s.Running && string.Equals(s.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
        return new PlayModeConfig
        {
            DefaultProfile = "Play",
            NeverTouch = [.. LeaveAlone(scan, catalog, []).SelectMany(e => e.Processes)],
            Profiles = [empty with { Apps = [.. picked.Select(s => s.Rule)], Services = [.. services] }],
        };
    }

    /// <summary>The id, or the id with a number on the end if the profile already has it.</summary>
    public static AppRule WithUniqueId(AppRule rule, IEnumerable<AppRule> existing)
    {
        var taken = existing.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(rule.Id))
            return rule;
        var n = 2;
        while (taken.Contains($"{rule.Id}{n}"))
            n++;
        return rule with { Id = $"{rule.Id}{n}" };
    }

    /// <summary>"Visual Studio Code" → "VisualStudioCode".</summary>
    public static string MakeId(string title)
    {
        var id = new StringBuilder();
        var upper = true;
        foreach (var c in title)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                id.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }
        return id.Length == 0 ? "App" : id.ToString();
    }

    static AppSuggestion Suggest(AppRule rule, CatalogApp? recipe, string title, Profile profile, MatchContext ctx,
        IReadOnlyList<ProcessInfo> processes, Catalog catalog, MachineScan scan) => new()
    {
        Title = title,
        Rule = rule,
        Recipe = recipe,
        Installed = recipe is not null && scan.InstalledRecipes.Contains(recipe.Rule.Id),
        AlreadyAdded = profile.Apps.Any(a =>
            string.Equals(a.Id, rule.Id, StringComparison.OrdinalIgnoreCase)
            || (a.Kind != AppKind.Process && a.Kind == rule.Kind)
            || (a.Kind == AppKind.Process && ProcessMatcher.Match(a, processes, ctx).Count > 0)),
        Services = recipe is null ? [] : [.. recipe.Services.Select(n => catalog.FindService(n)?.ToRule() ?? new ServiceRule { Name = n })],
    };

    static CatalogApp? RecipeFor(IReadOnlyList<ProcessInfo> processes, Catalog catalog, MatchContext ctx)
    {
        var anyCtx = ctx with { SessionId = processes.FirstOrDefault()?.SessionId ?? ctx.SessionId };
        // The main process decides first: an app that merely bundles CefSharp isn't Garmin Express.
        return catalog.Apps.FirstOrDefault(a => processes.Count > 0 && ProcessMatcher.Match(a.Rule, [processes[0]], anyCtx).Count > 0)
            ?? catalog.Apps.FirstOrDefault(a => a.Rule.Processes.Count > 0
                && ProcessMatcher.Match(a.Rule with { Processes = [a.Rule.Processes[0]] }, processes, anyCtx).Count > 0);
    }

    /// <summary>A rule for an app the catalog doesn't know.</summary>
    static AppRule Generic(RunningApp app, MachineScan scan)
    {
        var names = app.ProcessNames;
        var shared = scan.Running.Where(other => other != app).SelectMany(o => o.ProcessNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ambiguous = names.Any(n => GenericNames.Contains(n) || shared.Contains(n));
        return new AppRule
        {
            Id = MakeId(app.Title),
            Processes = names,
            PathLike = ambiguous ? app.FolderLike : null,
            // Reopening a windowless helper nobody started on purpose (a dev server, a runtime) does more harm than good.
            Restore = app.HasWindow || app.Startup is not null,
        };
    }

    static AppRule Generic(StartupEntry entry) => new()
    {
        Id = MakeId(entry.Name),
        Processes = [entry.ProcessName!],
    };

    /// <summary>Restart the app the way Windows starts it at sign-in, unless the recipe says otherwise.</summary>
    static AppRule Fill(AppRule rule, RunningApp? app, StartupEntry? startup)
    {
        if (rule.Kind != AppKind.Process || rule.Start is not null || rule.Args is not null)
            return rule;
        if (startup?.Shortcut is { } shortcut)
            return rule with { Start = EnvPaths.Collapse(shortcut) };
        if (startup?.Exe is { } exe)
        {
            var sameExe = app is not null
                ? string.Equals(app.Main.Path, exe, StringComparison.OrdinalIgnoreCase)
                : startup.Launches is null;
            // A versioned install path changes on update, so only pin the exe when it's a launcher stub.
            return rule with { Start = sameExe ? null : EnvPaths.Collapse(exe), Args = startup.Args };
        }
        if (app?.AppUserModelId is { } aumid)
            return rule with { Start = $@"shell:AppsFolder\{aumid}" };
        return rule;
    }

    static bool IsSystemTool(string exe)
    {
        var windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return !Path.IsPathRooted(exe) || exe.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Plain-language descriptions of rules, for lists and previews.</summary>
public static class RuleText
{
    public static string Describe(AppRule rule)
    {
        if (rule.Kind == AppKind.Wsl)
            return "Shut down all distros" + (rule.Risky ? " · asks first" : "");
        if (rule.Kind == AppKind.Tailscale)
            return "Disconnect, reconnect afterwards";

        var parts = new List<string>();
        if (rule.Action == CloseAction.Suspend)
        {
            parts.Add("Freeze, unfreeze afterwards");
        }
        else
        {
            var close = rule.Force ? "End" : "Close";
            parts.Add(!rule.Restore ? $"{close}, don't reopen"
                : rule.Args is { Length: > 0 } args ? $"{close}, reopen with {args}"
                : $"{close}, reopen");
        }
        if (rule.Restore && rule.Window != WindowAction.None)
            parts.Add(rule.Window == WindowAction.Hide ? "hides its window" : "closes its window");
        if (rule.Risky)
            parts.Add("asks first");
        return string.Join(" · ", parts);
    }
}
