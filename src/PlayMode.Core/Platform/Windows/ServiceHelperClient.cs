using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using PlayMode.Core.Model;
using PlayMode.Shared;

namespace PlayMode.Core.Platform.Windows;

/// <summary>
/// Client side of the elevated helper: one-time setup through UAC, then requests through a file only this
/// user can write and two on-demand scheduled tasks that run the helper as SYSTEM.
/// </summary>
public sealed class ServiceHelperClient(ILog log)
{
    const int ErrorCancelled = 1223;   // the user said no at the UAC prompt
    static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The helper shipped next to this app; setup copies it into Program Files.</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "helper");

    public static bool IsInstalled =>
        File.Exists(HelperPaths.Exe)
        && File.Exists(HelperPaths.Request)
        && Enum.GetValues<HelperAction>().All(a =>
            CliRunner.Run("schtasks.exe", ["/Query", "/TN", HelperPaths.TaskName(a)], TimeSpan.FromSeconds(10)).ExitCode == 0);

    /// <summary>Every service any profile wants stopped. Kept running afterwards only if every profile agrees.</summary>
    public static IReadOnlyList<AllowedService> AllowlistFor(PlayModeConfig config) =>
        [.. config.Profiles.SelectMany(p => p.Services)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AllowedService(g.First().Name, g.All(s => s.Restart)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    /// <returns>Why setup needs to run (again), or null if nothing is needed.</returns>
    public static string? SetupNeeded(PlayModeConfig config)
    {
        var wanted = AllowlistFor(config);
        if (!IsInstalled)
            return wanted.Count > 0 ? "Service control isn't set up" : null;
        var installed = ReadJson<List<AllowedService>>(HelperPaths.Allowlist) ?? [];
        if (!wanted.SequenceEqual(installed.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)))
            return "Your service list changed";
        var bundled = Hash(Path.Combine(BundledDir, HelperPaths.DllName));
        if (bundled is not null && bundled != Hash(Path.Combine(HelperPaths.InstallDir, HelperPaths.DllName)))
            return "PlayMode was updated";
        return null;
    }

    /// <summary>Installs or updates the helper (one UAC prompt).</summary>
    /// <returns>Null on success, otherwise why it failed.</returns>
    public static string? RunSetup(PlayModeConfig config)
    {
        var allowlist = Path.Combine(Path.GetTempPath(), $"playmode-allowlist-{Guid.NewGuid():N}.json");
        File.WriteAllText(allowlist, JsonSerializer.Serialize(AllowlistFor(config), HelperJson.Options));
        try
        {
            var user = WindowsIdentity.GetCurrent().User!.Value;
            return RunElevated($"install --allowlist \"{allowlist}\" --user {user}");
        }
        finally
        {
            File.Delete(allowlist);
        }
    }

    public static string? RunUninstall() => RunElevated("uninstall");

    static string? RunElevated(string arguments)
    {
        var exe = Path.Combine(BundledDir, HelperPaths.ExeName);
        if (!File.Exists(exe))
            return $"The helper isn't next to PlayMode ({exe})";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode == 0 ? null : $"Setup failed (exit {p.ExitCode}); see {HelperPaths.Log}";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return "Cancelled at the admin prompt";
        }
    }

    public async Task<ServiceResult?> RunAsync(HelperAction action, IReadOnlyList<string> services, CancellationToken ct)
    {
        if (!IsInstalled)
        {
            log.Info($"Service control isn't set up; skipping service {action.ToString().ToLowerInvariant()}");
            return null;
        }
        var request = new HelperRequest(Guid.NewGuid(), action, services);
        try
        {
            File.WriteAllText(HelperPaths.Request, JsonSerializer.Serialize(request, HelperJson.Options));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            log.Info($"Can't write the service request ({e.Message}); run setup again");
            return null;
        }
        var run = CliRunner.Run("schtasks.exe", ["/Run", "/TN", HelperPaths.TaskName(action)], TimeSpan.FromSeconds(15));
        if (run.ExitCode != 0)
        {
            log.Info($"Couldn't start the service helper: {run.Output.Trim()}");
            return null;
        }
        var started = DateTime.UtcNow;
        var finishedAt = (DateTime?)null;
        while (DateTime.UtcNow - started < ResultTimeout)
        {
            if (ReadJson<HelperResult>(HelperPaths.Result) is { } result && result.Id == request.Id)
                return new ServiceResult(result.Ok, result.Failed, result.Notes);
            await Task.Delay(200, ct).ConfigureAwait(false);

            // If the helper exits without answering (e.g. it couldn't even load), don't wait out the full timeout.
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(2) && !TaskRunning(action))
            {
                finishedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - finishedAt > TimeSpan.FromSeconds(1))
                {
                    log.Info($"Service helper exited without answering ({action}); see {HelperPaths.Log}");
                    return null;
                }
            }
        }
        log.Info($"Service helper timed out ({action})");
        return null;
    }

    static bool TaskRunning(HelperAction action)
    {
        var query = CliRunner.Run("schtasks.exe", ["/Query", "/TN", HelperPaths.TaskName(action), "/FO", "CSV", "/NH"], TimeSpan.FromSeconds(10));
        return query.Stdout.Contains("\"Running\"", StringComparison.OrdinalIgnoreCase);
    }

    static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), HelperJson.Options) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
}
