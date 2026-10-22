using System.Text.Json;

namespace PlayMode.Core.Storage;

/// <summary>Small per-machine choices made in the tray (not part of the shareable config).</summary>
public sealed record UiSettings
{
    /// <summary>The play profile picked in the tray; null means the config's default.</summary>
    public string? Profile { get; init; }

    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static UiSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), PlayModeJson.Options) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save() => PlayModeJson.WriteAtomic(FilePath, this);
}
