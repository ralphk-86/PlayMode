using PlayMode.Core.Discovery;
using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;
using PlayMode.Core.Storage;

namespace PlayMode.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\LGHUB\\lghub_system_tray.exe\" --minimized", @"C:\Program Files\LGHUB\lghub_system_tray.exe", "--minimized")]
    [InlineData("C:\\Program Files\\Proton\\VPN\\ProtonVPN.Launcher.exe \"--x:y\"", @"C:\Program Files\Proton\VPN\ProtonVPN.Launcher.exe", "\"--x:y\"")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\Handy\\handy.exe ", @"C:\Users\me\AppData\Local\Handy\handy.exe", null)]
    [InlineData("\"C:\\Program Files\\Everything\\Everything.exe\"", @"C:\Program Files\Everything\Everything.exe", null)]
    [InlineData("conhost.exe --headless pwsh.exe -File x.ps1", "conhost.exe", "--headless pwsh.exe -File x.ps1")]
    [InlineData("rundll32 shell32.dll,Control_RunDLL", "rundll32", "shell32.dll,Control_RunDLL")]
    public void Splits_program_from_arguments(string command, string exe, string? args)
    {
        var (gotExe, gotArgs, _) = CommandLine.Split(command);
        Assert.Equal(exe, gotExe);
        Assert.Equal(args, gotArgs);
    }

    [Theory]
    [InlineData("\"C:\\D\\Update.exe\" --processStart Discord.exe", "Discord")]
    [InlineData("\"C:\\D\\Update.exe\" --processStart \"Postman.exe\" --process-start-args \"--min\"", "Postman")]
    [InlineData("\"C:\\D\\app.exe\" --start", null)]
    public void Sees_through_launcher_stubs(string command, string? launches)
    {
        Assert.Equal(launches, CommandLine.Split(command).Launches);
    }

    [Fact]
    public void Collapses_paths_to_the_most_specific_variable()
    {
        (string, string)[] env = [("USERPROFILE", @"C:\Users\me"), ("LOCALAPPDATA", @"C:\Users\me\AppData\Local")];
        Assert.Equal(@"%LOCALAPPDATA%\X\x.exe", EnvPaths.Collapse(@"C:\Users\me\AppData\Local\X\x.exe", env));
        Assert.Equal(@"%USERPROFILE%\.local\bin\c.exe", EnvPaths.Collapse(@"C:\Users\me\.local\bin\c.exe", env));
        Assert.Equal(@"C:\Users\me2\x.exe", EnvPaths.Collapse(@"C:\Users\me2\x.exe", env));   // not a prefix of a folder name
    }
}

public class DiscoveryTests
{
    const string Local = @"C:\Users\me\AppData\Local";
    const string Pf = @"C:\Program Files";
    static readonly string[] Roots = [Pf, Local, $@"{Local}\Programs", @"C:\Users\me", @"C:\Users\me\.local\bin"];
    static readonly MatchContext Ctx = new(SelfPid: 1, SessionId: 1, NeverTouch: ["Discord2"]);

    int _pid = 100;

    ProcessInfo P(string path, int session = 1, long mb = 10) =>
        new(_pid++, Path.GetFileNameWithoutExtension(path), path, session, mb * 1024 * 1024);

    sealed class Details : IProcessDetails
    {
        public HashSet<int> Windows { get; } = [];
        public Dictionary<string, string> Descriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> Aumids { get; } = [];

        public bool HasWindow(int pid) => Windows.Contains(pid);
        public string? Describe(string path) => Descriptions.GetValueOrDefault(path);
        public string? AppUserModelId(int pid) => Aumids.GetValueOrDefault(pid);
    }

    static IReadOnlyList<RunningApp> Group(IEnumerable<ProcessInfo> procs, Details? details = null, IReadOnlyList<StartupEntry>? startup = null) =>
        AppGrouper.Group(procs, Roots, @"C:\Windows", Catalog.BuiltIn, Ctx, details ?? new Details(), startup ?? []);

    static StartupEntry Entry(string name, string command)
    {
        var (exe, args, launches) = CommandLine.Split(command);
        return new StartupEntry { Name = name, Location = "Run (user)", Command = command, Exe = exe, Args = args, Launches = launches };
    }

    [Fact]
    public void Processes_in_nested_folders_are_one_app()
    {
        var apps = Group([P($@"{Pf}\Docker\Docker\Docker Desktop.exe"), P($@"{Pf}\Docker\Docker\resources\com.docker.backend.exe")]);

        var docker = Assert.Single(apps);
        Assert.Equal("Docker Desktop", docker.Main.Name);
        Assert.Equal(["Docker Desktop", "com.docker.backend"], docker.ProcessNames);
        Assert.Equal(@"*\Docker\Docker\*", docker.FolderLike);
    }

