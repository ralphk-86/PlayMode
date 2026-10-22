namespace PlayMode.App;

static class Program
{
    /// <param name="args">
    /// Optional: --config &lt;path&gt; to use another settings file; --settings to open the settings window
    /// (in the running tray, if there is one).
    /// </param>
    [STAThread]
    static void Main(string[] args)
    {
        var settings = args.Contains("--settings");
        using var single = new Mutex(initiallyOwned: true, @"Local\PlayMode.Tray", out var first);
        if (!first)
        {
            if (settings)
                Core.Ipc.TraySignals.Send("Settings");
            return;
        }
        var i = Array.IndexOf(args, "--config");
        var configPath = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Core.Storage.AppPaths.ConfigFile;
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApp(configPath, settings));
    }
}
