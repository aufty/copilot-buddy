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
    private readonly Button save = new() { Text = "Save" };
    private readonly List<Image> avatarImages = [];
    private string selectedBuddy;
    private SettingsDialogResult? result;

    private CopilotSettingsDialog(
        float dpiScale,
        AssistantLaunchCommand currentCommand,
        string currentBuddy,
        IReadOnlyList<BuddySprite> buddies)
    {
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
        ClientSize = new Size((int)(680 * scale), (int)(405 * scale));

        Label heading = new()
        {
            Text = "SETTINGS",
            ForeColor = AccentColor,
            Bounds = Scale(new Rectangle(14, 12, 652, 38), scale)
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

        Button reset = new() { Text = "Use default" };
        ConfigureButton(reset, new Rectangle(16, 354, 120, 38), scale);
        ConfigureButton(save, new Rectangle(436, 354, 110, 38), scale);
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        ConfigureButton(cancel, new Rectangle(554, 354, 110, 38), scale);

        command.TextChanged += (_, _) => ValidateCommand();
        reset.Click += (_, _) =>
        {
            command.Text = AssistantLaunchCommand.Default.ToString();
            command.Focus();
            command.SelectAll();
        };
        save.Click += (_, _) =>
        {
            if (TryParseCommand(out AssistantLaunchCommand? parsed))
            {
                result = new SettingsDialogResult(parsed!, selectedBuddy);
                DialogResult = DialogResult.OK;
            }
        };

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([
            heading, buddyLabel, buddyOptions, commandLabel, command, hint, validation, reset, save, cancel
        ]);
        ValidateCommand();
    }

    public static SettingsDialogResult? Edit(
        IWin32Window owner,
        float dpiScale,
        AssistantLaunchCommand currentCommand,
        string currentBuddy)
    {
        IReadOnlyList<BuddySprite> buddies = BuddySpriteCatalog.GetAvailable();
        if (buddies.Count == 0)
        {
            throw new InvalidOperationException(
                $"No {BuddySpriteCatalog.SheetWidth}x{BuddySpriteCatalog.SheetHeight} buddy sprite sheets were found.");
        }
        using CopilotSettingsDialog dialog = new(dpiScale, currentCommand, currentBuddy, buddies);
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
        using Bitmap sheet = new(path);
        int width = (int)(BuddySpriteCatalog.FrameWidth * 4 * scale);
        int height = (int)(BuddySpriteCatalog.FrameHeight / 2 * 4 * scale);
        Bitmap avatar = new(width, height);
        using Graphics graphics = Graphics.FromImage(avatar);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(sheet,
            new Rectangle(0, 0, width, height),
            new Rectangle(0, 0, BuddySpriteCatalog.FrameWidth, BuddySpriteCatalog.FrameHeight / 2),
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

internal sealed record SettingsDialogResult(AssistantLaunchCommand CopilotLaunch, string Buddy);