    [Fact]
    public void Same_vendor_folder_is_not_the_same_app()
    {
        var apps = Group([P($@"{Pf}\Google\Chrome\Application\chrome.exe"), P($@"{Pf}\Google\Drive File Stream\132.0.0.0\GoogleDriveFS.exe")]);
        Assert.Equal(2, apps.Count);
        Assert.Contains(apps, a => a.Folder == $@"{Pf}\Google\Drive File Stream");   // version folder cut off
    }

    [Fact]
    public void Lone_tools_in_a_shared_bin_folder_stay_apart()
    {
        var apps = Group([P(@"C:\Users\me\.local\bin\claude.exe"), P(@"C:\Users\me\.local\bin\uv.exe")]);
        Assert.Equal(2, apps.Count);
        Assert.All(apps, a => Assert.Equal(@"*\.local\bin\*", a.FolderLike));
    }

    [Fact]
    public void Leaves_out_Windows_other_sessions_never_touch_and_shells()
    {
        var apps = Group(
        [
            P(@"C:\Windows\System32\notepad.exe"),
            P($@"{Pf}\Svc\svc.exe", session: 0),
            P($@"{Local}\Discord2\Discord2.exe"),
            P($@"{Pf}\PowerShell\7\pwsh.exe"),
            P($@"{Pf}\WSL\msrdc.exe"),
            P(@"C:\Windows\System32\AMD\ANR\AMDNoiseSuppression.exe"),   // in System32, but the catalog knows it
        ]);
        Assert.Equal(["AMDNoiseSuppression"], apps.Select(a => a.Main.Name));
    }

    [Fact]
    public void Launcher_stub_in_a_parent_folder_belongs_to_the_app_it_starts()
    {
        var discord = P($@"{Local}\Discord\app-1.0.9187\Discord.exe");
        var startup = Entry("Discord", $"\"{Local}\\Discord\\Update.exe\" --processStart Discord.exe");

        var app = Assert.Single(Group([discord, P($@"{Local}\Discord\app-1.0.9187\crashpad_handler.exe")], startup: [startup]));
        Assert.Same(startup, app.Startup);
        Assert.Equal($@"{Local}\Discord", app.Folder);
    }

    [Fact]
    public void Main_process_is_the_one_with_a_window_then_the_shallowest()
    {
        var helper = P($@"{Pf}\Tool\bin\helper.exe", mb: 500);
        var main = P($@"{Pf}\Tool\bin\tool.exe");
        var details = new Details();
        details.Windows.Add(main.Id);
        details.Descriptions[main.Path!] = "My Tool";

        var app = Assert.Single(Group([helper, main], details));
        Assert.Equal("tool", app.Main.Name);
        Assert.Equal("My Tool", app.Title);
        Assert.True(app.HasWindow);
    }

    // ---------------------------------------------------------------- suggestions

