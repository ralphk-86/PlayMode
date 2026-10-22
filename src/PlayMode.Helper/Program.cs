using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.Json;
using PlayMode.Shared;

namespace PlayMode.Helper;

/// <summary>
/// The only part of PlayMode that runs with admin rights. It lives in Program Files (admin-only write) and is
/// started by two on-demand scheduled tasks running as SYSTEM, which only the installing user may trigger.
/// Whatever a request asks for, it only stops and starts services in the allowlist stored next to it.
/// </summary>
static class Program
{
    static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(25);
    const int MaxRequestBytes = 64 * 1024;

    static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "stop" => Run(HelperAction.Stop),
                "start" => Run(HelperAction.Start),
                "install" => Installer.Install(Arg(args, "--allowlist"), Arg(args, "--user")),
                "uninstall" => Installer.Uninstall(),
                _ => 2,
            };
        }
        catch (Exception e)
        {
            Log($"fatal: {e}");
            return 1;
        }
    }

    static int Run(HelperAction action)
    {
        var allowlist = ReadJson<List<AllowedService>>(HelperPaths.Allowlist) ?? [];
        var request = ReadRequest();
        var ok = new List<string>();
        var failed = new List<string>();
        var notes = new List<string>();

        if (request is null || request.Action != action)
        {
            failed.Add($"Ignored a malformed or mismatched request for {action}");
            WriteResult(new HelperResult(request?.Id ?? Guid.Empty, action, ok, failed, notes));
            return 1;
        }

        try
        {
            if (action == HelperAction.Stop)
                Stop(request, allowlist, ok, failed, notes);
            else
                Start(allowlist, ok, failed);
        }
        catch (Exception e)
        {
            // Always answer: the app is waiting for this result.
            Log($"{action} failed: {e}");
            failed.Add($"Service helper error: {e.Message}");
        }

        WriteResult(new HelperResult(request.Id, action, ok, failed, notes));
        Log($"{action}: ok=[{string.Join(", ", ok)}] failed=[{string.Join("; ", failed)}]");
        return 0;
    }

    static void Stop(HelperRequest request, List<AllowedService> allowlist, List<string> ok, List<string> failed, List<string> notes)
    {
        var allowed = allowlist.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Services.Where(n => !allowed.Contains(n)))
            failed.Add($"{name}: not approved for PlayMode (run setup again)");

        var running = new List<ServiceController>();
        foreach (var name in request.Services.Where(allowed.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var service = new ServiceController(name);
            try
            {
                if (service.Status == ServiceControllerStatus.Running)
                {
                    running.Add(service);
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                // Not installed on this machine.
            }
            service.Dispose();
        }

        // Ask all to stop first, then wait, so slow ones overlap.
        var errors = new Dictionary<string, string>();
        foreach (var service in running)
        {
            try { service.Stop(); }
            catch (InvalidOperationException e) { errors[service.ServiceName] = e.InnerException?.Message ?? e.Message; }
        }
        foreach (var service in running)
        {
            using (service)
            {
                if (errors.TryGetValue(service.ServiceName, out var error))
                    failed.Add($"{service.ServiceName}: {error}");
                else if (WaitFor(service, ServiceControllerStatus.Stopped))
                    ok.Add(service.ServiceName);
                else
                    failed.Add($"{service.ServiceName}: did not stop within {ServiceTimeout.TotalSeconds:0}s");
            }
        }

        var previous = ReadJson<List<string>>(HelperPaths.State) ?? [];
        WriteJson(HelperPaths.State, previous.Union(ok, StringComparer.OrdinalIgnoreCase).ToList());

        // Proton's kill switch can leave the firewall blocking traffic if a tunnel dies uncleanly.
        if (ok.Any(n => n.StartsWith("ProtonVPN", StringComparison.OrdinalIgnoreCase)))
            CheckInternetAfterProton(notes);
    }

    static void Start(List<AllowedService> allowlist, List<string> ok, List<string> failed)
    {
        var restartable = allowlist.Where(a => a.Restart).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stopped = ReadJson<List<string>>(HelperPaths.State) ?? [];
        var starting = new List<ServiceController>();
        foreach (var name in stopped.Where(restartable.Contains))
        {
            var service = new ServiceController(name);
            try
            {
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    starting.Add(service);
                    continue;
                }
            }
            catch (InvalidOperationException e)
            {
                failed.Add($"{name}: {e.InnerException?.Message ?? e.Message}");
            }
            service.Dispose();
        }
        foreach (var service in starting)
        {
            using (service)
            {
                if (WaitFor(service, ServiceControllerStatus.Running))
                    ok.Add(service.ServiceName);
                else
                    failed.Add($"{service.ServiceName}: did not start within {ServiceTimeout.TotalSeconds:0}s");
            }
        }
        File.Delete(HelperPaths.State);
    }

    static bool WaitFor(ServiceController service, ServiceControllerStatus status)
    {
        try
        {
            service.WaitForStatus(status, ServiceTimeout);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }

    static void CheckInternetAfterProton(List<string> notes)
    {
        Thread.Sleep(2000);
        if (HasInternet())
            return;
        var restore = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Proton", "VPN"))
            ? Directory.GetDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Proton", "VPN"), "v*")
                .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d)[1..], out var v) ? v : new Version()))
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, "ProtonVPN.RestoreInternet.exe"))
                .FirstOrDefault(File.Exists)
            : null;
        if (restore is null)
        {
            notes.Add("Internet is blocked after stopping Proton, and ProtonVPN.RestoreInternet.exe was not found");
            return;
        }
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(restore) { CreateNoWindow = true }))
            p?.WaitForExit(30_000);
        notes.Add($"Internet was blocked after stopping Proton; ran RestoreInternet (online now: {HasInternet()})");
    }

    static bool HasInternet()
    {
        foreach (var (host, port) in new[] { ("1.1.1.1", 443), ("8.8.8.8", 53) })
        {
            using var client = new TcpClient();
            try
            {
                if (client.ConnectAsync(host, port).Wait(3000) && client.Connected)
                    return true;
            }
            catch (AggregateException)
            {
                // Try the next one.
            }
        }
        return false;
    }

    /// <summary>The request file is user-writable, so treat it as untrusted input.</summary>
    static HelperRequest? ReadRequest()
    {
        try
        {
            var file = new FileInfo(HelperPaths.Request);
            if (!file.Exists || file.Length > MaxRequestBytes)
                return null;
            return JsonSerializer.Deserialize<HelperRequest>(File.ReadAllText(file.FullName), HelperJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static void WriteResult(HelperResult result) => WriteJson(HelperPaths.Result, result);

    internal static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), HelperJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void WriteJson<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, HelperJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(HelperPaths.DataDir);
            File.AppendAllText(HelperPaths.Log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never break the helper.
        }
    }

    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
