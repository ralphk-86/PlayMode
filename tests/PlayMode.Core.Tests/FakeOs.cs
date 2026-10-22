using System.Text.Json;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;
using PlayMode.Core.Storage;

namespace PlayMode.Core.Tests;

sealed class FakeProc
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public int Session { get; init; } = 1;
    public long WorkingSet { get; init; } = 100L * 1024 * 1024;
    public bool Alive { get; set; } = true;
    /// <summary>Like Windows, suspends nest: each one needs its own resume.</summary>
    public int SuspendCount { get; set; }
    public bool Suspended => SuspendCount > 0;
    public bool IgnoresClose { get; init; }
    public bool Unkillable { get; init; }
    public List<FakeWindow> Windows { get; } = [];
}

sealed class FakeWindow(nint handle, string title)
{
    public nint Handle { get; } = handle;
    public string Title { get; } = title;
    public bool Visible { get; set; } = true;
}

/// <summary>An in-memory Windows: processes, windows, services, Tailscale, WSL, clock and state.</summary>
sealed class FakeOs : IProcessSource, IProcessControl, IWindowControl, IServiceControl, ITailscale, IWsl, ISystemInfo, IStateStore, IClock, ILog
{
    int _nextPid = 1000;
    nint _nextWindow = 1;
    string _stateJson = JsonSerializer.Serialize(new PlayState(), PlayModeJson.Options);

    public List<FakeProc> Procs { get; } = [];
    public List<int> CloseRequests { get; } = [];
    public List<int> Kills { get; } = [];
    public List<(string Target, string? Args)> Starts { get; } = [];
    public List<nint> ClosedWindows { get; } = [];
    public List<nint> HiddenWindows { get; } = [];
    public List<string> Logs { get; } = [];

    /// <summary>What a Start(target) brings up: process name and window titles.</summary>
    public Dictionary<string, (string Name, string[] Windows)> Launchers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Processes that come back by themselves right after being killed.</summary>
    public HashSet<string> Respawners { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RunningServices { get; } = [];
    public ServiceResult? StopResult { get; set; }
    public ServiceResult? StartResult { get; set; }
    public int StopCalls { get; private set; }
    public int StartCalls { get; private set; }

    public bool TailscaleInstalled { get; set; } = true;
    public bool TailscaleUp { get; set; }
    public bool TailscaleUpFails { get; set; }
    public int TailscaleDownCalls { get; private set; }
    public int TailscaleUpCalls { get; private set; }

    public int WslShutdowns { get; private set; }

    public DateTimeOffset? SessionLogonTime { get; set; } = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public PlatformServices Platform => new(this, this, this, this, this, this, this, this, this, this);

    public FakeProc Add(string name, string? path = null, int session = 1, bool ignoresClose = false, bool unkillable = false, params string[] windows)
    {
        var proc = new FakeProc { Id = _nextPid++, Name = name, Path = path ?? $@"C:\Apps\{name}\{name}.exe", Session = session, IgnoresClose = ignoresClose, Unkillable = unkillable };
        foreach (var title in windows)
            proc.Windows.Add(new FakeWindow(_nextWindow++, title));
        Procs.Add(proc);
        return proc;
    }

    public FakeProc? Find(string name) => Procs.FirstOrDefault(p => p.Alive && p.Name == name);
    public int Count(string name) => Procs.Count(p => p.Alive && p.Name == name);

    // IProcessSource
    public int CurrentProcessId => 1;
    public int CurrentSessionId => 1;
    public IReadOnlyList<ProcessInfo> Snapshot() =>
        [.. Procs.Where(p => p.Alive).Select(p => new ProcessInfo(p.Id, p.Name, p.Path, p.Session, p.WorkingSet))];
    public bool IsAlive(int pid) => Procs.Any(p => p.Id == pid && p.Alive);

    // IProcessControl
    public void RequestClose(int pid)
    {
        CloseRequests.Add(pid);
        var p = Procs.Single(x => x.Id == pid);
        if (!p.IgnoresClose)
            Exit(p);
    }

    public void Kill(int pid)
    {
        Kills.Add(pid);
        var p = Procs.Single(x => x.Id == pid);
        if (!p.Unkillable)
            Exit(p);
    }

    void Exit(FakeProc p)
    {
        if (!p.Alive)
            return;
        p.Alive = false;
        if (Respawners.Contains(p.Name))
            Add(p.Name, p.Path, p.Session);
    }

    public bool Suspend(int pid) => Adjust(pid, +1);
    public bool Resume(int pid) => Adjust(pid, -1);

    bool Adjust(int pid, int delta)
    {
        var p = Procs.FirstOrDefault(x => x.Id == pid && x.Alive);
        if (p is null)
            return false;
        p.SuspendCount = Math.Max(0, p.SuspendCount + delta);
        return true;
    }

    public string? Start(string target, string? args)
    {
        Starts.Add((target, args));
        if (target.Contains("missing", StringComparison.OrdinalIgnoreCase))
            return $"not found: {target}";
        var (name, windows) = Launchers.TryGetValue(target, out var launcher)
            ? launcher
            : (System.IO.Path.GetFileNameWithoutExtension(target), Array.Empty<string>());
        Add(name, target, windows: windows);
        return null;
    }

    // IWindowControl
    public IReadOnlyList<WindowInfo> VisibleWindows(IReadOnlyCollection<int> pids) =>
        [.. Procs.Where(p => p.Alive && pids.Contains(p.Id)).SelectMany(p => p.Windows).Where(w => w.Visible).Select(w => new WindowInfo(w.Handle, w.Title))];

    public void Close(nint handle)
    {
        ClosedWindows.Add(handle);
        Procs.SelectMany(p => p.Windows).Single(w => w.Handle == handle).Visible = false;
    }

    public void Hide(nint handle)
    {
        HiddenWindows.Add(handle);
        Procs.SelectMany(p => p.Windows).Single(w => w.Handle == handle).Visible = false;
    }

    // IServiceControl
    public IReadOnlyList<string> Running(IEnumerable<string> names) => [.. names.Where(RunningServices.Contains)];
    public List<string> StopRequested { get; } = [];
    public Task<ServiceResult?> StopAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        StopCalls++;
        StopRequested.AddRange(names);
        return Task.FromResult(StopResult);
    }
    public Task<ServiceResult?> StartAsync(CancellationToken ct) { StartCalls++; return Task.FromResult(StartResult); }

    // ITailscale
    public bool IsInstalled => TailscaleInstalled;
    public bool IsUp() => TailscaleInstalled && TailscaleUp;
    public bool Down() { TailscaleDownCalls++; TailscaleUp = false; return true; }
    public bool Up() { TailscaleUpCalls++; TailscaleUp = !TailscaleUpFails; return TailscaleUp; }

    // IWsl
    public IReadOnlyList<string> RunningDistros() => Find("vmmemWSL") is null ? [] : ["Ubuntu-24.04"];
    public bool Shutdown()
    {
        WslShutdowns++;
        foreach (var p in Procs.Where(p => p.Name == "vmmemWSL"))
            p.Alive = false;
        return true;
    }

    // IStateStore: round-trips through JSON like the real store, so serialization bugs show up here.
    public PlayState Load() => JsonSerializer.Deserialize<PlayState>(_stateJson, PlayModeJson.Options)!;
    public void Save(PlayState state) => _stateJson = JsonSerializer.Serialize(state, PlayModeJson.Options);

    // IClock: time only moves when someone waits.
    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        Now += delay;
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan by) => Now += by;

    // ILog
    public void Info(string message) => Logs.Add(message);
}
