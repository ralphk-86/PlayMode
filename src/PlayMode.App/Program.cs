namespace PlayMode.App;

static class Program
{
    /// <param name="args">Optional: --config &lt;path&gt; to use another settings file.</param>
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(initiallyOwned: true, @"Local\PlayMode.Tray", out var first);
        if (!first)
            return;
        var i = Array.IndexOf(args, "--config");
        var configPath = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Core.Storage.AppPaths.ConfigFile;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(configPath));
    }
}
