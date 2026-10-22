using System.Text.Json;
using System.Text.Json.Serialization;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Storage;

public static class AppPaths
{
    /// <summary>Machine-local state and logs.</summary>
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlayMode");

    /// <summary>User settings (roams with the profile).</summary>
    public static string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayMode");

    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogFile => Path.Combine(DataDir, "playmode.log");
    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
}

public static class PlayModeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Write via a temp file so a crash never leaves half a file behind.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class JsonStateStore(string path, ILog log) : IStateStore
{
    public PlayState Load()
    {
        if (!File.Exists(path))
            return new PlayState();
        try
        {
            return JsonSerializer.Deserialize<PlayState>(File.ReadAllText(path), PlayModeJson.Options) ?? new PlayState();
        }
        catch (JsonException e)
        {
            // Keep the evidence; it may hold the only record of what to restore.
            File.Copy(path, path + ".bad", overwrite: true);
            log.Info($"State file unreadable ({e.Message}); saved a copy as {path}.bad");
            return new PlayState();
        }
    }

    public void Save(PlayState state) => PlayModeJson.WriteAtomic(path, state);
}

public static class ConfigStore
{
    public static PlayModeConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<PlayModeConfig>(File.ReadAllText(path), PlayModeJson.Options)
            ?? throw new InvalidDataException($"{path} is empty.");
        Validate(config);
        return config;
    }

    public static void Save(string path, PlayModeConfig config)
    {
        Validate(config);
        PlayModeJson.WriteAtomic(path, config);
    }

    public static void Validate(PlayModeConfig config)
    {
        if (config.Profiles.Count == 0)
            throw new InvalidDataException("Config has no profiles.");
        foreach (var profile in config.Profiles)
        {
            var duplicate = profile.Apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new InvalidDataException($"Profile '{profile.Name}' lists app '{duplicate.Key}' twice.");
            var empty = profile.Apps.FirstOrDefault(a => a.Kind == AppKind.Process && a.Processes.Count == 0);
            if (empty is not null)
                throw new InvalidDataException($"App '{empty.Id}' in profile '{profile.Name}' has no process names.");
        }
        if (config.DefaultProfile is not null)
            config.GetProfile(config.DefaultProfile);
    }
}

public sealed class FileLog(string path, bool echo = false) : ILog
{
    readonly Lock _lock = new();

    public void Info(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        if (echo)
            Console.WriteLine(message);
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
