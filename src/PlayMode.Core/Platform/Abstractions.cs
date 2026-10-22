using PlayMode.Core.Model;

namespace PlayMode.Core.Platform;

public sealed record ProcessInfo(int Id, string Name, string? Path, int SessionId, long WorkingSet);

public sealed record WindowInfo(nint Handle, string Title);

public sealed record ServiceResult(IReadOnlyList<string> Ok, IReadOnlyList<string> Failed, IReadOnlyList<string> Notes);

public interface IProcessSource
{
    int CurrentProcessId { get; }
    int CurrentSessionId { get; }
    IReadOnlyList<ProcessInfo> Snapshot();
    bool IsAlive(int pid);
}

public interface IProcessControl
{
    /// <summary>Ask nicely (WM_CLOSE to its windows).</summary>
    void RequestClose(int pid);
    void Kill(int pid);
    bool Suspend(int pid);
    bool Resume(int pid);

    /// <returns>Null on success, otherwise why it failed.</returns>
    string? Start(string target, string? args);
}

public interface IWindowControl
{
    /// <summary>Visible, titled top-level windows owned by these processes.</summary>
    IReadOnlyList<WindowInfo> VisibleWindows(IReadOnlyCollection<int> pids);
    void Close(nint handle);
    void Hide(nint handle);
}

public interface IServiceControl
{
    IReadOnlyList<string> Running(IEnumerable<string> names);

    /// <returns>Null when the elevated helper is not installed or timed out.</returns>
    Task<ServiceResult?> StopAsync(CancellationToken ct);
    Task<ServiceResult?> StartAsync(CancellationToken ct);
}

public interface ITailscale
{
    bool IsInstalled { get; }
    bool IsUp();
    bool Down();
    bool Up();
}

public interface IWsl
{
    IReadOnlyList<string> RunningDistros();
    bool Shutdown();
}

public interface ISystemInfo
{
    /// <summary>When the current user signed in. Unlike boot time, this resets on a Fast Startup power-on.</summary>
    DateTimeOffset? SessionLogonTime { get; }
}

public interface IStateStore
{
    PlayState Load();
    void Save(PlayState state);
}

public interface IClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

public interface ILog
{
    void Info(string message);
}

public sealed record PlatformServices(
    IProcessSource Processes,
    IProcessControl Control,
    IWindowControl Windows,
    IServiceControl Services,
    ITailscale Tailscale,
    IWsl Wsl,
    ISystemInfo System,
    IStateStore Store,
    IClock Clock,
    ILog Log);