    static MachineScan Scan(IReadOnlyList<RunningApp> running, params StartupEntry[] startup) => new()
    {
        Running = running,
        Startup = startup,
        ProcessNames = running.SelectMany(a => a.ProcessNames).ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    static IReadOnlyList<AppSuggestion> Suggest(MachineScan scan, Profile? profile = null) =>
        Suggestions.Apps(scan, Catalog.BuiltIn, profile ?? new Profile { Name = "P" }, Ctx);

    [Fact]
    public void Unknown_app_restarts_the_way_Windows_starts_it()
    {
        var exe = $@"{Local}\Programs\Bitwarden\Bitwarden.exe";
        var startup = Entry("electron.app.Bitwarden", $"\"{exe}\" --autostart");
        var running = Group([P(exe)], startup: [startup]);

        var s = Suggest(Scan(running, startup)).First(x => x.IsRunning);
        Assert.Null(s.Recipe);
        Assert.Equal("Bitwarden", s.Rule.Id);
        Assert.Equal(["Bitwarden"], s.Rule.Processes);
        Assert.Null(s.Rule.Start);   // the recorded path stays right across updates
        Assert.Equal("--autostart", s.Rule.Args);
        Assert.True(s.Rule.Restore);
        Assert.DoesNotContain(Suggest(Scan(running, startup)), x => !x.IsRunning && x.Startup == startup);   // listed once
    }

    [Fact]
    public void Launcher_stub_is_kept_as_the_start_target()
    {
        var startup = Entry("Postman", $"\"{Local}\\Postman2\\Update.exe\" --processStart Postman2.exe");
        var running = Group([P($@"{Local}\Postman2\app-11.2.0\Postman2.exe")], startup: [startup]);

        var rule = Suggest(Scan(running, startup)).First(x => x.IsRunning).Rule;
        Assert.EndsWith(@"\Postman2\Update.exe", rule.Start, StringComparison.Ordinal);
        Assert.Equal("--processStart Postman2.exe", rule.Args);
    }

    [Fact]
    public void Packaged_app_restarts_through_its_app_id()
    {
        var proc = P($@"{Pf}\WindowsApps\Vendor.Thing_1.2.0.0_x64__abc\Thing.exe");
        var details = new Details();
        details.Windows.Add(proc.Id);
        details.Aumids[proc.Id] = "Vendor.Thing_abc!App";

        var rule = Suggest(Scan(Group([proc], details))).First(x => x.IsRunning).Rule;
        Assert.Equal(@"shell:AppsFolder\Vendor.Thing_abc!App", rule.Start);
    }

    [Fact]
    public void Recipe_wins_and_is_listed_once()
    {
        var running = Group(
        [
            P($@"{Local}\PowerToys\PowerToys.exe"),
            P($@"{Pf}\WindowsApps\Microsoft.CommandPalette_0.13.0.0_x64__8w\Microsoft.CmdPal.UI.exe"),
        ]);
        var spotifyStartup = Entry("Spotify", $"\"{Local}\\Spotify\\Spotify.exe\" --something-else");

        var all = Suggest(Scan(running, spotifyStartup));
        var powertoys = Assert.Single(all, s => s.Recipe?.Rule.Id == "PowerToys");
        Assert.True(powertoys.IsRunning);
        Assert.Equal("PowerToys", powertoys.Title);
        Assert.Equal("--autostart --minimized", Assert.Single(all, s => s.Recipe?.Rule.Id == "Spotify").Rule.Args);
    }

    [Fact]
    public void Sidekick_process_does_not_lend_its_start_details_to_the_recipe()
    {
        var cmdPal = P($@"{Pf}\WindowsApps\Microsoft.CommandPalette_0.13.0.0_x64__8w\Microsoft.CmdPal.UI.exe");
        var details = new Details();
        details.Aumids[cmdPal.Id] = "Microsoft.CommandPalette_8w!App";

        var powertoys = Suggest(Scan(Group([cmdPal], details))).Single(s => s.Recipe?.Rule.Id == "PowerToys");
        Assert.Null(powertoys.Rule.Start);
    }

    [Fact]
    public void Windowless_helpers_nobody_started_are_not_reopened_and_common_names_check_the_folder()
    {
        var running = Group([P($@"{Pf}\nodejs\node.exe")]);
        var rule = Suggest(Scan(running)).First(x => x.IsRunning).Rule;
        Assert.False(rule.Restore);
        Assert.Equal(@"*\nodejs\*", rule.PathLike);
    }

    [Fact]
    public void Knows_what_the_profile_already_has()
    {
        var running = Group([P($@"{Pf}\Tool\tool.exe"), P(@"C:\Users\me\.local\bin\claude.exe")]);
        var profile = new Profile
        {
            Name = "P",
            Apps = [new AppRule { Id = "Mine", Processes = ["tool"] }, new AppRule { Id = "W", Kind = AppKind.Wsl }],
        };

        var all = Suggest(Scan(running), profile);
        Assert.True(all.Single(s => s.App?.Main.Name == "tool").AlreadyAdded);
        Assert.False(all.Single(s => s.Recipe?.Rule.Id == "ClaudeCode").AlreadyAdded);
        Assert.True(all.Single(s => s.Recipe?.Rule.Id == "WSL").AlreadyAdded);   // same kind, different id
    }

    [Fact]
    public void Starter_profile_has_known_apps_found_here_and_protects_launchers()
    {
        var running = Group([P($@"{Local}\Programs\Microsoft VS Code\Code.exe"), P($@"{Pf}\Unknown\unknown.exe")]);
        var scan = Scan(running) with
        {
            InstalledRecipes = new HashSet<string>(["WSL", "Tailscale", "Spotify"], StringComparer.OrdinalIgnoreCase),
            ProcessNames = new HashSet<string>(["Code", "unknown", "steam", "steamwebhelper"], StringComparer.OrdinalIgnoreCase),
            Services = [new ServiceEntry("WSearch", "Windows Search", true, null, true), new ServiceEntry("Everything", "Everything", false, null, false)],
        };

        var config = Suggestions.Starter(scan, Catalog.BuiltIn, Ctx);
        ConfigStore.Validate(config);
        var profile = config.GetProfile(null);
        Assert.Equal(["WSL", "VSCode", "Tailscale"], profile.Apps.Select(a => a.Id));   // Spotify is installed but not in use
        Assert.Equal(["WSearch"], profile.Services.Select(s => s.Name));   // only services that are running
        Assert.Equal(["steam", "steamwebhelper"], config.NeverTouch);
    }

    [Fact]
    public void Ids_are_made_readable_and_unique()
    {
        Assert.Equal("VisualStudioCode", Suggestions.MakeId("Visual Studio Code"));
        Assert.Equal("AMDSoftwareHostApplication", Suggestions.MakeId("AMD Software: Host Application"));
        Assert.Equal("App", Suggestions.MakeId("—"));
        AppRule[] existing = [new() { Id = "Tool", Processes = ["a"] }, new() { Id = "Tool2", Processes = ["b"] }];
        Assert.Equal("Tool3", Suggestions.WithUniqueId(new AppRule { Id = "Tool", Processes = ["c"] }, existing).Id);
    }

    [Fact]
    public void Services_hide_what_games_need_and_show_third_party_ones_running()
    {
        var scan = new MachineScan
        {
            Services =
            [
                new ServiceEntry("vgc", "Vanguard", true, @"C:\Program Files\Riot Vanguard\vgc.exe", false),
                new ServiceEntry("Vendor", "Vendor thing", true, @"C:\Program Files\V\v.exe", false),
                new ServiceEntry("Stopped", "Stopped thing", false, @"C:\Program Files\S\s.exe", false),
                new ServiceEntry("Spooler", "Print Spooler", true, @"C:\Windows\System32\spoolsv.exe", true),
                new ServiceEntry("WSearch", "Windows Search", true, @"C:\Windows\System32\SearchIndexer.exe", true),
            ],
        };
        var profile = new Profile { Name = "P", Services = [new ServiceRule { Name = "vendor" }] };

        var list = Suggestions.Services(scan, Catalog.BuiltIn, profile);
        Assert.Equal(["Vendor", "WSearch"], list.Select(s => s.Rule.Name).Order());
        Assert.True(list.Single(s => s.Rule.Name == "Vendor").AlreadyAdded);
        Assert.Contains(Suggestions.Services(scan, Catalog.BuiltIn, profile, includeWindows: true), s => s.Rule.Name == "Spooler");
    }

    [Theory]
    [InlineData("WSL", "Shut down all distros · asks first")]
    [InlineData("VSCode", "Freeze, unfreeze afterwards")]
    [InlineData("Spotify", "Close, reopen with --autostart --minimized")]
    [InlineData("ClaudeUsageMonitor", "End, reopen")]
    [InlineData("Terminal", "Close, don't reopen · asks first")]
    [InlineData("LGMonitorApp", "Close, reopen · hides its window")]
    public void Describes_rules_in_plain_words(string id, string text)
    {
        Assert.Equal(text, RuleText.Describe(Catalog.BuiltIn.FindApp(id)!.Rule));
    }
}

public class CatalogTests
{
    static readonly Catalog C = Catalog.BuiltIn;

