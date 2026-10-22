using PlayMode.Core.Model;
using PlayMode.Core.Storage;

namespace PlayMode.Core.Tests;

public sealed class StorageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "PlayModeTests", Guid.NewGuid().ToString("N"));
    readonly FakeOs _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void State_round_trips_with_times_kept_as_times()
    {
        // The PowerShell prototype was bitten by ConvertFrom-Json turning ISO strings into DateTime.
        var store = new JsonStateStore(Path.Combine(_dir, "state.json"), _log);
        var since = new DateTimeOffset(2026, 10, 22, 10, 3, 43, TimeSpan.FromHours(-6));
        store.Save(new PlayState
        {
            Mode = Mode.Play,
            Profile = "Gaming",
            Since = since,
            TailscaleWasUp = true,
            Closed = [new ClosedApp("Rider", @"C:\r.exe") { SuspendedPids = [42] }],
        });

        var loaded = store.Load();
        Assert.Equal(Mode.Play, loaded.Mode);
        Assert.Equal(since, loaded.Since);
        Assert.True(loaded.TailscaleWasUp);
        Assert.Equal([42], loaded.Closed.Single().SuspendedPids);
        Assert.Contains("\"mode\": \"Play\"", File.ReadAllText(Path.Combine(_dir, "state.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_state_means_dev_mode()
    {
        Assert.Equal(Mode.Dev, new JsonStateStore(Path.Combine(_dir, "none.json"), _log).Load().Mode);
    }

    [Fact]
    public void Corrupt_state_is_kept_aside_not_silently_lost()
    {
        var path = Path.Combine(_dir, "state.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");

        var loaded = new JsonStateStore(path, _log).Load();
        Assert.Equal(Mode.Dev, loaded.Mode);
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
        Assert.Contains(_log.Logs, l => l.Contains("unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_loads_with_comments_trailing_commas_and_any_enum_case()
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {
              // personal setup
              "profiles": [{
                "name": "Gaming",
                "apps": [
                  { "id": "WSL", "kind": "wsl", "risky": true },
                  { "id": "Handy", "processes": ["handy"], "window": "Close", "windowTitle": "Handy" },
                ],
                "services": [{ "name": "WSearch" }, { "name": "ProtonVPN Service", "restart": false }]
              }]
            }
            """);

        var config = ConfigStore.Load(path);
        var profile = config.GetProfile(null);
        Assert.Equal(AppKind.Wsl, profile.Apps[0].Kind);
        Assert.Equal(WindowAction.Close, profile.Apps[1].Window);
        Assert.True(profile.Apps[1].Restore);   // default kept when not specified
        Assert.False(profile.Services[1].Restart);
    }

    [Theory]
    [InlineData("""{ "profiles": [] }""", "no profiles")]
    [InlineData("""{ "profiles": [{ "name": "A", "apps": [{ "id": "x", "processes": ["a"] }, { "id": "X", "processes": ["b"] }] }] }""", "twice")]
    [InlineData("""{ "profiles": [{ "name": "A", "apps": [{ "id": "x" }] }] }""", "no process names")]
    [InlineData("""{ "defaultProfile": "B", "profiles": [{ "name": "A" }] }""", "No profile named 'B'")]
    public void Rejects_broken_configs_with_a_useful_message(string json, string expected)
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, json);
        var error = Assert.ThrowsAny<Exception>(() => ConfigStore.Load(path));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "profiles": [ { "name": "A" }, { "name": "a" } ] }""", "two profiles called")]
    [InlineData("""{ "profiles": [ { "name": " " } ] }""", "has no name")]
    [InlineData("""{ "profiles": [ { "name": "A", "apps": [ { "id": "", "processes": ["x"] } ] } ] }""", "An app in profile 'A' has no name")]
    public void Config_rejects_what_the_settings_window_could_produce_by_mistake(string json, string error)
    {
        var path = Path.Combine(_dir, "bad.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, json);
        Assert.Contains(error, Assert.Throws<InvalidDataException>(() => ConfigStore.Load(path)).Message, StringComparison.Ordinal);
    }
}