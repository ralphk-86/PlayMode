using PlayMode.Core.Model;
using PlayMode.Core.Platform;

namespace PlayMode.Core.Engine;

public sealed record PlanItem(AppRule Rule, IReadOnlyList<ProcessInfo> Processes, long Memory, string? MainPath, string Detail);

/// <summary>What is running right now from a profile, i.e. what switching on would affect.</summary>
public sealed record Plan(Profile Profile, IReadOnlyList<PlanItem> Items, IReadOnlyList<string> RunningServices)
{
    public IEnumerable<PlanItem> Risky => Items.Where(i => i.Rule.Risky);

    public string Describe()
    {
        var lines = Items.Select(i => $"  {i.Rule.Id,-20} {i.Detail}").ToList();
        if (RunningServices.Count > 0)
            lines.Add($"  {"Services",-20} {string.Join(", ", RunningServices)}");
        return lines.Count == 0 ? "  (nothing running)" : string.Join(Environment.NewLine, lines);
    }
}

public sealed record Report(string Title, IReadOnlyList<string> Notes)
{
    public override string ToString() =>
        Notes.Count == 0 ? Title : Title + Environment.NewLine + string.Join(Environment.NewLine, Notes);
}
