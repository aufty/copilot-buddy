using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal sealed class CopilotSettingsDialog : Form
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
    private readonly TextBox command = new();
    private readonly Label validation = new();
    private readonly Label shortcutValidation = new();
    private readonly Button save = new() { Text = "Confirm" };
    private readonly List<Image> avatarImages = [];
    private readonly Dictionary<string, ShortcutCaptureBox> shortcutInputs = [];
    private readonly Func<SessionShortcutSettings, string?> applyShortcuts;
    private string selectedBuddy;
    private SettingsDialogResult? result;

    private CopilotSettingsDialog(
        float dpiScale,
        AssistantLaunchCommand currentCommand,
        string currentBuddy,
        int currentDisplayScalePercent,
        bool careSystemEnabled,
        bool ambientQuipsEnabled,
        IReadOnlyList<BuddySprite> buddies,
        SessionShortcutSettings currentShortcuts,
        Func<SessionShortcutSettings, string?> applyShortcuts)
    {
        this.applyShortcuts = applyShortcuts;
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf"));
        float scale = Math.Max(1, dpiScale);
        selectedBuddy = buddies.FirstOrDefault(buddy =>
                string.Equals(buddy.Name, currentBuddy, StringComparison.OrdinalIgnoreCase))?.Name
            ?? buddies.FirstOrDefault(buddy =>
                string.Equals(buddy.Name, BuddySpriteCatalog.DefaultName, StringComparison.OrdinalIgnoreCase))?.Name
            ?? buddies[0].Name;
        Font = new Font(fonts.Families[0], 21 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "Copilot Buddy settings";
        AccessibleName = "Copilot Buddy settings";
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
        ClientSize = new Size((int)(680 * scale), (int)(690 * scale));

        Label heading = new()
        {
            Text = "SETTINGS",
            ForeColor = AccentColor,
            Bounds = Scale(new Rectangle(14, 12, 260, 38), scale)
        };
        Label displayScaleLabel = new()
        {
            Text = "DISPLAY SCALE",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(300, 12, 174, 38), scale),
            TextAlign = ContentAlignment.MiddleRight
        };
        ComboBox displayScale = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Bounds = Scale(new Rectangle(486, 15, 178, 32), scale),
            BackColor = PanelColor,
            ForeColor = TextColor,
            AccessibleName = "Display scale"
        };
        displayScale.Items.AddRange(["Default (100%)", "150%", "200%"]);
        displayScale.SelectedIndex = currentDisplayScalePercent switch
        {
            150 => 1,
            200 => 2,
            _ => 0
        };
        Label buddyLabel = new()
        {
            Text = "BUDDY",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 68, 648, 28), scale)
        };
        FlowLayoutPanel buddyOptions = new()
        {
            Bounds = Scale(new Rectangle(16, 99, 648, 112), scale),
            AutoScroll = true,
            WrapContents = false,
            BackColor = BackgroundColor,
            AccessibleName = "Buddy sprite"
        };
        foreach (BuddySprite buddy in buddies)
        {
            RadioButton option = new()
            {
                Text = buddy.Name,
                Tag = buddy.Name,
                Checked = string.Equals(buddy.Name, selectedBuddy, StringComparison.OrdinalIgnoreCase),
                Appearance = Appearance.Button,
                AutoSize = false,
                Size = Scale(new Size(94, 96), scale),
                FlatStyle = FlatStyle.Flat,
                BackColor = PanelColor,
                ForeColor = TextColor,
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                TextImageRelation = TextImageRelation.ImageAboveText,
                AccessibleName = buddy.Name
            };
            option.FlatAppearance.BorderSize = Math.Max(1, (int)(2 * scale));
            option.FlatAppearance.BorderColor = EdgeColor;
            option.FlatAppearance.CheckedBackColor = HoverColor;
            option.FlatAppearance.MouseOverBackColor = HoverColor;
            option.FlatAppearance.MouseDownBackColor = HoverColor;
            option.Image = CreateAvatar(buddy.Path, scale);
            avatarImages.Add(option.Image);
            option.CheckedChanged += (_, _) =>
            {
                if (option.Checked)
                {
                    selectedBuddy = (string)option.Tag;
                }
            };
            buddyOptions.Controls.Add(option);
        }

        Label commandLabel = new()
        {
            Text = "COPILOT LAUNCH COMMAND",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 224, 648, 28), scale)
        };
        command.Text = currentCommand.ToString();
        command.Bounds = Scale(new Rectangle(16, 257, 648, 36), scale);
        command.BackColor = PanelColor;
        command.ForeColor = TextColor;
        command.BorderStyle = BorderStyle.FixedSingle;
        command.AccessibleName = "Copilot launch command";

        Label hint = new()
        {
            Text = "Buddy appends its UI server, port, and session arguments.",
            ForeColor = Color.FromArgb(182, 177, 195),
            Bounds = Scale(new Rectangle(16, 299, 648, 26), scale)
        };
        validation.ForeColor = ErrorColor;
        validation.Bounds = Scale(new Rectangle(16, 327, 648, 26), scale);

        Label careSystemLabel = new()
        {
            Text = "CARE SYSTEM",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 356, 172, 32), scale),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Label careSystemHint = new()
        {
            Text = "Needs stay fully satisfied while off.",
            ForeColor = Color.FromArgb(182, 177, 195),
            Bounds = Scale(new Rectangle(190, 356, 350, 32), scale),
            TextAlign = ContentAlignment.MiddleLeft
        };
        CheckBox careSystem = new()
        {
            Checked = careSystemEnabled,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Bounds = Scale(new Rectangle(554, 356, 110, 32), scale),
            BackColor = PanelColor,
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = "Care System"
        };
        careSystem.FlatAppearance.BorderSize = Math.Max(1, (int)(2 * scale));
        careSystem.FlatAppearance.BorderColor = EdgeColor;
        careSystem.FlatAppearance.CheckedBackColor = AccentColor;
        careSystem.FlatAppearance.MouseOverBackColor = HoverColor;
        careSystem.FlatAppearance.MouseDownBackColor = HoverColor;
        void UpdateCareSystemToggle()
        {
            careSystem.Text = careSystem.Checked ? "ON" : "OFF";
            careSystem.ForeColor = careSystem.Checked ? BackgroundColor : TextColor;
        }
        careSystem.CheckedChanged += (_, _) => UpdateCareSystemToggle();
        UpdateCareSystemToggle();

        Label ambientQuipsLabel = new()
        {
            Text = "AMBIENT QUIPS",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 400, 172, 32), scale),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Label ambientQuipsHint = new()
        {
            Text = "Occasional dry observations.",
            ForeColor = Color.FromArgb(182, 177, 195),
            Bounds = Scale(new Rectangle(190, 400, 350, 32), scale),
            TextAlign = ContentAlignment.MiddleLeft
        };
        CheckBox ambientQuips = new()
        {
            Checked = ambientQuipsEnabled,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Bounds = Scale(new Rectangle(554, 400, 110, 32), scale),
            BackColor = PanelColor,
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = "Ambient Quips"
        };
        ambientQuips.FlatAppearance.BorderSize = Math.Max(1, (int)(2 * scale));
        ambientQuips.FlatAppearance.BorderColor = EdgeColor;
        ambientQuips.FlatAppearance.CheckedBackColor = AccentColor;
        ambientQuips.FlatAppearance.MouseOverBackColor = HoverColor;
        ambientQuips.FlatAppearance.MouseDownBackColor = HoverColor;
        void UpdateAmbientQuipsToggle()
        {
            ambientQuips.Text = ambientQuips.Checked ? "ON" : "OFF";
            ambientQuips.ForeColor = ambientQuips.Checked ? BackgroundColor : TextColor;
        }
        ambientQuips.CheckedChanged += (_, _) => UpdateAmbientQuipsToggle();
        UpdateAmbientQuipsToggle();

        Label shortcutsLabel = new()
        {
            Text = "SHORTCUTS",
            ForeColor = HighlightColor,
            Bounds = Scale(new Rectangle(16, 444, 648, 28), scale)
        };
        Label shortcutsHint = new()
        {
            Text = "Click a shortcut box, then press a chord with Alt, Ctrl, or Shift.",
            ForeColor = Color.FromArgb(182, 177, 195),
            Bounds = Scale(new Rectangle(16, 472, 648, 26), scale)
        };
        AddShortcutRow("Summon", currentShortcuts.Summon, 502, scale);
        AddShortcutRow("Buddy Menu", currentShortcuts.SkillMenu, 536, scale);
        AddShortcutRow("Inquire", currentShortcuts.Inquire, 570, scale);
        AddShortcutRow("Handoff", currentShortcuts.Handoff, 604, scale);
        AddShortcutRow("Gather", currentShortcuts.Gather, 638, scale);
        AddShortcutRow("Store", currentShortcuts.Store, 672, scale);
        shortcutValidation.ForeColor = ErrorColor;
        shortcutValidation.Bounds = Scale(new Rectangle(16, 708, 648, 26), scale);

        Button reset = new() { Text = "Reset" };
        ConfigureButton(reset, new Rectangle(16, 730, 110, 38), scale);
        ConfigureButton(save, new Rectangle(436, 730, 110, 38), scale);
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        ConfigureButton(cancel, new Rectangle(554, 730, 110, 38), scale);
        ClientSize = new Size((int)(680 * scale), (int)(778 * scale));

        command.TextChanged += (_, _) => ValidateCommand();
        reset.Click += (_, _) =>
        {
            SetShortcuts(SessionShortcutSettings.Default);
            shortcutValidation.Text = "";
        };
        save.Click += (_, _) =>
        {
            if (TryParseCommand(out AssistantLaunchCommand? parsed) &&
                TryReadShortcuts(out SessionShortcutSettings? shortcuts))
            {
                string? shortcutError = applyShortcuts(shortcuts!);
                if (shortcutError is not null)
                {
                    shortcutValidation.Text = shortcutError;
                    return;
                }
                result = new SettingsDialogResult(
                    parsed!, selectedBuddy, displayScale.SelectedIndex switch
                    {
                        1 => 150,
                        2 => 200,
                        _ => 100
                    }, careSystem.Checked, ambientQuips.Checked, shortcuts!);
                DialogResult = DialogResult.OK;
            }
        };

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([
            heading, displayScaleLabel, displayScale, buddyLabel, buddyOptions, commandLabel, command, hint, validation,
            careSystemLabel, careSystemHint, careSystem, shortcutsLabel, shortcutsHint,
            ambientQuipsLabel, ambientQuipsHint, ambientQuips,
            shortcutValidation, reset, save, cancel
        ]);
        ValidateCommand();
    }

    public static SettingsDialogResult? Edit(
        IWin32Window owner,
        float dpiScale,
        AssistantLaunchCommand currentCommand,
        string currentBuddy,
        int currentDisplayScalePercent,
        bool careSystemEnabled,
        bool ambientQuipsEnabled,
        SessionShortcutSettings currentShortcuts,
        Func<SessionShortcutSettings, string?> applyShortcuts)
    {
        IReadOnlyList<BuddySprite> buddies = BuddySpriteCatalog.GetAvailable();
        if (buddies.Count == 0)
        {
            throw new InvalidOperationException(
                $"No {BuddySpriteCatalog.SheetWidth}x{BuddySpriteCatalog.SheetHeight} buddy sprite sheets were found.");
        }
        using CopilotSettingsDialog dialog = new(
            dpiScale, currentCommand, currentBuddy, currentDisplayScalePercent,
            careSystemEnabled, ambientQuipsEnabled,
            buddies, currentShortcuts, applyShortcuts);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.result : null;
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        SetForegroundWindow(Handle);
        Activate();
        command.Focus();
        command.SelectAll();
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

    private void ValidateCommand()
    {
        bool valid = TryParseCommand(out _);
        save.Enabled = valid;
    }

    private bool TryParseCommand(out AssistantLaunchCommand? parsed)
    {
        try
        {
            parsed = AssistantLaunchCommand.Parse(command.Text);
            validation.Text = "";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            parsed = null;
            validation.Text = exception.Message;
            return false;
        }
    }

    private void AddShortcutRow(string name, string shortcut, int y, float scale)
    {
        Label label = new()
        {
            Text = name,
            ForeColor = TextColor,
            Bounds = Scale(new Rectangle(16, y, 172, 30), scale),
            TextAlign = ContentAlignment.MiddleLeft
        };
        ShortcutCaptureBox input = new()
        {
            Text = shortcut,
            Bounds = Scale(new Rectangle(190, y, 474, 30), scale),
            BackColor = PanelColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = $"{name} shortcut"
        };
        input.ShortcutCaptured += (_, _) => shortcutValidation.Text = "";
        input.CaptureRejected += message => shortcutValidation.Text = message;
        shortcutInputs.Add(name, input);
        Controls.Add(label);
        Controls.Add(input);
    }

    private bool TryReadShortcuts(out SessionShortcutSettings? shortcuts)
    {
        shortcuts = new SessionShortcutSettings(
            shortcutInputs["Summon"].Text,
            shortcutInputs["Buddy Menu"].Text,
            shortcutInputs["Inquire"].Text,
            shortcutInputs["Handoff"].Text,
            shortcutInputs["Gather"].Text,
            shortcutInputs["Store"].Text);
        Dictionary<ShortcutBinding, string> used = [];
        try
        {
            foreach ((string name, string shortcut) in shortcuts.All())
            {
                ShortcutBinding binding = ShortcutBinding.Parse(shortcut);
                if (used.TryGetValue(binding, out string? existing))
                {
                    shortcutValidation.Text = $"{name} and {existing} use the same shortcut.";
                    shortcuts = null;
                    return false;
                }
                used.Add(binding, name);
            }
            shortcutValidation.Text = "";
            return true;
        }
        catch (ArgumentException exception)
        {
            shortcutValidation.Text = exception.Message;
            shortcuts = null;
            return false;
        }
    }

    private void SetShortcuts(SessionShortcutSettings shortcuts)
    {
        shortcutInputs["Summon"].Text = shortcuts.Summon;
        shortcutInputs["Buddy Menu"].Text = shortcuts.SkillMenu;
        shortcutInputs["Inquire"].Text = shortcuts.Inquire;
        shortcutInputs["Handoff"].Text = shortcuts.Handoff;
        shortcutInputs["Gather"].Text = shortcuts.Gather;
        shortcutInputs["Store"].Text = shortcuts.Store;
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

    private static Rectangle Scale(Rectangle bounds, float scale) => new(
        (int)(bounds.X * scale), (int)(bounds.Y * scale),
        (int)(bounds.Width * scale), (int)(bounds.Height * scale));

    private static Size Scale(Size size, float scale) => new(
        (int)(size.Width * scale), (int)(size.Height * scale));

    private static Bitmap CreateAvatar(string path, float scale)
    {
        const int previewScale = 3;
        using Bitmap sheet = new(path);
        int width = (int)(BuddySpriteCatalog.FrameWidth * previewScale * scale);
        int height = (int)(BuddySpriteCatalog.FrameHeight * previewScale * scale);
        Bitmap avatar = new(width, height);
        using Graphics graphics = Graphics.FromImage(avatar);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(sheet,
            new Rectangle(0, 0, width, height),
            new Rectangle(0, 0, BuddySpriteCatalog.FrameWidth, BuddySpriteCatalog.FrameHeight),
            GraphicsUnit.Pixel);
        return avatar;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Image image in avatarImages)
            {
                image.Dispose();
            }
            fonts.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
}

internal sealed record SettingsDialogResult(
    AssistantLaunchCommand CopilotLaunch,
    string Buddy,
    int DisplayScalePercent,
    bool CareSystemEnabled,
    bool AmbientQuipsEnabled,
    SessionShortcutSettings Shortcuts);

internal sealed class ShortcutCaptureBox : TextBox
{
    public ShortcutCaptureBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event EventHandler? ShortcutCaptured;
    public event Action<string>? CaptureRejected;

    protected override void OnClick(EventArgs args)
    {
        base.OnClick(args);
        Focus();
        SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        args.SuppressKeyPress = true;
        args.Handled = true;
        if (args.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
        {
            return;
        }
        try
        {
            Text = ShortcutBinding.FromKeyData(args.KeyData);
            SelectAll();
            ShortcutCaptured?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException)
        {
            CaptureRejected?.Invoke("Choose a key with Alt, Ctrl, or Shift.");
        }
    }
}
