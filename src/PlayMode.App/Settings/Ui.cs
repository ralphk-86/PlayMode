namespace PlayMode.App.Settings;

/// <summary>Small layout helpers so the forms read top to bottom without a designer file.</summary>
static class Ui
{
    public static Label Label(string text, bool muted = false, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 6, 8, 2),
        ForeColor = muted ? SystemColors.GrayText : SystemColors.ControlText,
        Font = bold ? new Font(Control.DefaultFont, FontStyle.Bold) : Control.DefaultFont,
    };

    public static Button Button(string text, EventHandler click, bool autoSize = true)
    {
        var button = new Button { Text = text, AutoSize = autoSize, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), UseVisualStyleBackColor = true };
        button.Click += click;
        return button;
    }

    /// <summary>A link that stays readable in dark mode, where the default dark blue nearly disappears.</summary>
    public static LinkLabel Link(string text, Action click)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
        if (Application.IsDarkModeEnabled)
            link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = Color.FromArgb(0x6C, 0xB8, 0xFF);
        link.LinkClicked += (_, _) => click();
        return link;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>Asks for one line of text; null if cancelled.</summary>
    public static string? Prompt(IWin32Window owner, string title, string question, string value = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        var box = new TextBox { Text = value, Width = 320 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.AddRange([Label(question), box, Row(ok, cancel)]);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    public static void Error(IWin32Window owner, string text) =>
        MessageBox.Show(owner, text, "PlayMode", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static bool Confirm(IWin32Window owner, string text) =>
        MessageBox.Show(owner, text, "PlayMode", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public static string Mb(long bytes) => bytes <= 0 ? "" : $"{bytes / (1024 * 1024):N0} MB";
}
