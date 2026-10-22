namespace PlayMode.Core.Ipc;

/// <summary>Named events the running tray listens on, so the CLI (and hotkeys, scripts) can drive it.</summary>
public static class TraySignals
{
    public static readonly IReadOnlyList<string> Names = ["On", "Off", "Toggle", "Settings", "Exit"];

    static string FullName(string name) => $@"Local\PlayMode.{name}";

    /// <returns>False if the tray isn't running.</returns>
    public static bool Send(string name)
    {
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown signal '{name}'.", nameof(name));
        try
        {
            using var handle = EventWaitHandle.OpenExisting(FullName(Names.First(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))));
            handle.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>For the tray: one auto-reset event per signal, in <see cref="Names"/> order.</summary>
    public static EventWaitHandle[] Create() =>
        [.. Names.Select(n => new EventWaitHandle(false, EventResetMode.AutoReset, FullName(n)))];
}
