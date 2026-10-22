using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using PlayMode.Core.Platform;
using PlayMode.Core.Updates;

namespace PlayMode.App;

/// <summary>
/// Finds the latest GitHub release, downloads its installer, checks it against the release's SHA256SUMS.txt and
/// runs it. The installer closes this tray, upgrades in place and starts the new version.
/// </summary>
sealed class Updater(ILog log)
{
    // Static initializers run top to bottom: Current must come before the client that puts it in its user agent.
    public static AppVersion Current { get; } =
        AppVersion.Parse(typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new AppVersion(0, 0, 0, "dev");

    static readonly HttpClient Http = CreateClient();

    /// <summary>Installed by the installer (it leaves its uninstaller next to us), rather than unzipped somewhere.</summary>
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateCheck.LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            log.Info("Update check: no releases published yet");
            return null;
        }
        response.EnsureSuccessStatusCode();
        var update = UpdateCheck.Find(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), Current);
        log.Info(update is null ? $"Update check: {Current} is the latest" : $"Update check: {update.Version} is available");
        return update;
    }

    /// <returns>The verified installer's path.</returns>
    public async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken ct = default)
    {
        var expected = UpdateCheck.ChecksumFor(await Http.GetStringAsync(update.ChecksumsUrl, ct).ConfigureAwait(false), update.InstallerName)
            ?? throw new InvalidDataException($"The release doesn't list a checksum for {update.InstallerName}.");
        var dir = Path.Combine(Path.GetTempPath(), "PlayMode-update");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, update.InstallerName);
        await using (var file = File.Create(path))
        await using (var download = await Http.GetStreamAsync(update.InstallerUrl, ct).ConfigureAwait(false))
            await download.CopyToAsync(file, ct).ConfigureAwait(false);

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidDataException($"The downloaded installer doesn't match the release's checksum, so it wasn't run.");
        }
        log.Info($"Downloaded {update.InstallerName} ({actual})");
        return path;
    }

    /// <summary>Starts the installer silently (with a progress window); it closes the tray and starts the new one.</summary>
    public void Install(string installer)
    {
        log.Info($"Installing {Path.GetFileName(installer)}");
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH") { UseShellExecute = true })?.Dispose();
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PlayMode/{Current}");   // GitHub requires one
        return client;
    }
}
