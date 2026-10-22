using PlayMode.Core.Platform;

namespace PlayMode.Core.Discovery;

/// <summary>An app as the user thinks of it: its processes grouped by install folder.</summary>
public sealed record RunningApp
{
    public required string Title { get; init; }

    /// <summary>Main process first.</summary>
    public required IReadOnlyList<ProcessInfo> Processes { get; init; }

    /// <summary>Install folder with version folders cut off; null for a lone exe in a shared folder.</summary>
    public string? Folder { get; init; }

    /// <summary>A wildcard for the install folder that works on other machines, e.g. <c>*\Proton\VPN\*</c>.</summary>
    public required string FolderLike { get; init; }

    public bool HasWindow { get; init; }

    /// <summary>Packaged (Store/MSIX) apps can only be started through their app id.</summary>
    public string? AppUserModelId { get; init; }

    public StartupEntry? Startup { get; init; }

    public ProcessInfo Main => Processes[0];

    public IReadOnlyList<string> ProcessNames =>
        [.. Processes.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    public long WorkingSet => Processes.Sum(p => p.WorkingSet);
}

/// <summary>Something Windows starts at sign-in: a Run key value or a Startup folder item.</summary>
public sealed record StartupEntry
{
    public required string Name { get; init; }

    /// <summary>Where it's registered, for display ("Run (user)", "Startup folder").</summary>
    public required string Location { get; init; }

    public required string Command { get; init; }
    public string? Exe { get; init; }
    public string? Args { get; init; }

    /// <summary>For Startup folder shortcuts: the .lnk itself, which is what should be started again.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Turned off in Task Manager's Startup apps.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The process a launcher stub starts, e.g. Discord.exe for <c>Update.exe --processStart Discord.exe</c>.</summary>
    public string? Launches { get; init; }

    /// <summary>The process name this entry ends up running.</summary>
    public string? ProcessName =>
        Launches ?? (Exe is null ? null : Path.GetFileNameWithoutExtension(Exe));
}

public sealed record ServiceEntry(string Name, string DisplayName, bool Running, string? ExePath, bool IsWindows);

/// <summary>What's on this machine right now, for building suggestions.</summary>
public sealed record MachineScan
{
    public IReadOnlyList<RunningApp> Running { get; init; } = [];
    public IReadOnlyList<StartupEntry> Startup { get; init; } = [];
    public IReadOnlyList<ServiceEntry> Services { get; init; } = [];

    /// <summary>Process names running in this session, including ones not offered as apps.</summary>
    public IReadOnlySet<string> ProcessNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Catalog app ids whose detect paths exist.</summary>
    public IReadOnlySet<string> InstalledRecipes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool WslRunning { get; init; }
    public bool TailscaleUp { get; init; }
}
