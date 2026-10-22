using PlayMode.Core.Discovery;
using PlayMode.Core.Model;

namespace PlayMode.App.Settings;

/// <summary>Edits one app rule in plain words. Raises <see cref="Changed"/> with the new rule on every edit.</summary>
sealed class RuleEditor : UserControl
{
    readonly TextBox _id = new() { Width = 260 };
    readonly TextBox _processes = new() { Width = 360, PlaceholderText = "e.g. Code, Docker Desktop, com.docker.*" };
    readonly TextBox _pathLike = new() { Width = 360, PlaceholderText = @"optional, e.g. *\AnthropicClaude\*" };
    readonly ComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly CheckBox _force = new() { Text = "End it right away (for apps that ignore a close or relaunch themselves)", AutoSize = true };
    readonly CheckBox _risky = new() { Text = "Ask before closing it (it may hold unsaved work)", AutoSize = true };
    readonly CheckBox _restore = new() { Text = "Start it again afterwards, if it was running", AutoSize = true };
    readonly TextBox _start = new() { Width = 360, PlaceholderText = "the program it was running from" };
    readonly TextBox _args = new() { Width = 360, PlaceholderText = "e.g. --minimized" };
    readonly ComboBox _window = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly TextBox _windowTitle = new() { Width = 260, PlaceholderText = "any title (wildcards allowed)" };
    readonly Label _notes = Ui.Label("", muted: true);
    readonly Label _kindNote = Ui.Label("");
    readonly Control[] _processOnly;
    readonly Control[] _restoreOnly;
    AppRule? _rule;
    bool _loading;

    public event Action<AppRule>? Changed;

    public RuleEditor()
    {
        _action.Items.AddRange(["Close it", "Freeze it (suspend)"]);
        _window.Items.AddRange(["Leave its windows alone", "Close the window it opens", "Hide the window it opens"]);
        _notes.MaximumSize = _kindNote.MaximumSize = new Size(440, 0);

        var browse = Ui.Button("Browse…", (_, _) => Browse());
        var processesLabel = Ui.Label("Processes (without .exe; * and ? allowed)");
        var pathLabel = Ui.Label("Only when its program is in a folder like");
        var actionLabel = Ui.Label("When switching to play mode");
        var afterLabel = Ui.Label("Afterwards", bold: true);
        var startLabel = Ui.Label("Start");
        var argsLabel = Ui.Label("With arguments");
        var windowLabel = Ui.Label("After starting it");
        var titleLabel = Ui.Label("Window title");
        var startRow = Ui.Row(_start, browse);
        _processOnly = [processesLabel, _processes, pathLabel, _pathLike, actionLabel, _action, _force, afterLabel, _restore];
        _restoreOnly = [startLabel, startRow, argsLabel, _args, windowLabel, _window, titleLabel, _windowTitle];

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 4, 12, 12) };
        layout.Controls.AddRange(
        [
            Ui.Label("Name"), _id, _kindNote,
            processesLabel, _processes, pathLabel, _pathLike,
            actionLabel, _action, _force, _risky,
            afterLabel, _restore, startLabel, startRow, argsLabel, _args,
            windowLabel, _window, titleLabel, _windowTitle,
            _notes,
        ]);
        Controls.Add(layout);

        foreach (var box in new[] { _id, _processes, _pathLike, _start, _args, _windowTitle })
            box.TextChanged += (_, _) => Edited();
        foreach (var check in new[] { _force, _risky, _restore })
            check.CheckedChanged += (_, _) => Edited();
        _action.SelectedIndexChanged += (_, _) => Edited();
        _window.SelectedIndexChanged += (_, _) => Edited();
        Edit(null, null);
    }

    public void Edit(AppRule? rule, CatalogApp? recipe)
    {
        _loading = true;
        _rule = rule;
        Enabled = rule is not null;
        rule ??= new AppRule { Id = "" };
        _id.Text = rule.Id;
        _processes.Text = string.Join(", ", rule.Processes);
        _pathLike.Text = rule.PathLike ?? "";
        _action.SelectedIndex = rule.Action == CloseAction.Suspend ? 1 : 0;
        _force.Checked = rule.Force;
        _risky.Checked = rule.Risky;
        _restore.Checked = rule.Restore;
        _start.Text = rule.Start ?? "";
        _args.Text = rule.Args ?? "";
        _window.SelectedIndex = (int)rule.Window;
        _windowTitle.Text = rule.WindowTitle ?? "";
        _notes.Text = rule.Kind == AppKind.Process ? recipe?.Notes ?? "" : "";   // WSL and Tailscale get their own note
        _kindNote.Text = rule.Kind switch
        {
            AppKind.Wsl => "Shuts down every WSL distro (wsl --shutdown). They start again the next time you use them.",
            AppKind.Tailscale => "Disconnects Tailscale, then reconnects it afterwards with the same settings. Only if it was connected.",
            _ => "",
        };
        _loading = false;
        UpdateEnabled();
    }

    void Edited()
    {
        UpdateEnabled();
        if (_loading || _rule is null)
            return;
        var processes = _processes.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _rule = _rule with
        {
            Id = _id.Text.Trim(),
            Processes = _rule.Kind == AppKind.Process ? processes : _rule.Processes,
            PathLike = Value(_pathLike),
            Action = _action.SelectedIndex == 1 ? CloseAction.Suspend : CloseAction.Close,
            Force = _force.Checked,
            Risky = _risky.Checked,
            Restore = _restore.Checked,
            Start = Value(_start),
            Args = Value(_args),
            Window = (WindowAction)Math.Max(0, _window.SelectedIndex),
            WindowTitle = Value(_windowTitle),
        };
        Changed?.Invoke(_rule);
    }

    void UpdateEnabled()
    {
        var kind = _rule?.Kind ?? AppKind.Process;
        foreach (var c in _processOnly)
            c.Visible = kind == AppKind.Process;
        foreach (var c in _restoreOnly)
            c.Visible = kind == AppKind.Process;
        _risky.Visible = kind != AppKind.Tailscale;
        _kindNote.Visible = kind != AppKind.Process;

        // A frozen app is simply unfrozen, so how to start it doesn't apply.
        var closes = _action.SelectedIndex != 1;
        _force.Enabled = closes;
        _restore.Enabled = closes;
        foreach (var c in _restoreOnly)
            c.Enabled = closes && _restore.Checked;
        _windowTitle.Enabled = _window.Enabled && _window.SelectedIndex > 0;
    }

    void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "Programs and shortcuts|*.exe;*.lnk|All files|*.*", DereferenceLinks = false };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _start.Text = EnvPaths.Collapse(dialog.FileName);
    }

    static string? Value(TextBox box) => box.Text.Trim() is { Length: > 0 } text ? text : null;
}
