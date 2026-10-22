using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Tests;

public class EngineTests
{
    readonly FakeOs _os = new();

    PlayModeEngine Engine(params AppRule[] apps) => Engine(new Profile { Name = "Gaming", Apps = apps });

    PlayModeEngine Engine(Profile profile, params string[] neverTouch) =>
        new(_os.Platform, new PlayModeConfig { Profiles = [profile], NeverTouch = neverTouch });

    static AppRule App(string id, params string[] processes) => new() { Id = id, Processes = processes.Length > 0 ? processes : [id] };

    static Func<string, Task<bool>> Answer(bool yes, List<string>? asked = null) => text =>
    {
        asked?.Add(text);
        return Task.FromResult(yes);
    };

    // ---------------------------------------------------------------- switching on

    [Fact]
    public async Task Closes_matching_apps_politely_and_records_how_to_restore_them()
    {
        var spotify = _os.Add("Spotify", @"C:\Spotify\Spotify.exe");
        var engine = Engine(App("Spotify"));

        var report = await engine.EnableAsync(null, confirm: null);

        Assert.False(spotify.Alive);
        Assert.Contains(spotify.Id, _os.CloseRequests);
        Assert.Empty(_os.Kills);
        var state = engine.State;
        Assert.Equal(Mode.Play, state.Mode);
        var closed = state.Closed.Single();
        Assert.Equal(("Spotify", @"C:\Spotify\Spotify.exe"), (closed.Id, closed.Path));
        Assert.Empty(closed.SuspendedPids);
        Assert.StartsWith("Gaming ON: closed 1 apps", report!.Title);
    }

    [Fact]
    public async Task Force_kills_what_ignores_the_polite_close()
    {
        var stubborn = _os.Add("Drive", ignoresClose: true);
        await Engine(App("Drive")).EnableAsync(null, null);

        Assert.False(stubborn.Alive);
        Assert.Contains(stubborn.Id, _os.CloseRequests);
        Assert.Contains(stubborn.Id, _os.Kills);
    }

    [Fact]
    public async Task Force_rule_skips_the_polite_close()
    {
        // Some apps relaunch themselves when asked to quit (seen with a usage-monitor tray app).
        var monitor = _os.Add("monitor");
        await Engine(App("monitor") with { Force = true }).EnableAsync(null, null);

        Assert.False(monitor.Alive);
        Assert.DoesNotContain(monitor.Id, _os.CloseRequests);
    }

    [Fact]
    public async Task Reports_processes_it_could_not_close()
    {
        _os.Add("Protected", ignoresClose: true, unkillable: true);
        var report = await Engine(App("Protected")).EnableAsync(null, null);
        Assert.Contains("Could not close: Protected", report!.Notes);
    }

    [Fact]
    public async Task Reports_apps_that_relaunch_themselves()
    {
        _os.Add("Watchdogged");
        _os.Respawners.Add("Watchdogged");
        var report = await Engine(App("Watchdogged")).EnableAsync(null, null);
        Assert.Contains("Relaunched by itself: Watchdogged", report!.Notes);
    }

    [Fact]
    public async Task Leaves_apps_that_are_not_in_the_profile_alone()
    {
        var discord = _os.Add("Discord");
        _os.Add("Spotify");
        await Engine(App("Spotify")).EnableAsync(null, null);
        Assert.True(discord.Alive);
    }

    [Fact]
    public async Task Asks_before_closing_risky_apps_and_does_nothing_if_declined()
    {
        var wsl = _os.Add("vmmemWSL", session: 0);
        var spotify = _os.Add("Spotify");
        var engine = Engine(new AppRule { Id = "WSL", Kind = AppKind.Wsl, Risky = true }, App("Spotify"));
        var asked = new List<string>();

        var report = await engine.EnableAsync(null, Answer(false, asked));

        Assert.Null(report);
        Assert.Contains("WSL (running: Ubuntu-24.04", Assert.Single(asked));
        Assert.True(wsl.Alive);
        Assert.True(spotify.Alive);
        Assert.Equal(Mode.Dev, engine.State.Mode);
        Assert.Equal(0, _os.WslShutdowns);
    }

    [Fact]
    public async Task Proceeds_with_risky_apps_when_confirmed()
    {
        var wsl = _os.Add("vmmemWSL", session: 0);
        await Engine(new AppRule { Id = "WSL", Kind = AppKind.Wsl, Risky = true }).EnableAsync(null, Answer(true));
        Assert.Equal(1, _os.WslShutdowns);
        Assert.False(wsl.Alive);
    }

