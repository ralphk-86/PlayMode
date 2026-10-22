namespace PlayMode.Core.Model;

public enum AppKind { Process, Wsl, Tailscale }

/// <summary>What happens to a matched app when PlayMode switches on.</summary>
public enum CloseAction { Close, Suspend }

/// <summary>What to do with a window that a restored app pops up.</summary>
public enum WindowAction { None, Close, Hide }

/// <summary>One app PlayMode manages, and how to get it back afterwards.</summary>
public sealed record AppRule
{
    public required string Id { get; init; }
    public AppKind Kind { get; init; }

    /// <summary>Process names (no .exe), wildcards allowed. The first is the main process.</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];

    /// <summary>Only match processes whose image path is like this wildcard.</summary>
    public string? PathLike { get; init; }

    /// <summary>What to launch on restore (exe, .lnk or shell:AppsFolder\…). Defaults to the recorded main exe.</summary>
    public string? Start { get; init; }
    public string? Args { get; init; }

    public bool Restore { get; init; } = true;

    /// <summary>Ask before closing: live work may be lost.</summary>
    public bool Risky { get; init; }

    /// <summary>Skip the polite close; for apps that relaunch themselves when asked to quit.</summary>
    public bool Force { get; init; }

    public CloseAction Action { get; init; }
    public WindowAction Window { get; init; }
    public string? WindowTitle { get; init; }
}

public sealed record ServiceRule
{
    public required string Name { get; init; }

    /// <summary>False for services an app restarts on its own (e.g. VPN clients).</summary>
    public bool Restart { get; init; } = true;
}

public sealed record Profile
{
    public required string Name { get; init; }
    public IReadOnlyList<AppRule> Apps { get; init; } = [];
    public IReadOnlyList<ServiceRule> Services { get; init; } = [];
    public bool ConfirmRisky { get; init; } = true;
}

public sealed record PlayModeConfig
{
    public IReadOnlyList<Profile> Profiles { get; init; } = [];
    public string? DefaultProfile { get; init; }

    /// <summary>Extra process names that are never touched, on top of the built-in list.</summary>
    public IReadOnlyList<string> NeverTouch { get; init; } = [];

    public Profile GetProfile(string? name)
    {
        name ??= DefaultProfile;
        if (name is null)
            return Profiles.Count > 0 ? Profiles[0] : throw new InvalidOperationException("No profiles configured.");
        return Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No profile named '{name}'.");
    }
}
