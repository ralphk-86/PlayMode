using System.Diagnostics;
using PlayMode.Core.Discovery;
using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Platform.Windows;
using PlayMode.Core.Storage;

namespace PlayMode.App.Settings;

/// <summary>
/// Profiles, the apps and services each one handles, and the never-touch list. Edits stay in memory until Save,
/// which validates, keeps a .bak of the previous file and offers to update service control if its list changed.
/// </summary>
sealed class SettingsForm : Form
{
    readonly string _configPath;
    readonly string? _activePlayProfile;
    readonly Catalog _catalog = Catalog.BuiltIn;
    readonly Task<MachineScan> _scan;
    readonly Dictionary<string, string> _renames = [];

    List<Profile> _profiles = [new Profile { Name = "Play" }];
    List<string> _neverTouch = [];
    string? _defaultProfile;
    int _current;
    bool _dirty;
    bool _isNew;
    bool _filling;
    DateTime? _fileStamp;

    readonly ComboBox _profileBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(0, 3, 8, 0) };
    readonly CheckBox _isDefault = new() { Text = "Default profile", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    readonly Label _banner = Ui.Label("");
    readonly ListView _apps = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly RuleEditor _editor = new() { Dock = DockStyle.Fill };
    readonly CheckBox _confirmRisky = new() { Text = "Ask before closing apps marked \"ask first\"", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    readonly DataGridView _services = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle = BorderStyle.None,
        MultiSelect = false,
    };
    readonly Label _serviceStatus = Ui.Label("", muted: true);
    readonly ListBox _never = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    readonly TextBox _neverInput = new() { Width = 260, PlaceholderText = "process name, e.g. obs64" };
    readonly FlowLayoutPanel _neverSuggest = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 8) };
    readonly Label _status = Ui.Label("", muted: true);
    readonly Button _save;
    readonly Button _deleteProfile;
    readonly Button _renameProfile;

    public event Action? Saved;

    /// <param name="activePlayProfile">The profile play mode is on with right now, if it is.</param>
    public SettingsForm(string configPath, string? activePlayProfile)
    {
        _configPath = configPath;
        _activePlayProfile = activePlayProfile;
        Text = "PlayMode settings";
        Icon = TrayIcons.Dev;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 760);
        MinimumSize = new Size(860, 560);
        Padding = new Padding(12);

        // The scan takes a moment (file versions, services); the window is usable meanwhile.
        _scan = Task.Run(() => WindowsDiscovery.Scan(TryLoad(), _catalog));

        // ------------------------------------------------ top: profile picker
        _renameProfile = Ui.Button("Rename…", (_, _) => RenameProfile());
        _deleteProfile = Ui.Button("Delete", (_, _) => DeleteProfile());
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
        top.Controls.AddRange(
        [
            Ui.Label("Profile"), _profileBox,
            Ui.Button("New…", (_, _) => NewProfile(copy: false)),
            Ui.Button("Copy…", (_, _) => NewProfile(copy: true)),
            _renameProfile, _deleteProfile, _isDefault,
        ]);
        _banner.MaximumSize = new Size(1060, 0);
        _banner.Dock = DockStyle.Top;
        _banner.Padding = new Padding(0, 0, 0, 6);
        _banner.Visible = false;
        _banner.TextChanged += (_, _) => _banner.Visible = _banner.Text.Length > 0;
        _profileBox.SelectedIndexChanged += (_, _) =>
        {
            if (_filling || _profileBox.SelectedIndex < 0)
                return;
            _current = _profileBox.SelectedIndex;
            FillProfile();
        };
        _isDefault.CheckedChanged += (_, _) =>
        {
            if (_filling)
                return;
            _defaultProfile = _isDefault.Checked ? Profile.Name : null;
            MarkDirty();
        };

        // ------------------------------------------------ tabs
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(AppsPage());
        tabs.TabPages.Add(ServicesPage());
        tabs.TabPages.Add(NeverTouchPage());

        // ------------------------------------------------ bottom
        _save = Ui.Button("Save", (_, _) =>
        {
            if (Save())
                Close();
        });
        var cancel = Ui.Button("Cancel", (_, _) => Close());
        var openFile = Ui.Link("Open the settings file", OpenFile);
        openFile.Margin = new Padding(0, 8, 16, 0);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        bottom.Controls.AddRange([cancel, _save, openFile, _status]);

        Controls.Add(tabs);
        Controls.Add(_banner);
        Controls.Add(top);
        Controls.Add(bottom);
        AcceptButton = null;
        CancelButton = cancel;

        LoadConfig();
        Shown += async (_, _) => await OnScanned();
    }

    Profile Profile => _profiles[_current];

    MatchContext Context => new(Environment.ProcessId, Process.GetCurrentProcess().SessionId, _neverTouch);

    // ---------------------------------------------------------------- pages

    TabPage AppsPage()
    {
        _apps.Columns.Add("App", 190);
        _apps.Columns.Add("What happens", 330);
        _apps.SelectedIndexChanged += (_, _) => ShowSelectedRule();
        _editor.Changed += rule =>
        {
            var i = SelectedApp;
            if (i < 0)
                return;
            SetProfile(Profile with { Apps = Replace(Profile.Apps, i, rule) });
            _apps.Items[i].Text = rule.Id.Length == 0 ? "(no name)" : rule.Id;
            _apps.Items[i].SubItems[1].Text = RuleText.Describe(rule);
        };
        _confirmRisky.CheckedChanged += (_, _) =>
        {
            if (!_filling)
                SetProfile(Profile with { ConfirmRisky = _confirmRisky.Checked });
        };

        var buttons = Ui.Row(
            Ui.Button("Add apps…", async (_, _) => await AddApps()),
            Ui.Button("Add by hand", (_, _) => AddBlank()),
            Ui.Button("Remove", (_, _) => RemoveApp()),
            Ui.Button("Move up", (_, _) => MoveApp(-1)),
            Ui.Button("Move down", (_, _) => MoveApp(+1)));
        buttons.Dock = DockStyle.Bottom;
        buttons.Padding = new Padding(0, 8, 0, 0);
        var order = Ui.Label("Apps close in this order, and come back in this order.", muted: true);
        order.Dock = DockStyle.Top;
        order.Padding = new Padding(0, 0, 0, 6);
        _confirmRisky.Dock = DockStyle.Bottom;

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        left.Controls.AddRange([_apps, order, buttons, _confirmRisky]);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(_editor);
        Load += (_, _) =>
        {
            split.SplitterDistance = Math.Max(300, split.Width - 480);
            split.Panel2MinSize = 380;
        };

        var page = new TabPage("Apps") { UseVisualStyleBackColor = false };
        page.Controls.Add(split);
        return page;
    }

    TabPage ServicesPage()
    {
        _services.Columns.Add(new DataGridViewTextBoxColumn { Name = "Service", HeaderText = "Service", ReadOnly = true, FillWeight = 35 });
        _services.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "What it is", ReadOnly = true, FillWeight = 45 });
        _services.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Restart", HeaderText = "Start again afterwards", FillWeight = 20 });
        // The grid draws its own headers in light colours unless told otherwise; these follow the dark palette.
        _services.BackgroundColor = SystemColors.Window;
        _services.EnableHeadersVisualStyles = false;
        _services.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        _services.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        _services.ColumnHeadersDefaultCellStyle.SelectionBackColor = SystemColors.Control;
        _services.ColumnHeadersDefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        _services.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _services.GridColor = SystemColors.ControlDark;
        _services.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_services.IsCurrentCellDirty)
                _services.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _services.CellValueChanged += (_, e) =>
        {
            if (_filling || e.RowIndex < 0 || e.ColumnIndex != 2)
                return;
            var restart = (bool)(_services.Rows[e.RowIndex].Cells[2].Value ?? true);
            SetProfile(Profile with { Services = Replace(Profile.Services, e.RowIndex, Profile.Services[e.RowIndex] with { Restart = restart }) });
        };

        var intro = Ui.Label("Services are stopped after the apps close, and started again before they come back. "
            + "Untick \"Start again\" for services an app starts by itself.", muted: true);
        intro.Dock = DockStyle.Top;
        intro.MaximumSize = new Size(1000, 0);
        intro.Padding = new Padding(0, 0, 0, 6);
        var buttons = Ui.Row(
            Ui.Button("Add services…", async (_, _) => await AddServices()),
            Ui.Button("Remove", (_, _) => RemoveService()),
            _serviceStatus);
        buttons.Dock = DockStyle.Bottom;
        buttons.Padding = new Padding(0, 8, 0, 0);

        var page = new TabPage("Services") { Padding = new Padding(8), UseVisualStyleBackColor = false };
        page.Controls.AddRange([_services, intro, buttons]);
        return page;
    }

    TabPage NeverTouchPage()
    {
        var intro = Ui.Label("PlayMode never closes or freezes these, in any profile, whatever a rule says. "
            + "Windows' own processes are always protected and don't need to be listed.", muted: true);
        intro.Dock = DockStyle.Top;
        intro.MaximumSize = new Size(1000, 0);
        _neverInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddNeverTouch(_neverInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                _neverInput.Clear();
                e.SuppressKeyPress = true;
            }
        };
        var add = Ui.Row(_neverInput,
            Ui.Button("Add", (_, _) =>
            {
                AddNeverTouch(_neverInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                _neverInput.Clear();
            }),
            Ui.Button("Remove", (_, _) =>
            {
                foreach (var name in _never.SelectedItems.Cast<string>().ToList())
                    _neverTouch.Remove(name);
                FillNeverTouch();
                MarkDirty();
            }));
        add.Dock = DockStyle.Bottom;
        add.Padding = new Padding(0, 8, 0, 0);

        var page = new TabPage("Never touch") { Padding = new Padding(8), UseVisualStyleBackColor = false };
        page.Controls.AddRange([_never, _neverSuggest, intro, add]);
        return page;
    }

    // ---------------------------------------------------------------- loading and filling

    PlayModeConfig? TryLoad()
    {
        try
        {
            return File.Exists(_configPath) ? ConfigStore.Load(_configPath) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    void LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                Apply(ConfigStore.Load(_configPath));
                _fileStamp = File.GetLastWriteTimeUtc(_configPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // Don't paper over a hand-edited file: starting fresh would throw the user's work away on Save.
                _isNew = false;
                Apply(new PlayModeConfig { Profiles = [new Profile { Name = "Play" }] });
                _banner.Text = $"Your settings file couldn't be read: {e.Message}\nFix it with \"Open the settings file\" below, "
                    + "or set things up again here; saving replaces the file (a copy is kept as config.json.bak).";
                _banner.ForeColor = Color.Firebrick;
            }
        }
        else
        {
            _isNew = true;
            Apply(new PlayModeConfig { Profiles = [new Profile { Name = "Play" }], DefaultProfile = "Play" });
            _banner.Text = "Looking at what's on this PC…";
        }
        if (_activePlayProfile is not null)
            _status.Text = $"Play mode is on ({_activePlayProfile}). Changes apply the next time you switch.";
    }

    void Apply(PlayModeConfig config)
    {
        _profiles = [.. config.Profiles];
        _neverTouch = [.. config.NeverTouch];
        _defaultProfile = config.DefaultProfile;
        var picked = UiSettings.Load().Profile;
        _current = Math.Max(0, _profiles.FindIndex(p => p.Name == (_activePlayProfile ?? picked ?? _defaultProfile)));
        FillProfiles();
        FillNeverTouch();
    }

    async Task OnScanned()
    {
        MachineScan scan;
        try
        {
            scan = await _scan;
        }
        catch (Exception e)
        {
            _banner.Text = $"Couldn't look around this PC ({e.Message}). You can still add apps by hand.";
            return;
        }
        if (IsDisposed)
            return;
        if (_isNew && !_dirty)
        {
            Apply(Suggestions.Starter(scan, _catalog, Context));
            _dirty = true;
            _banner.Text = "Welcome! PlayMode picked these from what's installed and running on this PC. "
                + "Check what each one does, add or remove apps, then Save.";
        }
        else if (_isNew)
        {
            _banner.Text = "";
        }
        _neverInput.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _neverInput.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _neverInput.AutoCompleteCustomSource.AddRange([.. scan.ProcessNames.Order()]);
        FillNeverTouch();
        FillServices();
    }

    void FillProfiles()
    {
        _filling = true;
        _profileBox.Items.Clear();
        foreach (var p in _profiles)
            _profileBox.Items.Add(p.Name);
        _profileBox.SelectedIndex = _current;
        _filling = false;
        FillProfile();
    }

    void FillProfile()
    {
        _filling = true;
        _isDefault.Checked = string.Equals(_defaultProfile, Profile.Name, StringComparison.OrdinalIgnoreCase);
        _confirmRisky.Checked = Profile.ConfirmRisky;
        var active = Profile.Name == _activePlayProfile;
        _renameProfile.Enabled = _deleteProfile.Enabled = !active;
        _deleteProfile.Enabled &= _profiles.Count > 1;
        _filling = false;
        FillApps(0);
        FillServices();
    }

    void FillApps(int select)
    {
        _apps.BeginUpdate();
        _apps.Items.Clear();
        foreach (var rule in Profile.Apps)
            _apps.Items.Add(new ListViewItem([rule.Id, RuleText.Describe(rule)]));
        _apps.EndUpdate();
        if (_apps.Items.Count > 0)
        {
            select = Math.Clamp(select, 0, _apps.Items.Count - 1);
            _apps.Items[select].Selected = true;
            _apps.Items[select].Focused = true;
            _apps.EnsureVisible(select);
        }
        ShowSelectedRule();
    }

    void ShowSelectedRule()
    {
        var i = SelectedApp;
        var rule = i >= 0 ? Profile.Apps[i] : null;
        _editor.Edit(rule, rule is null ? null : _catalog.FindApp(rule.Id));
    }

    int SelectedApp => _apps.SelectedIndices.Count > 0 ? _apps.SelectedIndices[0] : -1;

    void FillServices()
    {
        _filling = true;
        var scan = _scan.IsCompletedSuccessfully ? _scan.Result : null;
        _services.Rows.Clear();
        foreach (var s in Profile.Services)
        {
            var title = _catalog.FindService(s.Name)?.Title
                ?? scan?.Services.FirstOrDefault(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? "";
            if (scan is not null && !scan.Services.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                title = $"{title} (not installed)".Trim();
            _services.Rows.Add(s.Name, title, s.Restart);
        }
        _filling = false;
        UpdateServiceStatus();
    }

    void UpdateServiceStatus()
    {
        var config = Current();
        var needed = ServiceHelperClient.AllowlistFor(config).Count == 0 ? null : ServiceHelperClient.SetupNeeded(config);
        _serviceStatus.Text = needed is null ? "" : $"{needed}: PlayMode will ask to set up service control (admin) when you save.";
    }

    void FillNeverTouch()
    {
        _never.BeginUpdate();
        _never.Items.Clear();
        foreach (var name in _neverTouch.Order(StringComparer.OrdinalIgnoreCase))
            _never.Items.Add(name);
        _never.EndUpdate();

        _neverSuggest.Controls.Clear();
        if (!_scan.IsCompletedSuccessfully)
            return;
        var suggestions = Suggestions.LeaveAlone(_scan.Result, _catalog, _neverTouch);
        if (suggestions.Count == 0)
            return;
        _neverSuggest.Controls.Add(Ui.Label("Found on this PC:"));
        foreach (var entry in suggestions)
        {
            _neverSuggest.Controls.Add(Ui.Link($"+ {entry.Name}", () => AddNeverTouch(entry.Processes)));
        }
    }

    // ---------------------------------------------------------------- profile commands

    void NewProfile(bool copy)
    {
        var name = Ui.Prompt(this, copy ? "Copy profile" : "New profile", "Name", copy ? $"{Profile.Name} copy" : "");
        if (name is null || !NameIsFree(name))
            return;
        _profiles.Add(copy ? Profile with { Name = name } : new Profile { Name = name });
        _current = _profiles.Count - 1;
        FillProfiles();
        MarkDirty();
    }

    void RenameProfile()
    {
        var old = Profile.Name;
        var name = Ui.Prompt(this, "Rename profile", "Name", old);
        if (name is null || name == old || (!string.Equals(name, old, StringComparison.OrdinalIgnoreCase) && !NameIsFree(name)))
            return;
        _profiles[_current] = Profile with { Name = name };
        if (string.Equals(_defaultProfile, old, StringComparison.OrdinalIgnoreCase))
            _defaultProfile = name;
        _renames[old] = name;
        FillProfiles();
        MarkDirty();
    }

    void DeleteProfile()
    {
        if (_profiles.Count < 2 || !Ui.Confirm(this, $"Delete the profile \"{Profile.Name}\"?"))
            return;
        if (string.Equals(_defaultProfile, Profile.Name, StringComparison.OrdinalIgnoreCase))
            _defaultProfile = null;
        _profiles.RemoveAt(_current);
        _current = 0;
        FillProfiles();
        MarkDirty();
    }

    bool NameIsFree(string name)
    {
        if (!_profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return true;
        Ui.Error(this, $"There's already a profile called \"{name}\".");
        return false;
    }

    // ---------------------------------------------------------------- app commands

    async Task AddApps()
    {
        var scan = await Scanned();
        if (scan is null)
            return;
        using var picker = new AddAppsForm(Suggestions.Apps(scan, _catalog, Profile, Context), Profile.Name);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Picked.Count == 0)
            return;

        var apps = Profile.Apps.ToList();
        var services = Profile.Services.ToList();
        foreach (var s in picker.Picked)
        {
            apps.Add(Suggestions.WithUniqueId(s.Rule, apps));
            // An app's own services come along, if this PC has them.
            services.AddRange(s.Services.Where(rule =>
                scan.Services.Any(x => string.Equals(x.Name, rule.Name, StringComparison.OrdinalIgnoreCase))
                && !services.Any(x => string.Equals(x.Name, rule.Name, StringComparison.OrdinalIgnoreCase))));
        }
        var first = Profile.Apps.Count;
        SetProfile(Profile with { Apps = apps, Services = services });
        FillApps(first);
        FillServices();
    }

    void AddBlank()
    {
        var rule = Suggestions.WithUniqueId(new AppRule { Id = "NewApp" }, Profile.Apps);
        SetProfile(Profile with { Apps = [.. Profile.Apps, rule] });
        FillApps(Profile.Apps.Count - 1);
    }

    void RemoveApp()
    {
        var i = SelectedApp;
        if (i < 0)
            return;
        SetProfile(Profile with { Apps = [.. Profile.Apps.Where((_, n) => n != i)] });
        FillApps(i);
    }

    void MoveApp(int by)
    {
        var i = SelectedApp;
        var j = i + by;
        if (i < 0 || j < 0 || j >= Profile.Apps.Count)
            return;
        var apps = Profile.Apps.ToList();
        (apps[i], apps[j]) = (apps[j], apps[i]);
        SetProfile(Profile with { Apps = apps });
        FillApps(j);
    }

    // ---------------------------------------------------------------- service and never-touch commands

    async Task AddServices()
    {
        var scan = await Scanned();
        if (scan is null)
            return;
        using var picker = new AddServicesForm(windows => Suggestions.Services(scan, _catalog, Profile, windows), Profile.Name);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Picked.Count == 0)
            return;
        SetProfile(Profile with { Services = [.. Profile.Services, .. picker.Picked] });
        FillServices();
    }

    void RemoveService()
    {
        if (_services.CurrentRow is not { Index: >= 0 } row)
            return;
        var i = row.Index;
        SetProfile(Profile with { Services = [.. Profile.Services.Where((_, n) => n != i)] });
        FillServices();
    }

    void AddNeverTouch(IEnumerable<string> names)
    {
        foreach (var name in names.Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n))
        {
            if (name.Length > 0 && !_neverTouch.Contains(name, StringComparer.OrdinalIgnoreCase))
                _neverTouch.Add(name);
        }
        FillNeverTouch();
        MarkDirty();
    }

    async Task<MachineScan?> Scanned()
    {
        if (!_scan.IsCompleted)
        {
            UseWaitCursor = true;
            _status.Text = "Looking at what's on this PC…";
        }
        try
        {
            return await _scan;
        }
        catch (Exception e)
        {
            Ui.Error(this, $"Couldn't look around this PC: {e.Message}");
            return null;
        }
        finally
        {
            UseWaitCursor = false;
            _status.Text = "";
        }
    }

    // ---------------------------------------------------------------- saving

    void SetProfile(Profile profile)
    {
        _profiles[_current] = profile;
        MarkDirty();
    }

    void MarkDirty()
    {
        _dirty = true;
        UpdateServiceStatus();
    }

    PlayModeConfig Current() => new()
    {
        Profiles = _profiles,
        DefaultProfile = _profiles.Any(p => p.Name == _defaultProfile) ? _defaultProfile : null,
        NeverTouch = _neverTouch,
    };

    bool Save()
    {
        var config = Current();
        try
        {
            ConfigStore.Validate(config);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            Ui.Error(this, $"Can't save yet: {e.Message}");
            return false;
        }
        if (_fileStamp is { } stamp && File.Exists(_configPath) && File.GetLastWriteTimeUtc(_configPath) != stamp
            && !Ui.Confirm(this, "The settings file was changed by something else while this window was open. Replace it with what's here?"))
            return false;

        if (File.Exists(_configPath))
            File.Copy(_configPath, _configPath + ".bak", overwrite: true);
        ConfigStore.Save(_configPath, config);
        _fileStamp = File.GetLastWriteTimeUtc(_configPath);
        _dirty = false;
        _isNew = false;

        var ui = UiSettings.Load();
        if (ui.Profile is { } picked && _renames.TryGetValue(picked, out var renamed))
            (ui with { Profile = renamed }).Save();
        Saved?.Invoke();

        var needed = ServiceHelperClient.AllowlistFor(config).Count == 0 ? null : ServiceHelperClient.SetupNeeded(config);
        if (needed is not null && Ui.Confirm(this, $"{needed}. Set up service control now? Windows will ask for admin approval once."))
        {
            UseWaitCursor = true;
            var error = ServiceHelperClient.RunSetup(config);
            UseWaitCursor = false;
            if (error is not null)
                Ui.Error(this, $"Service control was not set up: {error}\nYou can do it later from the tray menu.");
        }
        return true;
    }

    void OpenFile()
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "Save your changes before opening the file?", "PlayMode", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
                return;
            _dirty = false;
        }
        if (!File.Exists(_configPath))
        {
            Ui.Error(this, "There's no settings file yet. Save first.");
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_configPath}\"") { UseShellExecute = true })?.Dispose();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // None is a plain WM_CLOSE from outside (another app, a script); still the user's unsaved work.
        if (_dirty && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            var answer = MessageBox.Show(this, _isNew ? "Save these settings?" : "Save your changes?", "PlayMode",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
            _dirty = false;
        }
        base.OnFormClosing(e);
    }

    static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> list, int index, T value) =>
        [.. list.Select((item, i) => i == index ? value : item)];
}