    [Fact]
    public void Every_recipe_is_a_valid_rule()
    {
        ConfigStore.Validate(new PlayModeConfig { Profiles = [new Profile { Name = "All", Apps = [.. C.Apps.Select(a => a.Rule)] }] });
        Assert.True(C.Apps.Count >= 30);
    }

    [Fact]
    public void Recipes_never_target_protected_or_leave_alone_processes()
    {
        var leaveAlone = C.LeaveAlone.SelectMany(e => e.Processes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in C.Apps.SelectMany(a => a.Rule.Processes))
        {
            Assert.DoesNotContain(name, ProcessMatcher.BuiltInNeverTouch);
            Assert.DoesNotContain(name, leaveAlone);
        }
    }

    [Fact]
    public void Services_named_by_apps_are_in_the_catalog_and_not_kept()
    {
        foreach (var name in C.Apps.SelectMany(a => a.Services))
            Assert.NotNull(C.FindService(name));
        Assert.Empty(C.Services.Select(s => s.Name).Intersect(C.KeepServices, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Leave_alone_suggestions_skip_what_is_already_protected()
    {
        var scan = new MachineScan
        {
            ProcessNames = new HashSet<string>(["Discord", "obs64"], StringComparer.OrdinalIgnoreCase),
            Startup = [new StartupEntry { Name = "Steam", Location = "Run", Command = "steam.exe", Exe = @"C:\S\steam.exe" }],
        };
        var names = Suggestions.LeaveAlone(scan, C, ["discord"]).Select(e => e.Name);
        Assert.Equal(["Steam", "OBS Studio"], names);
    }
}
