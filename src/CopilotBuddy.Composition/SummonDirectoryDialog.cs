using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CopilotBuddy.Composition;

internal sealed class SummonDirectoryDialog : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(31, 29, 38);
    private static readonly Color PanelColor = Color.FromArgb(42, 39, 51);
    private static readonly Color HoverColor = Color.FromArgb(63, 83, 96);
    private static readonly Color EdgeColor = Color.FromArgb(75, 71, 88);
    private static readonly Color AccentColor = Color.FromArgb(91, 216, 211);
    private static readonly Color HighlightColor = Color.FromArgb(255, 203, 77);
    private static readonly Color TextColor = Color.FromArgb(247, 244, 231);
    private static readonly Color ErrorColor = Color.FromArgb(245, 108, 108);
    private readonly PrivateFontCollection fonts = new();
    private readonly TextBox pathInput = new();
    private readonly ListBox recentPaths = new();
    private readonly Label validation = new();
    private readonly Button summonButton = new() { Text = "Summon" };
    private string? selectedPath;

    private SummonDirectoryDialog(
        float dpiScale,
        string? lastWorkingDirectory,
        IEnumerable<string> workingDirectoryHistory)
    {
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf"));
        float scale = Math.Max(1, dpiScale);
        Font = new Font(fonts.Families[0], 21 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "Summon Copilot";
        AccessibleName = "Choose a project or directory";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BackgroundColor;
        ForeColor = TextColor;
        ClientSize = new Size((int)(680 * scale), (int)(460 * scale));

        string initialPath = string.IsNullOrWhiteSpace(lastWorkingDirectory)
            ? "~"
            : lastWorkingDirectory;
        string[] history = workingDirectoryHistory
            .Prepend(lastWorkingDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray()!;

        Label heading = new()
        {
            Text = "SUMMON COPILOT",
            ForeColor = AccentColor,
            Bounds = Scale(new Rectangle(14, 12, 652, 38), scale)
        };
        Label lastLabel = new()
        {
            Text = "PROJECT / DIRECTORY",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 64, 648, 28), scale)
        };
        pathInput.Bounds = Scale(new Rectangle(16, 96, 536, 36), scale);
        pathInput.BackColor = PanelColor;
        pathInput.ForeColor = TextColor;
        pathInput.BorderStyle = BorderStyle.FixedSingle;
        pathInput.AccessibleName = "Project or directory path";
        pathInput.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        pathInput.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
        pathInput.Text = initialPath;

        Button browseButton = new() { Text = "Browse" };
        ConfigureButton(browseButton, new Rectangle(560, 96, 104, 36), scale);
        browseButton.Click += (_, _) => BrowseForDirectory();

        Label recentLabel = new()
        {
            Text = "RECENTLY OPENED   ↑/k  ↓/j",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 151, 648, 28), scale)
        };
        recentPaths.Bounds = Scale(new Rectangle(16, 182, 648, 174), scale);
        recentPaths.BackColor = PanelColor;
        recentPaths.ForeColor = TextColor;
        recentPaths.BorderStyle = BorderStyle.FixedSingle;
        recentPaths.IntegralHeight = false;
        recentPaths.AccessibleName = "Recently opened projects and directories";
        recentPaths.ItemHeight = (int)Math.Round(30 * scale);
        recentPaths.DrawMode = DrawMode.OwnerDrawFixed;
        recentPaths.DrawItem += DrawRecentPath;
        recentPaths.Items.AddRange(history);
        if (recentPaths.Items.Count == 0)
        {
            recentPaths.Items.Add("~");
        }
        recentPaths.SelectedIndex = 0;
        recentPaths.SelectedIndexChanged += (_, _) =>
        {
            if (recentPaths.SelectedItem is string path)
            {
                pathInput.Text = path;
                pathInput.SelectionStart = pathInput.TextLength;
            }
        };
        recentPaths.DoubleClick += (_, _) => ConfirmSelection();

        validation.Bounds = Scale(new Rectangle(16, 365, 648, 28), scale);
        validation.ForeColor = ErrorColor;
        validation.AutoEllipsis = true;
        validation.AccessibleName = "Path validation message";

        ConfigureButton(summonButton, new Rectangle(436, 405, 120, 38), scale);
        summonButton.Click += (_, _) => ConfirmSelection();
        Button cancelButton = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        ConfigureButton(cancelButton, new Rectangle(564, 405, 100, 38), scale);
        AcceptButton = summonButton;
        CancelButton = cancelButton;

        pathInput.TextChanged += (_, _) => validation.Text = "";
        pathInput.KeyDown += (_, args) =>
        {
            if (args.KeyCode is Keys.Up or Keys.Down)
            {
                MoveSelection(args.KeyCode == Keys.Up ? -1 : 1);
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };
        recentPaths.KeyDown += (_, args) =>
        {
            int delta = args.KeyCode switch
            {
                Keys.K => -1,
                Keys.J => 1,
                _ => 0
            };
            if (delta != 0)
            {
                MoveSelection(delta);
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };
        recentPaths.KeyPress += (_, args) =>
        {
            if (!char.IsControl(args.KeyChar) &&
                char.ToLowerInvariant(args.KeyChar) is not ('j' or 'k'))
            {
                pathInput.Text = args.KeyChar.ToString();
                pathInput.SelectionStart = pathInput.TextLength;
                pathInput.Focus();
                args.Handled = true;
            }
        };

        Controls.AddRange(
            [heading, lastLabel, pathInput, browseButton, recentLabel, recentPaths, validation,
                summonButton, cancelButton]);
    }

    public static string? Ask(
        IWin32Window owner,
        float dpiScale,
        string? lastWorkingDirectory,
        IEnumerable<string>? workingDirectoryHistory)
    {
        using SummonDirectoryDialog dialog =
            new(dpiScale, lastWorkingDirectory, workingDirectoryHistory ?? []);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.selectedPath
            : null;
    }

    public static string ResolveDefaultDirectory(string? workingDirectory)
    {
        if (TryResolveDirectory(workingDirectory ?? "~", out string? resolved))
        {
            return resolved!;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(systemRoot) && Directory.Exists(systemRoot))
        {
            return systemRoot;
        }
        if (Directory.Exists(Environment.CurrentDirectory))
        {
            return Environment.CurrentDirectory;
        }
        throw new DirectoryNotFoundException("No safe working directory is available.");
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Tab)
        {
            if (pathInput.Focused && CompletePath())
            {
                return true;
            }
            BrowseForDirectory();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        SetForegroundWindow(Handle);
        Activate();
        recentPaths.Select();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.None;
        float scale = DeviceDpi / 96f;
        using Pen shadow = new(Color.FromArgb(17, 16, 22), Math.Max(2, 4 * scale));
        using Pen edge = new(AccentColor, Math.Max(1, 3 * scale));
        args.Graphics.DrawRectangle(shadow, (int)(4 * scale), (int)(4 * scale),
            ClientSize.Width - (int)(8 * scale), ClientSize.Height - (int)(8 * scale));
        args.Graphics.DrawRectangle(edge, 1, 1, ClientSize.Width - 4, ClientSize.Height - 4);
        using Pen divider = new(HoverColor, Math.Max(1, 2 * scale));
        args.Graphics.DrawLine(divider, (int)(12 * scale), (int)(53 * scale),
            ClientSize.Width - (int)(13 * scale), (int)(53 * scale));
    }

    private void ConfirmSelection()
    {
        if (!TryResolveDirectory(pathInput.Text, out string? path))
        {
            validation.Text = "Choose an existing project or directory.";
            pathInput.Focus();
            pathInput.SelectAll();
            return;
        }
        selectedPath = path;
        DialogResult = DialogResult.OK;
    }

    private void BrowseForDirectory()
    {
        using FolderBrowserDialog browser = new()
        {
            Description = "Choose a project or directory for Copilot",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (TryResolveDirectory(pathInput.Text, out string? selected))
        {
            browser.SelectedPath = selected;
        }
        if (browser.ShowDialog(this) == DialogResult.OK)
        {
            pathInput.Text = browser.SelectedPath;
            pathInput.SelectionStart = pathInput.TextLength;
            pathInput.Focus();
        }
    }

    private bool CompletePath()
    {
        string expanded = ExpandHome(pathInput.Text.Trim());
        string? parent = Path.GetDirectoryName(expanded);
        string prefix = Path.GetFileName(expanded);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return false;
        }

        string[] matches;
        try
        {
            matches = Directory.EnumerateDirectories(parent, prefix + "*")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            validation.Text = "That path cannot be completed.";
            return true;
        }
        if (matches.Length == 0)
        {
            validation.Text = "No matching directory.";
            return true;
        }

        pathInput.Text = matches[0] + Path.DirectorySeparatorChar;
        pathInput.SelectionStart = pathInput.TextLength;
        return true;
    }

    private void MoveSelection(int delta)
    {
        if (recentPaths.Items.Count == 0)
        {
            return;
        }
        recentPaths.SelectedIndex =
            Math.Clamp(Math.Max(0, recentPaths.SelectedIndex) + delta, 0, recentPaths.Items.Count - 1);
    }

    private void DrawRecentPath(object? sender, DrawItemEventArgs args)
    {
        if (args.Index < 0)
        {
            return;
        }
        bool selected = (args.State & DrawItemState.Selected) != 0;
        using SolidBrush background = new(selected ? HoverColor : PanelColor);
        args.Graphics.FillRectangle(background, args.Bounds);
        string path = recentPaths.Items[args.Index]?.ToString() ?? "";
        Rectangle textBounds = Rectangle.Inflate(args.Bounds, -10, 0);
        TextRenderer.DrawText(args.Graphics, path, Font, textBounds,
            selected ? HighlightColor : TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (selected)
        {
            using Pen edge = new(HighlightColor, Math.Max(1, (int)(2 * DeviceDpi / 96f)));
            args.Graphics.DrawRectangle(edge, args.Bounds.X, args.Bounds.Y,
                args.Bounds.Width - 1, args.Bounds.Height - 1);
        }
        args.DrawFocusRectangle();
    }

    private void ConfigureButton(Button button, Rectangle bounds, float scale)
    {
        button.Bounds = Scale(bounds, scale);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = Math.Max(1, (int)(2 * scale));
        button.FlatAppearance.BorderColor = EdgeColor;
        button.FlatAppearance.MouseOverBackColor = HoverColor;
        button.FlatAppearance.MouseDownBackColor = HoverColor;
        button.BackColor = PanelColor;
        button.ForeColor = TextColor;
    }

    private static bool TryResolveDirectory(string path, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            string candidate = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(ExpandHome(path.Trim().Trim('"'))));
            if (!Directory.Exists(candidate))
            {
                return false;
            }
            resolved = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            path.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }
        return path;
    }

    private static Rectangle Scale(Rectangle bounds, float scale) => new(
        (int)(bounds.X * scale), (int)(bounds.Y * scale),
        (int)(bounds.Width * scale), (int)(bounds.Height * scale));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fonts.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
}
