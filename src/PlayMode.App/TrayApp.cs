using System.Diagnostics;
using PlayMode.App.Settings;
using PlayMode.Core.Engine;
using PlayMode.Core.Ipc;
using PlayMode.Core.Model;
using PlayMode.Core.Platform;
using PlayMode.Core.Platform.Windows;
using PlayMode.Core.Storage;
using PlayMode.Core.Updates;

namespace PlayMode.App;

/// <summary>
/// The tray icon. Left-click switches; the menu has the rest. Switching runs on a background thread
/// (it waits on processes and services) and only touches the UI to ask questions and report back.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    enum Target { On, Off, Toggle }

    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    readonly Control _ui = new();   // marshals background work back to the UI thread
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 5000 };
    readonly ToolStripMenuItem _header = new() { Enabled = false };
    readonly ToolStripMenuItem _switch = new();
    readonly ToolStripMenuItem _profiles = new("Play profile");
    readonly ToolStripMenuItem _preview = new("Preview what would change…");
    readonly ToolStripMenuItem _report = new("Last report…");
    readonly ToolStripMenuItem _setup = new();
    readonly ToolStripMenuItem _autostart = new("Start with Windows");
    readonly ToolStripMenuItem _update = new() { Visible = false };
    readonly ToolStripMenuItem _updates = new("Updates");
    readonly ToolStripMenuItem _checkNow = new();
    readonly ToolStripMenuItem _autoUpdates = new("Check for updates automatically");
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 };   // a minute after start, then hourly
    readonly EventWaitHandle[] _signals = TraySignals.Create();
    readonly ILog _log = new FileLog(AppPaths.LogFile);
    readonly Updater _updater;
    readonly string _configPath;
    SettingsForm? _settings;
    AvailableUpdate? _available;
    bool _busy;
    bool _updating;
    bool _updateBalloon;
    DateTime _lastSwitchEnd;

    public TrayApp(string configPath, bool openSettings = false)
    {
        _configPath = configPath;
        _updater = new Updater(_log);
        _ = _ui.Handle;   // create it now, on the UI thread
        _header.Font = new Font(_menu.Font, FontStyle.Bold);
        _update.Font = new Font(_menu.Font, FontStyle.Bold);
        _switch.Click += async (_, _) => await SwitchAsync(Target.Toggle, interactive: true);
        _preview.Click += async (_, _) => await PreviewAsync();
        _report.Click += (_, _) => Show(State().LastReport ?? "Nothing yet.", "PlayMode: last report");
        _setup.Click += async (_, _) => await SetupAsync();
        _autostart.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled);
        _update.Click += async (_, _) => await InstallUpdateAsync();
        _checkNow.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        _autoUpdates.Click += (_, _) => { var s = UiSettings.Load(); (s with { CheckForUpdates = !s.CheckForUpdates }).Save(); };
        _updates.DropDownItems.AddRange([_checkNow, _autoUpdates]);
        _menu.Items.AddRange(
        [
            _header, _update,
            new ToolStripSeparator(),
            _switch, _profiles, _preview, _report,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()),
            _setup, _autostart, _updates,
            new ToolStripMenuItem("Open log", null, (_, _) => OpenInNotepad(AppPaths.LogFile)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        _menu.Opening += (_, _) => RefreshMenu();

        _tray.ContextMenuStrip = _menu;
        _tray.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await SwitchAsync(Target.Toggle, interactive: true);
        };
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (_updateBalloon)
                await InstallUpdateAsync();
        };
        _refresh.Tick += (_, _) => UpdateIcon();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 3_600_000;
            await CheckForUpdatesAsync(manual: false);
        };

        UpdateIcon();
        _tray.Visible = true;
        _refresh.Start();
        _updateTimer.Start();
        new Thread(ListenForSignals) { IsBackground = true, Name = "PlayMode signals" }.Start();
        _log.Info($"Tray started (pid {Environment.ProcessId})");

        if (openSettings || !File.Exists(_configPath))
        {
            // First run: the settings window builds a starting profile from what it finds.
            _ui.BeginInvoke(OpenSettings);
            return;
        }
        if (TryCreateEngine(out var engine, out _) && engine.NeedsRecovery())
        {
            _log.Info("Signed in again while in play mode; restoring dev mode");
            _ = SwitchAsync(Target.Off, interactive: false);
        }
    }

    // ---------------------------------------------------------------- switching

    async Task SwitchAsync(Target target, bool interactive)
    {
        if (_busy)
            return;
        if (interactive && DateTime.Now - _lastSwitchEnd < TimeSpan.FromSeconds(2))
            return;   // swallow the second click of a double-click
        if (!TryCreateEngine(out var engine, out var config))
            return;
        if (target == Target.Toggle)
            target = engine.State.Mode == Mode.Play ? Target.Off : Target.On;

        _busy = true;
        UpdateIcon();
        try
        {
            var profile = PickedProfile(config);
            var report = target == Target.On
                ? await Task.Run(() => engine.EnableAsync(profile, interactive ? ConfirmAsync : null))
                : await Task.Run(() => engine.DisableAsync());
            if (report is not null)
                Balloon(report.Title, string.Join(Environment.NewLine, report.Notes));
        }
        catch (Exception e)
        {
            _log.Info($"Switch failed: {e}");
            Balloon("PlayMode: switch failed", e.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            _lastSwitchEnd = DateTime.Now;
            UpdateIcon();
        }
    }

    /// <summary>Called from the switching thread; asks on the UI thread.</summary>
    Task<bool> ConfirmAsync(string text)
    {
        var answer = new TaskCompletionSource<bool>();
        _ui.BeginInvoke(() => answer.SetResult(Ask(text)));
        return answer.Task;
    }

    static bool Ask(string text)
    {
        using var owner = TopmostOwner();
        return MessageBox.Show(owner, text, "PlayMode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>A hidden topmost owner keeps a question in front even if focus moved on in the meantime.</summary>
    static Form TopmostOwner()
    {
        var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0,
        };
        owner.Show();
        owner.Activate();
        return owner;
    }

    async Task PreviewAsync()
    {
        if (!TryCreateEngine(out var engine, out var config))
            return;
        var state = engine.State;
        var text = state.Mode == Mode.Play
            ? "Switching back would restore:\n" + string.Join("\n", state.Closed.Select(c => "  " + c.Id))
                + (state.TailscaleWasUp ? "\n  Tailscale" : "")
            : $"Switching to {config.GetProfile(PickedProfile(config)).Name} would affect:\n\n"
                + await Task.Run(() => engine.BuildPlan(PickedProfile(config)).Describe());
        Show(text, "PlayMode: preview");
    }

    async Task SetupAsync()
    {
        if (!TryLoadConfig(out var config))
            return;
        var error = await Task.Run(() => ServiceHelperClient.RunSetup(config));
        if (error is null)
            Balloon("Service control is set up", "PlayMode can now stop and restart the services in your settings.");
        else
            Balloon("Service control was not set up", error, ToolTipIcon.Warning);
    }

    // ---------------------------------------------------------------- updates

    /// <summary>Automatic checks run at most once a day, and never in play mode or mid-switch.</summary>
    async Task CheckForUpdatesAsync(bool manual)
    {
        var settings = UiSettings.Load();
        if (!manual && (!settings.CheckForUpdates || _busy || _updating || State().Mode == Mode.Play
            || DateTimeOffset.Now - settings.LastUpdateCheck < TimeSpan.FromDays(1)))
            return;
        try
        {
            _available = await Task.Run(() => _updater.CheckAsync());
            (UiSettings.Load() with { LastUpdateCheck = DateTimeOffset.Now }).Save();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            _log.Info($"Update check failed: {e.Message}");
            if (manual)
                Show($"Couldn't check for updates: {e.Message}", "PlayMode");
            return;
        }

        if (_available is null)
        {
            if (manual)
                Show($"You have the latest version ({Updater.Current}).", "PlayMode");
        }
        else if (manual)
        {
            await InstallUpdateAsync();
        }
        else if (UiSettings.Load() is var latest && latest.AnnouncedVersion != _available.Version.ToString())
        {
            (latest with { AnnouncedVersion = _available.Version.ToString() }).Save();
            Balloon($"PlayMode {_available.Version} is available", Updater.Installed ? "Click to install it." : "Click to open the download page.");
            _updateBalloon = true;
        }
    }

    async Task InstallUpdateAsync()
    {
        if (_available is not { } update || _updating)
            return;
        if (!Updater.Installed)
        {
            // A portable copy: the installer would put a second copy elsewhere, so point at the download instead.
            OpenUrl(update.PageUrl);
            return;
        }
        if (_busy || State().Mode == Mode.Play)
        {
            Show("Switch back to dev mode first, then install the update.", "PlayMode");
            return;
        }

        var install = new TaskDialogButton("Install now");
        var notes = new TaskDialogButton("What's new");
        var page = new TaskDialogPage
        {
            Caption = "PlayMode",
            Heading = $"PlayMode {update.Version} is available",
            Text = $"You have {Updater.Current}. PlayMode closes while the update installs, then starts again.",
            Icon = TaskDialogIcon.Information,
            Buttons = { install, notes, TaskDialogButton.Cancel },
        };
        TaskDialogButton picked;
        using (var owner = TopmostOwner())
            picked = TaskDialog.ShowDialog(owner, page);
        if (picked == notes)
            OpenUrl(update.PageUrl);
        if (picked != install)
            return;

        _updating = true;
        _tray.Text = $"PlayMode: downloading {update.Version}…";
        try
        {
            var installer = await Task.Run(() => _updater.DownloadAsync(update));
            _updater.Install(installer);
            ExitThread();   // the installer would ask us to anyway; it starts the new version when it's done
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            _log.Info($"Update failed: {e}");
            Balloon("PlayMode: update failed", e.Message, ToolTipIcon.Error);
        }
        finally
        {
            _updating = false;
            UpdateIcon();
        }
    }

    // ---------------------------------------------------------------- signals from the CLI / hotkeys

    void ListenForSignals()
    {
        while (true)
        {
            var name = TraySignals.Names[WaitHandle.WaitAny(_signals)];
            _ui.BeginInvoke(() => OnSignal(name));
            if (name == "Exit")
                return;
        }
    }

    void OnSignal(string name)
    {
        switch (name)
        {
            case "Exit": ExitThread(); break;
            case "On": _ = SwitchAsync(Target.On, interactive: false); break;
            case "Off": _ = SwitchAsync(Target.Off, interactive: false); break;
            case "Settings": OpenSettings(); break;
            default: _ = SwitchAsync(Target.Toggle, interactive: false); break;
        }
    }

    // ---------------------------------------------------------------- UI state

    void UpdateIcon()
    {
        var state = State();
        _tray.Icon = _busy ? TrayIcons.TrayBusy : state.Mode == Mode.Play ? TrayIcons.TrayPlay : TrayIcons.TrayDev;
        _tray.Text = _busy ? "PlayMode: switching…"
            : state.Mode == Mode.Play ? $"PlayMode: {state.Profile} is on"
            : "PlayMode: dev mode";
    }

    void RefreshMenu()
    {
        var state = State();
        var haveConfig = TryLoadConfig(out var config, quiet: true);
        var profile = haveConfig ? config.GetProfile(PickedProfile(config)).Name : "play mode";

        _header.Text = _tray.Text;
        _switch.Text = state.Mode == Mode.Play ? "Back to dev mode" : $"Switch to {profile}";
        _switch.Enabled = _preview.Enabled = !_busy && haveConfig;

        _profiles.DropDownItems.Clear();
        if (haveConfig)
        {
            foreach (var p in config.Profiles)
            {
                var name = p.Name;
                _profiles.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => (UiSettings.Load() with { Profile = name }).Save())
                {
                    Checked = name == profile,
                });
            }
        }
        _profiles.Visible = haveConfig && config.Profiles.Count > 1;
        _profiles.Enabled = state.Mode == Mode.Dev && !_busy;

        var setupNeeded = haveConfig ? ServiceHelperClient.SetupNeeded(config) : null;
        _setup.Visible = setupNeeded is not null;
        _setup.Text = $"{setupNeeded}: set up service control (admin)…";
        _autostart.Checked = Autostart.IsEnabled;

        _update.Visible = _available is not null;
        _update.Enabled = !_updating;
        if (_available is not null)
            _update.Text = Updater.Installed ? $"Install update {_available.Version}…" : $"Version {_available.Version} is available…";
        _checkNow.Text = $"Check now (you have {Updater.Current})";
        _checkNow.Enabled = !_updating;
        _autoUpdates.Checked = UiSettings.Load().CheckForUpdates;
    }

    static string? PickedProfile(PlayModeConfig config)
    {
        var picked = UiSettings.Load().Profile;
        return config.Profiles.Any(p => p.Name == picked) ? picked : null;
    }

    PlayState State() => new JsonStateStore(AppPaths.StateFile, _log).Load();

    // ---------------------------------------------------------------- config

    bool TryCreateEngine(out PlayModeEngine engine, out PlayModeConfig config)
    {
        engine = null!;
        if (!TryLoadConfig(out config))
            return false;
        engine = new PlayModeEngine(WindowsPlatform.Create(), config);
        return true;
    }

    bool TryLoadConfig(out PlayModeConfig config, bool quiet = false)
    {
        if (!File.Exists(_configPath))
        {
            config = null!;
            if (!quiet)
                OpenSettings();
            return false;
        }
        try
        {
            config = ConfigStore.Load(_configPath);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            config = null!;
            if (!quiet)
                Balloon("PlayMode: settings problem", e.Message, ToolTipIcon.Error);
            return false;
        }
    }

    void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }
        var state = State();
        _settings = new SettingsForm(_configPath, state.Mode == Mode.Play ? state.Profile : null);
        _settings.Saved += () =>
        {
            _log.Info("Settings saved");
            UpdateIcon();
        };
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    // ---------------------------------------------------------------- helpers

    void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _updateBalloon = false;   // the caller sets it again if this one is about an update
        _tray.ShowBalloonTip(6000, title, string.IsNullOrWhiteSpace(text) ? " " : text, icon);
    }

    static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    static void Show(string text, string title) =>
        MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    static void OpenInNotepad(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    protected override void ExitThreadCore()
    {
        _refresh.Stop();
        _updateTimer.Stop();
        _tray.Visible = false;
        _log.Info("Tray exited");
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _refresh.Dispose();
            _updateTimer.Dispose();
            _settings?.Dispose();
            _ui.Dispose();
            foreach (var signal in _signals)
                signal.Dispose();
        }
        base.Dispose(disposing);
    }
}
