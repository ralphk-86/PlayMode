using System.Reflection;
using PlayMode.Core.Discovery;
using PlayMode.Core.Engine;
using PlayMode.Core.Ipc;
using PlayMode.Core.Model;
using PlayMode.Core.Platform.Windows;
using PlayMode.Core.Storage;

const string Usage = """
    usage: playmodectl <command> [options]

    commands:
      status                 current mode, last report, and what is running from the profile
      on                     switch to the play profile
      off                    switch back to dev mode, restoring what was running
      tray <on|off|toggle|settings|exit>
                             tell the running tray app to do it (shows its usual prompts and notifications)
      scan [--verbose] [--all]
                             what PlayMode finds on this machine: apps, startup items, services
      setup                  install or update service control (asks for admin once)
      remove-helper          uninstall service control (asks for admin)
      version                PlayMode version

    options:
      --profile <name>       profile to use (default: the one picked in the tray, else the config default)
      --config <path>        config file (default: %APPDATA%\PlayMode\config.json)
      --exclude <ids>        comma-separated app ids to leave alone this time
      --dry-run              show what would happen, change nothing
      --yes                  don't ask before closing apps marked risky
    """;

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var command = positional.FirstOrDefault()?.ToLowerInvariant() ?? "status";
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains(name);

if (command is "help" or "-h" || Flag("--help"))
{
    Console.WriteLine(Usage);
    return 0;
}

if (command == "version" || Flag("--version"))
{
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Console.WriteLine($"PlayMode {version?.Split('+')[0] ?? "unknown"}");
    return 0;
}

if (command == "tray")
{
    var signal = positional.ElementAtOrDefault(1);
    if (signal is null || !TraySignals.Names.Contains(signal, StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("usage: playmodectl tray <on|off|toggle|settings|exit>");
        return 2;
    }
    if (TraySignals.Send(signal))
        return 0;
    Console.Error.WriteLine("The PlayMode tray app isn't running.");
    return 1;
}

if (command == "remove-helper")
{
    var error = ServiceHelperClient.RunUninstall();
    Console.WriteLine(error ?? "Service control removed.");
    return error is null ? 0 : 1;
}

var configPath = Option("--config") ?? AppPaths.ConfigFile;

if (command == "scan")
{
    var current = File.Exists(configPath) ? ConfigStore.Load(configPath) : null;
    var scan = WindowsDiscovery.Scan(current, Catalog.BuiltIn);
    var compare = current?.GetProfile(Option("--profile")) ?? new Profile { Name = "(none)" };
    var ctx = new MatchContext(Environment.ProcessId, System.Diagnostics.Process.GetCurrentProcess().SessionId, current?.NeverTouch ?? []);
    Console.WriteLine($"Comparing with profile: {compare.Name}");
    Console.WriteLine();
    foreach (var s in Suggestions.Apps(scan, Catalog.BuiltIn, compare, ctx).Where(s => s.IsRunning || s.Startup is not null || s.Installed))
    {
        var where = string.Join(", ", new[] { s.IsRunning ? "running" : null, s.Startup is not null ? "startup" : null, s.Installed ? "installed" : null }.OfType<string>());
        Console.WriteLine($"{(s.AlreadyAdded ? "  added " : "        ")}{s.Title,-34} {s.Category,-9} {where,-26} {RuleText.Describe(s.Rule)}");
        if (Flag("--verbose"))
        {
            Console.WriteLine($"          rule: {System.Text.Json.JsonSerializer.Serialize(s.Rule, PlayModeJson.Options).ReplaceLineEndings(" ")}");
            if (s.App is { } app)
                Console.WriteLine($"          main: {app.Main.Path}  folder: {app.Folder ?? "-"}  aumid: {app.AppUserModelId ?? "-"}  {app.WorkingSet / 1048576} MB");
        }
    }
    Console.WriteLine();
    Console.WriteLine("Services:");
    foreach (var s in Suggestions.Services(scan, Catalog.BuiltIn, compare, includeWindows: Flag("--all")))
        Console.WriteLine($"{(s.AlreadyAdded ? "  added " : "        ")}{s.Rule.Name,-34} {s.Title,-44} {(s.Running ? "running" : "stopped")}{(s.InCatalog ? " (catalog)" : "")}");
    Console.WriteLine();
    Console.WriteLine($"Leave alone: {string.Join(", ", Suggestions.LeaveAlone(scan, Catalog.BuiltIn, current?.NeverTouch ?? []).Select(e => e.Name))}");
    return 0;
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"No config at {configPath}. Open PlayMode's settings from the tray to create one.");
    return 2;
}

PlayModeConfig config;
try
{
    config = ConfigStore.Load(configPath);
}
catch (Exception e) when (e is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Config error: {e.Message}");
    return 2;
}

if (command == "setup")
{
    var error = ServiceHelperClient.RunSetup(config);
    Console.WriteLine(error ?? $"Service control is set up for: {string.Join(", ", ServiceHelperClient.AllowlistFor(config).Select(s => s.Name))}");
    return error is null ? 0 : 1;
}

var exclude = (Option("--exclude") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (exclude.Length > 0)
{
    config = config with
    {
        Profiles = [.. config.Profiles.Select(p => p with { Apps = [.. p.Apps.Where(a => !exclude.Contains(a.Id, StringComparer.OrdinalIgnoreCase))] })],
    };
}

var engine = new PlayModeEngine(WindowsPlatform.Create(echoLog: true), config);
var picked = UiSettings.Load().Profile;
var profile = Option("--profile") ?? (config.Profiles.Any(p => p.Name == picked) ? picked : null);

switch (command)
{
    case "status":
        var state = engine.State;
        Console.WriteLine($"Mode: {state.Mode}{(state.Mode == Mode.Play ? $" ({state.Profile}) since {state.Since:g}" : "")}");
        if (state.Closed.Count > 0)
            Console.WriteLine($"Closed: {string.Join(", ", state.Closed.Select(c => c.Id))}");
        if (state.LastReport is not null)
            Console.WriteLine($"Last: {state.LastReport}");
        Console.WriteLine($"Service control: {ServiceHelperClient.SetupNeeded(config) ?? "ready"}");
        Console.WriteLine($"Running from '{config.GetProfile(profile).Name}':");
        Console.WriteLine(engine.BuildPlan(profile).Describe());
        return 0;

    case "on":
        if (Flag("--dry-run"))
        {
            Console.WriteLine("Would affect:");
            Console.WriteLine(engine.BuildPlan(profile).Describe());
            return 0;
        }
        var report = await engine.EnableAsync(profile, Flag("--yes") ? null : text =>
        {
            Console.WriteLine(text);
            Console.WriteLine("Re-run with --yes to proceed.");
            return Task.FromResult(false);
        });
        return report is null ? 1 : 0;

    case "off":
        if (Flag("--dry-run"))
        {
            var closed = engine.State.Closed;
            Console.WriteLine(closed.Count == 0 ? "Nothing recorded to restore." : "Would restore: " + string.Join(", ", closed.Select(c => c.Id)));
            return 0;
        }
        await engine.DisableAsync();
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine(Usage);
        return 2;
}