    [Fact]
    public async Task Does_not_ask_when_nothing_risky_is_running()
    {
        _os.Add("Spotify");
        var asked = new List<string>();
        await Engine(App("Spotify"), App("Code") with { Risky = true }).EnableAsync(null, Answer(false, asked));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Disconnects_tailscale_only_if_it_was_up()
    {
        var engine = Engine(new AppRule { Id = "Tailscale", Kind = AppKind.Tailscale });
        await engine.EnableAsync(null, null);
        Assert.Equal(0, _os.TailscaleDownCalls);
        Assert.False(engine.State.TailscaleWasUp);

        _os.TailscaleUp = true;
        await engine.DisableAsync();
        await engine.EnableAsync(null, null);
        Assert.Equal(1, _os.TailscaleDownCalls);
        Assert.True(engine.State.TailscaleWasUp);
    }

    [Fact]
    public async Task Stops_services_only_when_some_are_running()
    {
        var profile = new Profile { Name = "Gaming", Services = [new ServiceRule { Name = "WSearch" }] };
        await Engine(profile).EnableAsync(null, null);
        Assert.Equal(0, _os.StopCalls);

        _os.RunningServices.Add("WSearch");
        _os.StopResult = new ServiceResult(["WSearch"], [], []);
        var report = await Engine(profile).EnableAsync(null, null);
        Assert.Equal(1, _os.StopCalls);
        Assert.Contains("stopped 1 services", report!.Title);
    }

    [Fact]
    public async Task Says_so_when_the_service_helper_is_missing()
    {
        _os.RunningServices.Add("WSearch");
        var report = await Engine(new Profile { Name = "Gaming", Services = [new ServiceRule { Name = "WSearch" }] }).EnableAsync(null, null);
        Assert.Contains(report!.Notes, n => n.StartsWith("Services were not stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_on_twice_does_not_duplicate_the_restore_list()
    {
        _os.Add("Spotify");
        var engine = Engine(App("Spotify"));
        await engine.EnableAsync(null, null);
        _os.Add("Spotify");
        await engine.EnableAsync(null, null);
        Assert.Single(engine.State.Closed);
    }

    // ---------------------------------------------------------------- suspend

    [Fact]
    public async Task Suspend_rule_freezes_instead_of_closing_and_resume_brings_it_back()
    {
        var ide = _os.Add("rider64");
        var engine = Engine(App("Rider", "rider64") with { Action = CloseAction.Suspend });

        var on = await engine.EnableAsync(null, null);
        Assert.True(ide.Alive);
        Assert.True(ide.Suspended);
        Assert.Contains("suspended 1", on!.Title);
        Assert.Equal([ide.Id], engine.State.Closed.Single().SuspendedPids);

        var off = await engine.DisableAsync();
        Assert.False(ide.Suspended);
        Assert.Contains("resumed 1", off.Title);
        Assert.Empty(_os.Starts);
    }

    [Fact]
    public async Task Relaunches_a_suspended_app_that_died_meanwhile()
    {
        var ide = _os.Add("rider64", @"C:\JetBrains\rider64.exe");
        var engine = Engine(App("Rider", "rider64") with { Action = CloseAction.Suspend });
        await engine.EnableAsync(null, null);
        ide.Alive = false;

        await engine.DisableAsync();
        Assert.Equal(@"C:\JetBrains\rider64.exe", _os.Starts.Single().Target);
    }

    // ---------------------------------------------------------------- switching off

    [Fact]
    public async Task Restores_only_what_was_running_using_start_overrides_and_args()
    {
        _os.Add("Spotify", @"C:\Spotify\Spotify.exe");
        _os.Add("PowerToys");
        var engine = Engine(
            App("Spotify") with { Args = "--minimized" },
            App("PowerToys") with { Start = @"C:\Custom\PowerToys.exe" },
            App("NotRunning"));
        await engine.EnableAsync(null, null);

        var report = await engine.DisableAsync();

        Assert.Equal([(@"C:\Spotify\Spotify.exe", "--minimized"), (@"C:\Custom\PowerToys.exe", (string?)null)], _os.Starts);
        Assert.Equal("Dev mode: restored 2 apps", report.Title);
        Assert.Equal(Mode.Dev, engine.State.Mode);
        Assert.Empty(engine.State.Closed);
    }

    [Fact]
    public async Task Does_not_start_a_second_copy_of_something_already_running()
    {
        _os.Add("Spotify");
        var engine = Engine(App("Spotify"));
        await engine.EnableAsync(null, null);
        _os.Add("Spotify");   // user started it again during play

        await engine.DisableAsync();
        Assert.Empty(_os.Starts);
        Assert.Equal(1, _os.Count("Spotify"));
    }

    [Fact]
    public async Task Close_only_apps_are_not_relaunched()
    {
        _os.Add("WhatsApp");
        var engine = Engine(App("WhatsApp") with { Restore = false });
        await engine.EnableAsync(null, null);
        await engine.DisableAsync();
        Assert.Empty(_os.Starts);
    }

    [Fact]
    public async Task Closes_or_hides_windows_that_a_manual_launch_pops_up()
    {
        // Handy closes to its tray; LG's app quits if closed, so it gets hidden instead.
        _os.Add("handy", @"C:\Handy\handy.exe");
        _os.Add("LGMonitor", @"C:\LG\LGMonitor.exe");
        _os.Launchers[@"C:\Handy\handy.exe"] = ("handy", ["handy-siw", "Handy"]);
        _os.Launchers[@"C:\LG\LGMonitor.exe"] = ("LGMonitor", ["LG Monitor App Installer"]);
        var engine = Engine(
            App("Handy", "handy") with { Window = WindowAction.Close, WindowTitle = "Handy" },
            App("LG", "LGMonitor") with { Window = WindowAction.Hide, WindowTitle = "LG Monitor App*" });
        await engine.EnableAsync(null, null);

        await engine.DisableAsync();

        var handy = _os.Find("handy")!;
        Assert.Equal([handy.Windows.Single(w => w.Title == "Handy").Handle], _os.ClosedWindows);
        Assert.True(handy.Windows.Single(w => w.Title == "handy-siw").Visible);   // title filter respected
        Assert.Single(_os.HiddenWindows);
    }

    [Fact]
    public async Task Reports_restore_failures_without_giving_up_on_the_rest()
    {
        _os.Add("Gone", @"C:\missing\Gone.exe");
        _os.Add("Spotify");
        var engine = Engine(App("Gone"), App("Spotify"));
        await engine.EnableAsync(null, null);

        var report = await engine.DisableAsync();

        Assert.Contains(report.Notes, n => n.StartsWith("Gone: not found", StringComparison.Ordinal));
        Assert.Equal(1, _os.Count("Spotify"));
    }

    [Fact]
    public async Task Restores_apps_that_were_removed_from_the_profile_meanwhile()
    {
        _os.Add("Spotify", @"C:\Spotify\Spotify.exe");
        await Engine(App("Spotify")).EnableAsync(null, null);

        await Engine(App("Other")).DisableAsync();
        Assert.Equal(@"C:\Spotify\Spotify.exe", _os.Starts.Single().Target);
    }

    [Fact]
    public async Task Reconnects_tailscale_and_reports_if_it_fails()
    {
        _os.TailscaleUp = true;
        var engine = Engine(new AppRule { Id = "Tailscale", Kind = AppKind.Tailscale });
        await engine.EnableAsync(null, null);
        _os.TailscaleUpFails = true;

        var report = await engine.DisableAsync();
        Assert.Equal(1, _os.TailscaleUpCalls);
        Assert.Contains("Tailscale did not reconnect", report.Notes);
    }

    [Fact]
    public async Task Leaves_the_service_helper_alone_when_the_profile_has_no_services()
    {
        _os.Add("Spotify");
        var engine = Engine(App("Spotify"));
        await engine.EnableAsync(null, null);
        await engine.DisableAsync();
        Assert.Equal((0, 0), (_os.StopCalls, _os.StartCalls));
    }

    [Fact]
    public async Task Starts_services_before_launching_apps()
    {
        _os.Add("Everything");
        var engine = Engine(new Profile { Name = "Gaming", Apps = [App("Everything")], Services = [new ServiceRule { Name = "Everything" }] });
        await engine.EnableAsync(null, null);
        _os.StartResult = new ServiceResult(["Everything"], [], []);

        var report = await engine.DisableAsync();
        Assert.Equal(1, _os.StartCalls);
        Assert.Contains("started 1 services", report.Title);
    }

    // ---------------------------------------------------------------- recovery

    [Fact]
    public async Task Needs_recovery_only_after_signing_in_again()
    {
        _os.Add("Spotify");
        var engine = Engine(App("Spotify"));
        await engine.EnableAsync(null, null);   // at 12:00, signed in at 08:00
        Assert.False(engine.NeedsRecovery());

        // Shut down and powered on (Fast Startup keeps the boot time, but sign-in is new).
        _os.SessionLogonTime = _os.Now.AddHours(10);
        Assert.True(engine.NeedsRecovery());

        await engine.DisableAsync();
        Assert.False(engine.NeedsRecovery());
    }

    [Fact]
    public async Task Unknown_sign_in_time_never_triggers_recovery()
    {
        _os.Add("Spotify");
        var engine = Engine(App("Spotify"));
        await engine.EnableAsync(null, null);
        _os.SessionLogonTime = null;
        Assert.False(engine.NeedsRecovery());
    }

    [Fact]
    public async Task State_is_saved_before_anything_is_closed()
    {
        // If PlayMode crashes mid-switch, the restore list must already exist.
        var spotify = _os.Add("Spotify");
        var engine = Engine(App("Spotify"));
        PlayState? seenAtClose = null;
        _os.Respawners.Clear();
        var original = _os.Platform;
        var spy = original with { Control = new SpyControl(_os, () => seenAtClose ??= _os.Load()) };

        await new PlayModeEngine(spy, new PlayModeConfig { Profiles = [new Profile { Name = "Gaming", Apps = [App("Spotify")] }] }).EnableAsync(null, null);

        Assert.NotNull(seenAtClose);
        Assert.Equal(Mode.Play, seenAtClose!.Mode);
        Assert.Equal("Spotify", seenAtClose.Closed.Single().Id);
        Assert.False(spotify.Alive);
    }

    sealed class SpyControl(IProcessControl inner, Action onClose) : IProcessControl
    {
        public void RequestClose(int pid) { onClose(); inner.RequestClose(pid); }
        public void Kill(int pid) { onClose(); inner.Kill(pid); }
        public bool Suspend(int pid) => inner.Suspend(pid);
        public bool Resume(int pid) => inner.Resume(pid);
        public string? Start(string target, string? args) => inner.Start(target, args);
    }
}
