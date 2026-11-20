namespace PlayMode.Core.Model;

public enum Mode { Dev, Play }

/// <summary>An app PlayMode closed or suspended, with what it needs to bring it back.</summary>
public sealed record ClosedApp(string Id, string? Path)
{
    public IReadOnlyList<int> SuspendedPids { get; init; } = [];
}

/// <summary>Windows mouse acceleration ("Enhance pointer precision"): the SPI_GETMOUSE triple. Speed 0 means off.</summary>
public sealed record MouseParams(int Threshold1, int Threshold2, int Speed)
{
    public static MouseParams Off { get; } = new(0, 0, 0);
}

/// <summary>Persisted between switches (and across crashes and sign-ins).</summary>
public sealed class PlayState
{
    public Mode Mode { get; set; }
    public string? Profile { get; set; }
    public DateTimeOffset? Since { get; set; }
    public List<ClosedApp> Closed { get; set; } = [];
    public bool TailscaleWasUp { get; set; }

    /// <summary>Mouse settings from before play mode turned acceleration off; null when nothing was changed.</summary>
    public MouseParams? MouseBefore { get; set; }
    public string? LastReport { get; set; }
}
