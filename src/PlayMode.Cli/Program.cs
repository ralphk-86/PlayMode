using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Platform.Windows;
using PlayMode.Core.Storage;

const string Usage = """
    usage: playmode <command> [options]

    commands:
      status            current mode, last report, and what is running from the profile
      on                switch to the play profile
      off               switch back to dev mode, restoring what was running

    options:
      --profile <name>  profile to use (default: the config's default profile)
      --config <path>   config file (default: %APPDATA%\PlayMode\config.json)
      --exclude <ids>   comma-separated app ids to leave alone this time
      --dry-run         show what would happen, change nothing
      --yes             don't ask before closing apps marked risky
    """;

var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant() ?? "status";
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains(name);

if (command is "help" or "-h" or "--help" || Flag("--help"))
{
    Console.WriteLine(Usage);
    return 0;
}

var configPath = Option("--config") ?? AppPaths.ConfigFile;
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"No config at {configPath}. See samples/config.example.json.");
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

var exclude = (Option("--exclude") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (exclude.Length > 0)
{
    config = config with
    {
        Profiles = [.. config.Profiles.Select(p => p with { Apps = [.. p.Apps.Where(a => !exclude.Contains(a.Id, StringComparer.OrdinalIgnoreCase))] })],
    };
}

var engine = new PlayModeEngine(WindowsPlatform.Create(echoLog: true), config);
var profile = Option("--profile");

switch (command)
{
    case "status":
        var state = engine.State;
        Console.WriteLine($"Mode: {state.Mode}{(state.Mode == Mode.Play ? $" ({state.Profile}) since {state.Since:g}" : "")}");
        if (state.Closed.Count > 0)
            Console.WriteLine($"Closed: {string.Join(", ", state.Closed.Select(c => c.Id))}");
        if (state.LastReport is not null)
            Console.WriteLine($"Last: {state.LastReport}");
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
