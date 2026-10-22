namespace PlayMode.Core.Model;

public enum Mode { Dev, Play }

/// <summary>An app PlayMode closed or suspended, with what it needs to bring it back.</summary>
public sealed record ClosedApp(string Id, string? Path)
{
    public IReadOnlyList<int> SuspendedPids { get; init; } = [];
}

/// <summary>Persisted between switches (and across crashes and sign-ins).</summary>
public sealed class PlayState
{
    public Mode Mode { get; set; }
    public string? Profile { get; set; }
    public DateTimeOffset? Since { get; set; }
    public List<ClosedApp> Closed { get; set; } = [];
    public bool TailscaleWasUp { get; set; }
    public string? LastReport { get; set; }
}
