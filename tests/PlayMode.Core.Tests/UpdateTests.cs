using PlayMode.Core.Updates;

namespace PlayMode.Core.Tests;

public class UpdateTests
{
    static readonly AppVersion Current = AppVersion.Parse("0.1.0")!;

    static string Release(string tag, bool prerelease = false, string? installerUrl = null, bool sums = true) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/ralphk-86/PlayMode/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "PlayMode-{{tag.TrimStart('v')}}-setup-x64.exe",
              "browser_download_url": "{{installerUrl ?? $"https://github.com/ralphk-86/PlayMode/releases/download/{tag}/PlayMode-{tag.TrimStart('v')}-setup-x64.exe"}}" },
            { "name": "PlayMode-{{tag.TrimStart('v')}}-win-x64.zip",
              "browser_download_url": "https://github.com/ralphk-86/PlayMode/releases/download/{{tag}}/PlayMode-{{tag.TrimStart('v')}}-win-x64.zip" }
            {{(sums ? $", {{ \"name\": \"SHA256SUMS.txt\", \"browser_download_url\": \"https://github.com/ralphk-86/PlayMode/releases/download/{tag}/SHA256SUMS.txt\" }}" : "")}}
          ]
        }
        """;

    [Theory]
    [InlineData("0.1.0", "0.1.1", -1)]
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("v1.2.3", "1.2.3+abc123", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void Compares_versions(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(AppVersion.Parse(a)!.CompareTo(AppVersion.Parse(b))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("latest")]
    public void Rejects_odd_versions(string text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void Offers_a_newer_release_with_its_installer_and_checksums()
    {
        var update = UpdateCheck.Find(Release("v0.2.0"), Current);
        Assert.NotNull(update);
        Assert.Equal("0.2.0", update.Version.ToString());
        Assert.Equal("PlayMode-0.2.0-setup-x64.exe", update.InstallerName);
        Assert.EndsWith("/v0.2.0/PlayMode-0.2.0-setup-x64.exe", update.InstallerUrl);
        Assert.EndsWith("/v0.2.0/SHA256SUMS.txt", update.ChecksumsUrl);
        Assert.Equal("https://github.com/ralphk-86/PlayMode/releases/tag/v0.2.0", update.PageUrl);
    }

    [Fact]
    public void Ignores_same_older_and_prereleases()
    {
        Assert.Null(UpdateCheck.Find(Release("v0.1.0"), Current));
        Assert.Null(UpdateCheck.Find(Release("v0.0.9"), Current));
        Assert.Null(UpdateCheck.Find(Release("v0.2.0", prerelease: true), Current));
    }

    [Fact]
    public void Never_downloads_from_anywhere_but_this_repos_releases()
    {
        Assert.Null(UpdateCheck.Find(Release("v0.2.0", installerUrl: "https://evil.example/PlayMode-0.2.0-setup-x64.exe"), Current));
        Assert.Null(UpdateCheck.Find(Release("v0.2.0", installerUrl: "https://github.com/someone/PlayMode/releases/download/v0.2.0/PlayMode-0.2.0-setup-x64.exe"), Current));
        Assert.Null(UpdateCheck.Find(Release("v0.2.0", installerUrl: "http://github.com/ralphk-86/PlayMode/releases/download/v0.2.0/PlayMode-0.2.0-setup-x64.exe"), Current));
    }

    [Fact]
    public void Needs_checksums()
    {
        Assert.Null(UpdateCheck.Find(Release("v0.2.0", sums: false), Current));
    }

    [Fact]
    public void Reads_checksum_files()
    {
        var sums = """
            9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  PlayMode-0.2.0-setup-x64.exe
            60303AE22B998861BCE3B28F33EEC1BE758A213C86C93C076DBE9F558C11C752 *PlayMode-0.2.0-win-x64.zip
            """;
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", UpdateCheck.ChecksumFor(sums, "PlayMode-0.2.0-setup-x64.exe"));
        Assert.Equal("60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752", UpdateCheck.ChecksumFor(sums, "PlayMode-0.2.0-win-x64.zip"));
        Assert.Null(UpdateCheck.ChecksumFor(sums, "other.exe"));
        Assert.Null(UpdateCheck.ChecksumFor("nothex  PlayMode-0.2.0-setup-x64.exe", "PlayMode-0.2.0-setup-x64.exe"));
    }
}
