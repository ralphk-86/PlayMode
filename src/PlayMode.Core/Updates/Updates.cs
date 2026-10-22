using System.Text.Json;

namespace PlayMode.Core.Updates;

/// <summary>A version like 1.2.3 or 1.2.3-beta.1, compared the way SemVer does (a release beats its pre-releases).</summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? Pre) : IComparable<AppVersion>
{
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = text.IndexOf('-');
        var pre = dash >= 0 ? text[(dash + 1)..] : null;
        var parts = (dash >= 0 ? text[..dash] : text).Split('.');
        if (parts.Length != 3 || !parts.All(p => int.TryParse(p, out var n) && n >= 0) || pre is "")
            return null;
        return new AppVersion(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), pre);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
            return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
            return core;
        if (Pre == other.Pre)
            return 0;
        if (Pre is null)
            return 1;
        if (other.Pre is null)
            return -1;
        return string.CompareOrdinal(Pre, other.Pre);
    }

    public override string ToString() => Pre is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}

public sealed record AvailableUpdate(AppVersion Version, string PageUrl, string InstallerName, string InstallerUrl, string ChecksumsUrl);

/// <summary>Reads GitHub's "latest release" answer. Everything in it is treated as untrusted input.</summary>
public static class UpdateCheck
{
    public const string Repository = "ralphk-86/PlayMode";
    public const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    // Only files attached to this repository's own releases are ever downloaded.
    const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <returns>The update to offer, or null if the release isn't newer, isn't final, or lacks what we need.</returns>
    public static AvailableUpdate? Find(string releaseJson, AppVersion current)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease"))
            return null;
        var version = AppVersion.Parse(Text(root, "tag_name"));
        if (version is null || version.CompareTo(current) <= 0)
            return null;

        var installerName = $"PlayMode-{version}-setup-x64.exe";
        string? installer = null, checksums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");
                if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                    continue;
                if (name == installerName)
                    installer = url;
                else if (name == "SHA256SUMS.txt")
                    checksums = url;
            }
        }
        if (installer is null || checksums is null)
            return null;
        var page = Text(root, "html_url") is { } html && html.StartsWith($"https://github.com/{Repository}/", StringComparison.Ordinal)
            ? html
            : ReleasesPage;
        return new AvailableUpdate(version, page, installerName, installer, checksums);
    }

    /// <summary>The SHA-256 (lowercase hex) listed for a file in a sha256sum-style file, or null.</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
