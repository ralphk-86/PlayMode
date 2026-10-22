using PlayMode.Core.Discovery;
using PlayMode.Core.Model;

namespace PlayMode.App.Settings;

/// <summary>Pick services to stop while playing.</summary>
sealed class AddServicesForm : Form
{
    readonly Func<bool, IReadOnlyList<ServiceSuggestion>> _suggest;
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true };
    readonly CheckBox _windows = new() { Text = "Show Windows services too", AutoSize = true };
    readonly Button _add;
    readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<ServiceSuggestion> _shown = [];
    bool _filling;

    public IReadOnlyList<ServiceRule> Picked => [.. _shown.Where(s => _checked.Contains(s.Rule.Name)).Select(s => s.Rule)];

    /// <param name="suggest">Suggestions, with or without Windows' own services.</param>
    public AddServicesForm(Func<bool, IReadOnlyList<ServiceSuggestion>> suggest, string profileName)
    {
        _suggest = suggest;
        Text = $"Add services to {profileName}";
        Icon = TrayIcons.Dev;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(900, 560);
        MinimumSize = new Size(640, 360);
        Padding = new Padding(12);

        _list.Columns.Add("Service", 300);
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("Status", 80);
        _list.Columns.Add("Notes", 260);
        _list.ItemCheck += (_, e) =>
        {
            if (_filling)
                return;
            var s = (ServiceSuggestion)_list.Items[e.Index].Tag!;
            if (s.AlreadyAdded)
                e.NewValue = CheckState.Unchecked;
            else if (e.NewValue == CheckState.Checked)
                _checked.Add(s.Rule.Name);
            else
                _checked.Remove(s.Rule.Name);
            BeginInvoke(() => _add!.Enabled = _checked.Count > 0);
        };
        _windows.CheckedChanged += (_, _) => Fill();

        _add = new Button { Text = "Add", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false, Padding = new Padding(8, 2, 8, 2) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 2, 8, 2) };
        AcceptButton = _add;
        CancelButton = cancel;

        var note = Ui.Label("Only services from other vendors are listed at first. Stopping a Windows service can break things; "
            + "PlayMode never lists the ones games and anti-cheat need.", muted: true);
        note.MaximumSize = new Size(840, 0);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 0, 0, 8) };
        top.Controls.AddRange([note, _windows]);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.AddRange([cancel, _add]);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);
        Fill();
    }

    void Fill()
    {
        _filling = true;
        _shown = _suggest(_windows.Checked);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in _shown)
        {
            var item = new ListViewItem(s.AlreadyAdded ? $"{s.Title} (added)" : s.Title)
            {
                Tag = s,
                Checked = _checked.Contains(s.Rule.Name),
                ForeColor = s.AlreadyAdded ? SystemColors.GrayText : _list.ForeColor,
                ToolTipText = s.Notes ?? "",
            };
            item.SubItems.Add(s.Rule.Name);
            item.SubItems.Add(s.Running ? "Running" : "Stopped");
            item.SubItems.Add(s.BelongsTo is { } app ? $"Belongs with {app}" : s.Notes ?? (s.IsWindows ? "Part of Windows" : ""));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _filling = false;
    }
}
