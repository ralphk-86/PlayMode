using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Engine;

public sealed record EngineOptions
{
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan KillTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan WslSettle { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan RelaunchCheckDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RestoreTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>Switches between dev mode and a play profile, and back to exactly what was running.</summary>
public sealed class PlayModeEngine(PlatformServices os, PlayModeConfig config, EngineOptions? options = null)
{
    readonly EngineOptions _opt = options ?? new();

    MatchContext Context => new(os.Processes.CurrentProcessId, os.Processes.CurrentSessionId, config.NeverTouch);

    public PlayState State => os.Store.Load();

    /// <summary>
    /// Signed in again while in play mode (reboot, Fast Startup power-on, sign-out/in): startup apps are
    /// back but Tailscale stays down and non-startup apps are missing, so the restore should be finished.
    /// </summary>
    public bool NeedsRecovery()
    {
        var state = os.Store.Load();
        return state.Mode == Mode.Play
            && state.Since is { } since
            && os.System.SessionLogonTime is { } logon
            && logon > since;
    }

    public Plan BuildPlan(string? profileName = null) => BuildPlan(config.GetProfile(profileName));

    Plan BuildPlan(Profile profile)
    {
        var snapshot = os.Processes.Snapshot();
        var ctx = Context;
        var items = new List<PlanItem>();
        foreach (var rule in profile.Apps)
        {
            if (rule.Kind == AppKind.Tailscale)
            {
                if (os.Tailscale.IsInstalled && os.Tailscale.IsUp())
                    items.Add(new PlanItem(rule, [], 0, null, "connected"));
                continue;
            }
            var procs = ProcessMatcher.Match(rule, snapshot, ctx);
            if (procs.Count == 0)
                continue;
            var memory = procs.Sum(p => p.WorkingSet);
            var mainPattern = ProcessMatcher.PatternsFor(rule)[0];
            var main = procs.FirstOrDefault(p => p.Path is not null && ProcessMatcher.Like(p.Name, mainPattern))
                ?? procs.FirstOrDefault(p => p.Path is not null);
            var detail = rule.Kind == AppKind.Wsl
                ? $"running: {string.Join(", ", os.Wsl.RunningDistros())}, {Mb(memory)} MB"
                : $"{procs.Count} process{(procs.Count == 1 ? "" : "es")}, {Mb(memory)} MB";
            items.Add(new PlanItem(rule, procs, memory, main?.Path, detail));
        }
        return new Plan(profile, items, os.Services.Running(profile.Services.Select(s => s.Name)));
    }

    /// <param name="confirm">Asked when risky apps are running; null means don't ask.</param>
    /// <returns>The report, or null if the user cancelled.</returns>
    public async Task<Report?> EnableAsync(string? profileName, Func<string, Task<bool>>? confirm, CancellationToken ct = default)
    {
        var profile = config.GetProfile(profileName);
        var plan = BuildPlan(profile);

        var risky = plan.Risky.ToList();
        if (risky.Count > 0 && profile.ConfirmRisky && confirm is not null)
        {
            var text = "These have live work that will be lost:\n\n"
                + string.Join("\n", risky.Select(i => $"  - {i.Rule.Id} ({i.Detail})"))
                + $"\n\nSwitch to {profile.Name} anyway?";
            if (!await confirm(text).ConfigureAwait(false))
            {
                os.Log.Info("Switch cancelled by user");
                return null;
            }
        }

        // Record before acting, so a crash mid-switch can still be restored.
        var state = os.Store.Load();
        if (state.Mode != Mode.Play)
            state = new PlayState { Mode = Mode.Play, Profile = profile.Name };
        state.Since = os.Clock.Now;
        foreach (var item in plan.Items)
        {
            if (item.Rule.Kind == AppKind.Tailscale)
                state.TailscaleWasUp = true;
            else if (state.Closed.All(c => c.Id != item.Rule.Id))
                state.Closed.Add(new ClosedApp(item.Rule.Id, item.MainPath));
        }
        os.Store.Save(state);
        var affecting = plan.Items.Select(i => i.Rule.Id).Concat(plan.RunningServices.Count > 0 ? [$"{plan.RunningServices.Count} services"] : []).ToList();
        os.Log.Info($"{profile.Name} ON. Affecting: {(affecting.Count > 0 ? string.Join(", ", affecting) : "nothing running")}");

        var notes = new List<string>();
        var apps = plan.Items.Where(i => i.Rule.Kind == AppKind.Process).ToList();

        var suspended = 0;
        foreach (var item in apps.Where(i => i.Rule.Action == CloseAction.Suspend))
        {
            var index = state.Closed.FindIndex(c => c.Id == item.Rule.Id);
            // Windows counts suspends, and restore resumes once: never freeze the same process twice.
            var already = state.Closed[index].SuspendedPids.ToHashSet();
            var fresh = item.Processes.Where(p => !already.Contains(p.Id)).ToList();
            var pids = fresh.Where(p => os.Control.Suspend(p.Id)).Select(p => p.Id).ToList();
            if (pids.Count < fresh.Count)
                notes.Add($"{item.Rule.Id}: could not suspend {fresh.Count - pids.Count} process(es)");
            state.Closed[index] = state.Closed[index] with { SuspendedPids = [.. already.Union(pids)] };
            if (pids.Count > 0)
                suspended++;
        }
        if (suspended > 0)
            os.Store.Save(state);

        var closing = apps.Where(i => i.Rule.Action == CloseAction.Close).ToList();
        var closingPids = closing.SelectMany(i => i.Processes).Select(p => p.Id).ToHashSet();
        // Force-only apps relaunch themselves when asked to close politely.
        foreach (var p in closing.Where(i => i.Rule.Force).SelectMany(i => i.Processes))
            os.Control.Kill(p.Id);
        var stuck = await CloseAsync(closingPids, ct).ConfigureAwait(false);

        var wsl = plan.Items.FirstOrDefault(i => i.Rule.Kind == AppKind.Wsl);
        if (wsl is not null)
        {
            os.Wsl.Shutdown();
            await os.Clock.Delay(_opt.WslSettle, ct).ConfigureAwait(false);
            if (ProcessMatcher.Match(wsl.Rule, os.Processes.Snapshot(), Context).Count > 0)
                notes.Add("WSL did not shut down");
        }

        if (state.TailscaleWasUp && os.Tailscale.IsUp())
        {
            os.Tailscale.Down();
            if (os.Tailscale.IsUp())
                notes.Add("Tailscale did not disconnect");
        }

        ServiceResult? services = null;
        if (plan.RunningServices.Count > 0)
        {
            services = await os.Services.StopAsync(plan.RunningServices, ct).ConfigureAwait(false);
            if (services is null)
                notes.Add("Services were not stopped (helper missing or timed out)");
            else
                notes.AddRange([.. services.Failed.Select(f => $"Service problem: {f}"), .. services.Notes]);
        }

        // Some apps get relaunched by Windows or a watchdog right away; report them rather than fight.
        await os.Clock.Delay(_opt.RelaunchCheckDelay, ct).ConfigureAwait(false);
        stuck = [.. stuck.Where(os.Processes.IsAlive)];
        if (stuck.Count > 0)
        {
            var names = closing.SelectMany(i => i.Processes).Where(p => stuck.Contains(p.Id)).Select(p => p.Name).Distinct();
            notes.Add($"Could not close: {string.Join(", ", names)}");
        }
        var closingIds = closing.Select(i => i.Rule.Id).ToHashSet();
        var relaunched = BuildPlan(profile).Items
            .Where(i => closingIds.Contains(i.Rule.Id) && i.Processes.Any(p => !closingPids.Contains(p.Id)))
            .Select(i => i.Rule.Id).ToList();
        if (relaunched.Count > 0)
            notes.Add($"Relaunched by itself: {string.Join(", ", relaunched)}");

        var freed = closing.Sum(i => i.Memory) + (wsl?.Memory ?? 0);
        var parts = new List<string> { $"closed {closing.Count} apps (~{freed / 1073741824.0:0.0} GB)" };
        if (suspended > 0)
            parts.Add($"suspended {suspended}");
        if (services is not null)
            parts.Add($"stopped {services.Ok.Count} services");
        if (state.TailscaleWasUp)
            parts.Add("Tailscale off");
        var report = new Report($"{profile.Name} ON: {string.Join(", ", parts)}", notes);

        state = os.Store.Load();
        state.LastReport = report.ToString();
        os.Store.Save(state);
        os.Log.Info(report.ToString());
        return report;
    }

    public async Task<Report> DisableAsync(CancellationToken ct = default)
    {
        var state = os.Store.Load();
        if (state.Mode != Mode.Play)
            os.Log.Info("Already in dev mode; checking anyway");
        var profile = config.Profiles.FirstOrDefault(p => p.Name == state.Profile) ?? config.GetProfile(null);
        var notes = new List<string>();

        // Services first: some apps (e.g. Everything) need theirs running before the client starts.
        // The helper remembers what it stopped, so this is safe even if nothing was.
        var services = profile.Services.Count > 0 ? await os.Services.StartAsync(ct).ConfigureAwait(false) : null;
        if (services is not null)
            notes.AddRange(services.Failed.Select(f => $"Service problem: {f}"));

        if (state.TailscaleWasUp && os.Tailscale.IsInstalled && !os.Tailscale.IsUp() && !os.Tailscale.Up())
            notes.Add("Tailscale did not reconnect");

        var snapshot = os.Processes.Snapshot();
        var ctx = Context;
        var launched = new List<AppRule>();
        var resumed = 0;
        foreach (var rule in RulesToRestore(profile, state))
        {
            var entry = state.Closed.First(c => c.Id == rule.Id);
            if (entry.SuspendedPids.Count > 0)
            {
                var ok = 0;
                foreach (var pid in entry.SuspendedPids.Where(os.Processes.IsAlive))
                {
                    if (os.Control.Resume(pid))
                        ok++;
                }
                if (ok > 0)
                {
                    resumed++;
                    continue;
                }
                // It died while suspended; fall through and relaunch it.
            }
            if (!rule.Restore || ProcessMatcher.Match(rule, snapshot, ctx).Count > 0)
                continue;
            var target = Expand(rule.Start) ?? entry.Path;
            if (target is null)
            {
                notes.Add($"{rule.Id}: no start path known");
                continue;
            }
            var error = os.Control.Start(target, rule.Args);
            if (error is null)
                launched.Add(rule);
            else
                notes.Add($"{rule.Id}: {error}");
        }

        // Confirm they came up, and put away windows that a manual launch shows but autostart doesn't.
        var tuck = launched.Where(r => r.Window != WindowAction.None).ToList();
        var deadline = os.Clock.Now + _opt.RestoreTimeout;
        while (os.Clock.Now < deadline)
        {
            var snap = os.Processes.Snapshot();
            foreach (var rule in tuck.ToList())
            {
                var pids = ProcessMatcher.Match(rule, snap, ctx).Select(p => p.Id).ToList();
                if (pids.Count == 0)
                    continue;
                var windows = os.Windows.VisibleWindows(pids).Where(w => ProcessMatcher.Like(w.Title, rule.WindowTitle ?? "*")).ToList();
                foreach (var w in windows)
                {
                    if (rule.Window == WindowAction.Hide)
                        os.Windows.Hide(w.Handle);
                    else
                        os.Windows.Close(w.Handle);
                }
                if (windows.Count > 0)
                    tuck.Remove(rule);
            }
            if (tuck.Count == 0 && launched.All(r => ProcessMatcher.Match(r, snap, ctx).Count > 0))
                break;
            await os.Clock.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        var final = os.Processes.Snapshot();
        var missing = launched.Where(r => ProcessMatcher.Match(r, final, ctx).Count == 0).Select(r => r.Id).ToList();
        if (missing.Count > 0)
            notes.Add($"Started but not running: {string.Join(", ", missing)}");

        var parts = new List<string> { $"restored {launched.Count - missing.Count} apps" };
        if (resumed > 0)
            parts.Add($"resumed {resumed}");
        if (services is not null)
            parts.Add($"started {services.Ok.Count} services");
        if (state.TailscaleWasUp)
            parts.Add("Tailscale on");
        var report = new Report($"Dev mode: {string.Join(", ", parts)}", notes);
        os.Store.Save(new PlayState { Mode = Mode.Dev, LastReport = report.ToString() });
        os.Log.Info(report.ToString());
        return report;
    }

    /// <summary>Profile order first, then anything recorded that the profile no longer lists.</summary>
    static IEnumerable<AppRule> RulesToRestore(Profile profile, PlayState state)
    {
        var closed = state.Closed.Select(c => c.Id).ToHashSet();
        foreach (var rule in profile.Apps.Where(r => r.Kind == AppKind.Process && closed.Contains(r.Id)))
            yield return rule;
        foreach (var orphan in state.Closed.Where(c => profile.Apps.All(a => a.Id != c.Id) && c.Path is not null))
            yield return new AppRule { Id = orphan.Id, Processes = [Path.GetFileNameWithoutExtension(orphan.Path!)] };
    }

    /// <summary>Polite close first, then force whatever is left.</summary>
    async Task<List<int>> CloseAsync(IReadOnlyCollection<int> pids, CancellationToken ct)
    {
        var alive = pids.Where(os.Processes.IsAlive).ToList();
        foreach (var pid in alive)
            os.Control.RequestClose(pid);
        alive = await WaitForExitAsync(alive, _opt.GracefulTimeout, ct).ConfigureAwait(false);
        foreach (var pid in alive)
            os.Control.Kill(pid);
        return await WaitForExitAsync(alive, _opt.KillTimeout, ct).ConfigureAwait(false);
    }

    async Task<List<int>> WaitForExitAsync(List<int> pids, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = os.Clock.Now + timeout;
        while (true)
        {
            var remaining = pids.Where(os.Processes.IsAlive).ToList();
            if (remaining.Count == 0 || os.Clock.Now >= deadline)
                return remaining;
            await os.Clock.Delay(_opt.PollInterval, ct).ConfigureAwait(false);
        }
    }

    static string? Expand(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);

    static long Mb(long bytes) => bytes / (1024 * 1024);
}
