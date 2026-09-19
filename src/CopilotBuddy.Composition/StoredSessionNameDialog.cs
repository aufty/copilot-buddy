using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal sealed class StoredSessionNameDialog : Form
{
    private static readonly Color PanelColor = Color.FromArgb(42, 39, 51);
    private static readonly Color HoverColor = Color.FromArgb(63, 83, 96);
    private static readonly Color EdgeColor = Color.FromArgb(75, 71, 88);
    private static readonly Color AccentColor = Color.FromArgb(91, 216, 211);
    private static readonly Color TextColor = Color.FromArgb(247, 244, 231);
    private readonly PrivateFontCollection fonts = new();
    private readonly TextBox nameInput = new() { MaxLength = 80 };
    private readonly Button storeButton = new() { Text = "Store" };

    private StoredSessionNameDialog(float dpiScale, AssistantWindowBounds? targetBounds)
    {
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf"));
        float scale = Math.Max(1, dpiScale);
        Font = new Font(fonts.Families[0], 21 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "Store session";
        AccessibleName = "Name stored session";
        FormBorderStyle = FormBorderStyle.None;
        bool centerOnTarget = targetBounds is { Width: > 0, Height: > 0 };
        StartPosition = centerOnTarget ? FormStartPosition.Manual : FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(31, 29, 38);
        ForeColor = TextColor;
        ClientSize = new Size((int)(500 * scale), (int)(190 * scale));
        if (centerOnTarget)
        {
            AssistantWindowBounds bounds = targetBounds!;
            Rectangle target = new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            Rectangle area = Screen.FromRectangle(target).WorkingArea;
            Location = new Point(
                Math.Clamp(target.Left + (target.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(target.Top + (target.Height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }

        Label heading = new()
        {
            Text = "NAME STORED SESSION",
            ForeColor = AccentColor,
            Bounds = Scale(new Rectangle(14, 12, 472, 38), scale)
        };
        Label prompt = new()
        {
            Text = "What should this session be called?",
            Bounds = Scale(new Rectangle(16, 64, 468, 28), scale)
        };
        nameInput.Bounds = Scale(new Rectangle(16, 96, 468, 34), scale);
        nameInput.BackColor = PanelColor;
        nameInput.ForeColor = TextColor;
        nameInput.BorderStyle = BorderStyle.FixedSingle;
        nameInput.AccessibleName = "Stored session name";
        ConfigureButton(storeButton, new Rectangle(276, 140, 100, 38), scale);
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        ConfigureButton(cancel, new Rectangle(384, 140, 100, 38), scale);
        nameInput.TextChanged += (_, _) =>
            storeButton.Enabled = !string.IsNullOrWhiteSpace(nameInput.Text);
        storeButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameInput.Text))
            {
                DialogResult = DialogResult.OK;
            }
        };
        storeButton.Enabled = false;
        AcceptButton = storeButton;
        CancelButton = cancel;
        Controls.AddRange([heading, prompt, nameInput, storeButton, cancel]);
    }

    public static string? Ask(
        IWin32Window owner,
        float dpiScale,
        AssistantWindowBounds? targetBounds = null)
    {
        using StoredSessionNameDialog dialog = new(dpiScale, targetBounds);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.nameInput.Text.Trim()
            : null;
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        SetForegroundWindow(Handle);
        Activate();
        nameInput.Focus();
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
