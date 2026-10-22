using PlayMode.Core.Engine;

namespace PlayMode.Core.Tests;

public class TailscaleFlagsTests
{
    // Verbatim from `tailscale up` on a node with --hostname and --unattended set.
    const string RealError = """
        Error: changing settings via 'tailscale up' requires mentioning all
        non-default flags. To proceed, either re-run your command with --reset or
        use the command below to explicitly mention the current value of
        all non-default settings:

        	tailscale up --timeout=20s --hostname=my-pc --unattended

        """;

    [Fact]
    public void Parses_the_suggested_command()
    {
        Assert.Equal(["--timeout=20s", "--hostname=my-pc", "--unattended"], TailscaleFlags.ParseSuggestedUp(RealError));
    }

    [Fact]
    public void Handles_windows_line_endings()
    {
        Assert.Equal(["--hostname=my-pc", "--unattended"], TailscaleFlags.ParseSuggestedUp("x\r\n\ttailscale up --hostname=my-pc --unattended\r\n"));
    }

    [Theory]
    [InlineData("some unrelated failure")]
    [InlineData("\ttailscale up --hostname=a;calc.exe")]
    [InlineData("\ttailscale up --hostname=a && calc")]
    [InlineData("\ttailscale up --hostname=$(evil)")]
    [InlineData("\ttailscale up --hostname=\"x y\"")]
    [InlineData("\ttailscale up rm -rf")]
    public void Refuses_anything_it_does_not_fully_understand(string output)
    {
        Assert.Null(TailscaleFlags.ParseSuggestedUp(output));
    }
}
