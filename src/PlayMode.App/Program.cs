using System.Runtime.CompilerServices;

namespace PlayMode.App;

static class Program
{
    /// <param name="args">
    /// Optional: --config &lt;path&gt; to use another settings file; --settings to open the settings window
    /// (in the running tray, if there is one); --self-test to check the app can start, without starting it.
    /// </param>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return SelfTest();
        var settings = args.Contains("--settings");
        using var single = new Mutex(initiallyOwned: true, @"Local\PlayMode.Tray", out var first);
        if (!first)
        {
            if (settings)
                Core.Ipc.TraySignals.Send("Settings");
            return 0;
        }
        var i = Array.IndexOf(args, "--config");
        var configPath = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Core.Storage.AppPaths.ConfigFile;
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApp(configPath, settings));
        return 0;
    }

    /// <summary>
    /// Runs the static set-up the tray depends on (icons, the update client, the catalog), which otherwise only fails
    /// on someone's PC at start-up. The publish script runs it on every build. Exit code 0 means fine.
    /// </summary>
    static int SelfTest()
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(TrayIcons).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(Updater).TypeHandle);
            _ = Core.Discovery.Catalog.BuiltIn.Apps.Count;
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "PlayMode-self-test.txt"), e.ToString());
            return 1;
        }
    }
}
