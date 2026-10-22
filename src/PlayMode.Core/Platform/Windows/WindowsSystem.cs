using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using PlayMode.Core.Engine;

namespace PlayMode.Core.Platform.Windows;

internal static class CliRunner
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public string Output => Stdout + Stderr;
    }

    public static Result Run(string exe, IEnumerable<string> args, TimeSpan timeout, IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
            info.Environment[key] = value;

        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new Result(-1, "", $"{Path.GetFileName(exe)} timed out");
        }
        p.WaitForExit();   // drain redirected output
        return new Result(p.ExitCode, stdout.Result, stderr.Result);
    }
}

public sealed class TailscaleCli(ILog log, string? exePath = null) : ITailscale
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    readonly string _exe = exePath
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");

    public bool IsInstalled => File.Exists(_exe);

    public bool IsUp()
    {
        if (!IsInstalled)
            return false;
        var result = CliRunner.Run(_exe, ["status", "--json"], Timeout);
        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            return doc.RootElement.TryGetProperty("BackendState", out var state) && state.GetString() == "Running";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool Down() => CliRunner.Run(_exe, ["down"], Timeout).ExitCode == 0;

    public bool Up()
    {
        var first = CliRunner.Run(_exe, ["up", "--timeout=30s"], Timeout);
        if (first.ExitCode == 0)
            return true;
        var flags = TailscaleFlags.ParseSuggestedUp(first.Output);
        if (flags is null)
        {
            log.Info($"tailscale up failed: {first.Output.Trim()}");
            return false;
        }
        log.Info($"tailscale up needs flags, retrying with: {string.Join(' ', flags)}");
        return CliRunner.Run(_exe, ["up", .. flags], Timeout).ExitCode == 0;
    }
}

public sealed class WslCli : IWsl
{
    static readonly string Exe = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public IReadOnlyList<string> RunningDistros()
    {
        if (!File.Exists(Exe))
            return [];
        // Without WSL_UTF8, wsl.exe prints UTF-16.
        var result = CliRunner.Run(Exe, ["-l", "--running", "-q"], TimeSpan.FromSeconds(10), new Dictionary<string, string> { ["WSL_UTF8"] = "1" });
        return result.ExitCode != 0 ? [] : result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool Shutdown() => File.Exists(Exe) && CliRunner.Run(Exe, ["--shutdown"], TimeSpan.FromSeconds(60)).ExitCode == 0;
}

/// <summary>Reads service state directly; stopping/starting goes through the elevated helper.</summary>
public sealed class WindowsServiceControl(Func<string, CancellationToken, Task<ServiceResult?>>? helper, ILog log) : IServiceControl
{
    public IReadOnlyList<string> Running(IEnumerable<string> names)
    {
        var running = new List<string>();
        foreach (var name in names)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status == ServiceControllerStatus.Running)
                    running.Add(name);
            }
            catch (InvalidOperationException)
            {
                // Not installed on this machine.
            }
        }
        return running;
    }

    public Task<ServiceResult?> StopAsync(CancellationToken ct) => Run("Stop", ct);

    public Task<ServiceResult?> StartAsync(CancellationToken ct) => Run("Start", ct);

    Task<ServiceResult?> Run(string action, CancellationToken ct)
    {
        if (helper is null)
        {
            log.Info($"Service helper not installed; skipping service {action.ToLowerInvariant()}");
            return Task.FromResult<ServiceResult?>(null);
        }
        return helper(action, ct);
    }
}

public sealed class WindowsSystemInfo : ISystemInfo
{
    const int WTSSessionInfo = 24;
    const int WTSInfoSize = 216;          // sizeof(WTSINFOW) on x64
    const int LogonTimeOffset = 200;      // WTSINFOW.LogonTime

    public DateTimeOffset? SessionLogonTime
    {
        get
        {
            if (!Native.WTSQuerySessionInformation(0, -1, WTSSessionInfo, out var buffer, out var bytes) || buffer == 0)
                return null;
            try
            {
                if (bytes < WTSInfoSize)
                    return null;
                var fileTime = Marshal.ReadInt64(buffer, LogonTimeOffset);
                return fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null;
            }
            finally
            {
                Native.WTSFreeMemory(buffer);
            }
        }
    }
}
