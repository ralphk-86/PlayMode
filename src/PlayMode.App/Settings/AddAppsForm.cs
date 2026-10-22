using PlayMode.Core.Discovery;

namespace PlayMode.App.Settings;

/// <summary>Pick apps to add: what's running, what starts with Windows, and the catalog of known apps.</summary>
sealed class AddAppsForm : Form
{
    readonly IReadOnlyList<AppSuggestion> _all;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        ShowItemToolTips = true,
    };
    readonly TextBox _search = new() { Width = 260, PlaceholderText = "Search" };
    readonly CheckBox _showAdded = new() { Text = "Show apps already in this profile", AutoSize = true, Margin = new Padding(16, 4, 0, 0) };
    readonly Label _details = Ui.Label("", muted: true);
    readonly Button _add;
    readonly HashSet<AppSuggestion> _checked = [];
    bool _filling;

    public IReadOnlyList<AppSuggestion> Picked => [.. _all.Where(_checked.Contains)];

    public AddAppsForm(IReadOnlyList<AppSuggestion> suggestions, string profileName)
    {
        _all = suggestions;
        Text = $"Add apps to {profileName}";
        Icon = TrayIcons.Dev;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(1100, 680);
        MinimumSize = new Size(720, 420);
        Padding = new Padding(12);

        _list.Columns.Add("App", 260);
        _list.Columns.Add("Found", 150);
        _list.Columns.Add("Memory", 80, HorizontalAlignment.Right);
        _list.Columns.Add("What PlayMode will do", 420);
        _list.Columns.Add("Category", 90);
        _list.ItemCheck += (_, e) =>
        {
            if (_filling)
                return;
            var s = (AppSuggestion)_list.Items[e.Index].Tag!;
            if (s.AlreadyAdded)
                e.NewValue = CheckState.Unchecked;   // shown for reference only
            else if (e.NewValue == CheckState.Checked)
                _checked.Add(s);
            else
                _checked.Remove(s);
            BeginInvoke(UpdateButton);
        };
        _list.SelectedIndexChanged += (_, _) => ShowDetails();
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1)
                _list.SelectedItems[0].Checked = !_list.SelectedItems[0].Checked;
        };
        _search.TextChanged += (_, _) => Fill();
        _showAdded.CheckedChanged += (_, _) => Fill();

        _add = new Button { Text = "Add", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false, Padding = new Padding(8, 2, 8, 2) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 2, 8, 2) };
        AcceptButton = _add;
        CancelButton = cancel;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8) };
        top.Controls.AddRange([_search, _showAdded]);
        _details.MaximumSize = new Size(940, 0);
        _details.Dock = DockStyle.Top;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.AddRange([cancel, _add]);
        var detailsPanel = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(0, 6, 0, 0) };
        detailsPanel.Controls.Add(_details);

        Controls.Add(_list);
        Controls.Add(detailsPanel);
        Controls.Add(bottom);
        Controls.Add(top);
        Fill();
    }

    void Fill()
    {
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        // A column rather than ListView groups: group headers stay dark blue in dark mode.
        var query = _search.Text.Trim();
        foreach (var s in _all.OrderBy(Rank))
        {
            if (s.AlreadyAdded && !_showAdded.Checked)
                continue;
            if (query.Length > 0 && !Matches(s, query))
                continue;
            var item = new ListViewItem(s.AlreadyAdded ? $"{s.Title} (added)" : s.Title)
            {
                Tag = s,
                Checked = _checked.Contains(s),
                ForeColor = s.AlreadyAdded ? SystemColors.GrayText : _list.ForeColor,
                ToolTipText = s.Recipe?.Notes ?? "",
            };
            item.SubItems.Add(Rank(s) switch { 0 => "Running", 1 => "Starts with Windows", 2 => "Installed", _ => "" });
            item.SubItems.Add(Ui.Mb(s.App?.WorkingSet ?? 0));
            item.SubItems.Add(RuleText.Describe(s.Rule));
            item.SubItems.Add(s.Category);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _filling = false;
        ShowDetails();
        UpdateButton();
    }

    static int Rank(AppSuggestion s) => s.IsRunning ? 0 : s.Startup is not null ? 1 : s.Installed ? 2 : 3;

    static bool Matches(AppSuggestion s, string query) =>
        s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || s.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
        || s.Rule.Processes.Any(p => p.Contains(query, StringComparison.OrdinalIgnoreCase));

    void ShowDetails()
    {
        if (_list.SelectedItems.Count == 0)
        {
            _details.Text = "Tick the apps to add. Known apps come with settings that suit them; you can change any of it afterwards.";
            return;
        }
        var s = (AppSuggestion)_list.SelectedItems[0].Tag!;
        var lines = new List<string>();
        if (s.Rule.Processes.Count > 0)
            lines.Add($"Processes: {string.Join(", ", s.Rule.Processes)}");
        if (s.App?.Main.Path is { } path)
            lines.Add($"Program: {path}");
        else if (s.Startup?.Exe is { } exe)
            lines.Add($"Program: {exe}");
        if (s.Startup is { } entry)
            lines.Add($"Starts with Windows ({entry.Location}{(entry.Enabled ? "" : ", turned off")}): {entry.Command}");
        if (s.Recipe?.Notes is { } notes)
            lines.Add(notes);
        if (s.Services.Count > 0)
            lines.Add($"Also stops: {string.Join(", ", s.Services.Select(x => x.Name))}");
        _details.Text = string.Join(Environment.NewLine, lines);
    }

    void UpdateButton()
    {
        _add.Enabled = _checked.Count > 0;
        _add.Text = _checked.Count switch { 0 => "Add", 1 => "Add 1 app", var n => $"Add {n} apps" };
    }
}
