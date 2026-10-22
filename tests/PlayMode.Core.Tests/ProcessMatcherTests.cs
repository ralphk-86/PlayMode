using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Tests;

public class ProcessMatcherTests
{
    static readonly MatchContext Ctx = new(SelfPid: 1, SessionId: 1, NeverTouch: []);

    static ProcessInfo P(int id, string name, string? path = null, int session = 1) => new(id, name, path ?? $@"C:\Apps\{name}.exe", session, 0);

    [Theory]
    [InlineData("PowerToys.FancyZones", "PowerToys.*", true)]
    [InlineData("PowerToys", "PowerToys.*", false)]
    [InlineData("SPOTIFY", "spotify", true)]
    [InlineData("handy", "handy.exe", true)]
    [InlineData("express", "expr?ss", true)]
    [InlineData("a.b", "a?b", true)]
    [InlineData("aXb", "a.b", false)]   // dots are literal, not regex
    public void Matches_process_names_with_wildcards(string name, string pattern, bool expected)
    {
        var rule = new AppRule { Id = "x", Processes = [pattern] };
        Assert.Equal(expected, ProcessMatcher.Match(rule, [P(10, name)], Ctx).Count == 1);
    }

    [Fact]
    public void PathLike_tells_apart_same_named_processes()
    {
        // The real case: Claude desktop and Claude Code are both claude.exe.
        var procs = new[]
        {
            P(10, "claude", @"C:\Users\me\AppData\Local\AnthropicClaude\app-3.1\claude.exe"),
            P(11, "claude", @"C:\Users\me\.local\bin\claude.exe"),
        };
        var desktop = new AppRule { Id = "desktop", Processes = ["claude"], PathLike = @"*\AnthropicClaude\*" };
        var code = new AppRule { Id = "code", Processes = ["claude"], PathLike = @"*\.local\bin\*" };

        Assert.Equal([10], ProcessMatcher.Match(desktop, procs, Ctx).Select(p => p.Id));
        Assert.Equal([11], ProcessMatcher.Match(code, procs, Ctx).Select(p => p.Id));
    }

    [Fact]
    public void PathLike_never_matches_a_process_whose_path_is_unknown()
    {
        var rule = new AppRule { Id = "x", Processes = ["claude"], PathLike = "*" };
        Assert.Empty(ProcessMatcher.Match(rule, [new ProcessInfo(10, "claude", null, 1, 0)], Ctx));
    }

    [Fact]
    public void Ignores_other_sessions_so_service_copies_are_left_to_the_helper()
    {
        // Everything runs a user client (session 1) and a service (session 0) from the same exe.
        var rule = new AppRule { Id = "everything", Processes = ["Everything"] };
        var matched = ProcessMatcher.Match(rule, [P(10, "Everything", session: 0), P(11, "Everything", session: 1)], Ctx);
        Assert.Equal([11], matched.Select(p => p.Id));
    }

    [Fact]
    public void Wsl_rule_matches_the_vm_process_in_session_0()
    {
        var rule = new AppRule { Id = "wsl", Kind = AppKind.Wsl };
        Assert.Single(ProcessMatcher.Match(rule, [P(10, "vmmemWSL", session: 0)], Ctx));
    }

    [Fact]
    public void Never_matches_itself()
    {
        var rule = new AppRule { Id = "x", Processes = ["*"] };
        Assert.DoesNotContain(ProcessMatcher.Match(rule, [P(1, "whatever"), P(2, "other")], Ctx), p => p.Id == 1);
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    [InlineData("dwm")]
    [InlineData("MsMpEng")]
    public void Never_touches_critical_processes_even_with_a_catch_all_rule(string name)
    {
        var rule = new AppRule { Id = "greedy", Processes = ["*"] };
        Assert.Empty(ProcessMatcher.Match(rule, [P(10, name)], Ctx));
    }

    [Fact]
    public void User_never_touch_list_is_honoured()
    {
        var rule = new AppRule { Id = "greedy", Processes = ["*"] };
        var ctx = Ctx with { NeverTouch = ["Discord"] };
        Assert.Equal(["Steam"], ProcessMatcher.Match(rule, [P(10, "discord"), P(11, "Steam")], ctx).Select(p => p.Name));
    }
}
