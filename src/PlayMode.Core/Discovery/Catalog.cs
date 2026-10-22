using System.Text.Json;
using PlayMode.Core.Model;
using PlayMode.Core.Storage;

namespace PlayMode.Core.Discovery;

/// <summary>A known app and the rule that handles it well.</summary>
public sealed record CatalogApp
{
    public required string Name { get; init; }
    public string Category { get; init; } = "Other";
    public string? Notes { get; init; }

    /// <summary>Paths (environment variables allowed) that mean the app is installed.</summary>
    public IReadOnlyList<string> Detect { get; init; } = [];

    /// <summary>Names of catalog services that belong with this app.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    public required AppRule Rule { get; init; }
}

public sealed record CatalogService
{
    public required string Name { get; init; }
    public string? Title { get; init; }
    public bool Restart { get; init; } = true;
    public string? Notes { get; init; }

    public ServiceRule ToRule() => new() { Name = Name, Restart = Restart };
}

public sealed record LeaveAloneEntry
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Processes { get; init; } = [];
}

public sealed record IgnoreList
{
    public IReadOnlyList<string> Processes { get; init; } = [];
    public IReadOnlyList<string> Paths { get; init; } = [];
}

public sealed record Catalog
{
    public IReadOnlyList<CatalogApp> Apps { get; init; } = [];
    public IReadOnlyList<CatalogService> Services { get; init; } = [];
    public IReadOnlyList<string> KeepServices { get; init; } = [];
    public IReadOnlyList<LeaveAloneEntry> LeaveAlone { get; init; } = [];
    public IgnoreList Ignore { get; init; } = new();

    static readonly Lazy<Catalog> Embedded = new(() =>
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("catalog.json")
            ?? throw new InvalidOperationException("The app catalog is missing from the build.");
        return JsonSerializer.Deserialize<Catalog>(stream, PlayModeJson.Options)
            ?? throw new InvalidDataException("The app catalog is empty.");
    });

    public static Catalog BuiltIn => Embedded.Value;

    public CatalogApp? FindApp(string id) =>
        Apps.FirstOrDefault(a => string.Equals(a.Rule.Id, id, StringComparison.OrdinalIgnoreCase));

    public CatalogService? FindService(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
