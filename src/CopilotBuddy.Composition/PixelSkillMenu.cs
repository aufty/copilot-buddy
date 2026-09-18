using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CopilotBuddy.Composition;

internal sealed class PixelSkillMenu : Form
{
    private readonly PrivateFontCollection fonts = new();
    private readonly System.Windows.Forms.Timer dismissTimer = new() { Interval = 40 };
    private Font? headingFont;
    private Font? itemFont;
    private Font? shortcutFont;
    private Rectangle summonBounds;
    private Rectangle inquireBounds;
    private Rectangle handoffBounds;
    private Rectangle storeBounds;
    private bool summonHovered;
    private bool inquireHovered;
    private bool handoffHovered;
    private bool storeHovered;
    private string summonShortcut = "Alt+Enter";
    private bool waitForMouseRelease;
    private MouseButtons previousButtons;

    public PixelSkillMenu()
    {
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf"));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(31, 29, 38);
        DoubleBuffered = true;
        ClientSize = new Size(360, 378);
        AccessibleName = "Copilot Buddy special skills";
        dismissTimer.Tick += (_, _) => DismissOnOutsideClick();
    }

    public event EventHandler? SummonRequested;
    public event EventHandler? InquireRequested;
    public event EventHandler? HandoffRequested;
    public event EventHandler? StoreRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080;
            return parameters;
        }
    }

    public void Present(Form owner, Point location, float dpiScale, string shortcut)
    {
        float scale = Math.Max(1, dpiScale);
        summonShortcut = shortcut;
        headingFont?.Dispose();
        itemFont?.Dispose();
        shortcutFont?.Dispose();
        headingFont = new Font(fonts.Families[0], 22 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        itemFont = new Font(fonts.Families[0], 30 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        shortcutFont = new Font(fonts.Families[0], 21 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        ClientSize = new Size((int)Math.Round(360 * scale), (int)Math.Round(378 * scale));
        Location = location;
        summonBounds = new Rectangle((int)(12 * scale), (int)(61 * scale),
            ClientSize.Width - (int)(24 * scale), (int)(74 * scale));
        inquireBounds = new Rectangle((int)(12 * scale), (int)(137 * scale),
            ClientSize.Width - (int)(24 * scale), (int)(74 * scale));
        handoffBounds = new Rectangle((int)(12 * scale), (int)(213 * scale),
            ClientSize.Width - (int)(24 * scale), (int)(74 * scale));
        storeBounds = new Rectangle((int)(12 * scale), (int)(289 * scale),
            ClientSize.Width - (int)(24 * scale), (int)(74 * scale));
        waitForMouseRelease = true;
        previousButtons = MouseButtons.None;
        summonHovered = false;
        inquireHovered = false;
        handoffHovered = false;
        storeHovered = false;
        Invalidate();
        if (!Visible)
        {
            Show(owner);
        }
        dismissTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.None;
        args.Graphics.Clear(BackColor);
        float scale = DeviceDpi / 96f;
        using Pen shadow = new(Color.FromArgb(17, 16, 22), Math.Max(2, 4 * scale));
        using Pen edge = new(Color.FromArgb(91, 216, 211), Math.Max(1, 3 * scale));
        args.Graphics.DrawRectangle(shadow, (int)(4 * scale), (int)(4 * scale),
            ClientSize.Width - (int)(8 * scale), ClientSize.Height - (int)(8 * scale));
        args.Graphics.DrawRectangle(edge, 1, 1, ClientSize.Width - 4, ClientSize.Height - 4);

        Rectangle headingBounds = new((int)(14 * scale), (int)(12 * scale),
            ClientSize.Width - (int)(28 * scale), (int)(38 * scale));
        TextRenderer.DrawText(args.Graphics, "SPECIAL SKILLS", headingFont, headingBounds,
            Color.FromArgb(91, 216, 211), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        using Pen divider = new(Color.FromArgb(63, 83, 96), Math.Max(1, 2 * scale));
        args.Graphics.DrawLine(divider, (int)(12 * scale), (int)(53 * scale),
            ClientSize.Width - (int)(13 * scale), (int)(53 * scale));

        DrawItem(args.Graphics, summonBounds, summonHovered, "Summon", summonShortcut, scale);
        DrawItem(args.Graphics, inquireBounds, inquireHovered, "Inquire", "Alt+Shift+G", scale);
        DrawItem(args.Graphics, handoffBounds, handoffHovered, "Handoff", "Alt+Shift+H", scale);
        DrawItem(args.Graphics, storeBounds, storeHovered, "Store", "Alt+Shift+S", scale);
    }

    private void DrawItem(
        Graphics graphics,
        Rectangle bounds,
        bool hovered,
        string label,
        string shortcut,
        float scale)
    {
        using SolidBrush background = new(hovered
            ? Color.FromArgb(63, 83, 96)
            : Color.FromArgb(42, 39, 51));
        graphics.FillRectangle(background, bounds);
        using Pen edge = new(hovered
            ? Color.FromArgb(255, 203, 77)
            : Color.FromArgb(75, 71, 88), Math.Max(1, 2 * scale));
        graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        Rectangle labelBounds = new(bounds.X + (int)(16 * scale), bounds.Y,
            bounds.Width / 2, bounds.Height);
        TextRenderer.DrawText(graphics, label, itemFont, labelBounds,
            Color.FromArgb(247, 244, 231),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        Rectangle shortcutBounds = new(bounds.X + bounds.Width / 2, bounds.Y,
            bounds.Width / 2 - (int)(16 * scale), bounds.Height);
        TextRenderer.DrawText(graphics, shortcut, shortcutFont, shortcutBounds,
            Color.FromArgb(255, 203, 77),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        bool summon = summonBounds.Contains(args.Location);
        bool inquire = inquireBounds.Contains(args.Location);
        bool hovered = handoffBounds.Contains(args.Location);
        bool store = storeBounds.Contains(args.Location);
        if (summonHovered != summon || inquireHovered != inquire ||
            handoffHovered != hovered || storeHovered != store)
        {
            summonHovered = summon;
            inquireHovered = inquire;
            handoffHovered = hovered;
            storeHovered = store;
            Cursor = summon || inquire || hovered || store ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        if (summonHovered || inquireHovered || handoffHovered || storeHovered)
        {
            summonHovered = false;
            inquireHovered = false;
            handoffHovered = false;
            storeHovered = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button == MouseButtons.Left && summonBounds.Contains(args.Location))
        {
            Hide();
            SummonRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Button == MouseButtons.Left && inquireBounds.Contains(args.Location))
        {
            Hide();
            InquireRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Button == MouseButtons.Left && handoffBounds.Contains(args.Location))
        {
            Hide();
            HandoffRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Button == MouseButtons.Left && storeBounds.Contains(args.Location))
        {
            Hide();
            StoreRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021)
        {
            message.Result = 3;
            return;
        }
        base.WndProc(ref message);
    }

    private void DismissOnOutsideClick()
    {
        MouseButtons buttons = Control.MouseButtons;
        if (waitForMouseRelease)
        {
            if (buttons == MouseButtons.None)
            {
                waitForMouseRelease = false;
            }
            previousButtons = buttons;
            return;
        }
        bool pressed = buttons != MouseButtons.None && previousButtons == MouseButtons.None;
        previousButtons = buttons;
        if (pressed && !Bounds.Contains(Cursor.Position))
        {
            Hide();
        }
    }

    protected override void OnVisibleChanged(EventArgs args)
    {
        base.OnVisibleChanged(args);
        if (!Visible)
        {
            dismissTimer.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dismissTimer.Dispose();
            headingFont?.Dispose();
            itemFont?.Dispose();
            shortcutFont?.Dispose();
            fonts.Dispose();
        }
        base.Dispose(disposing);
    }
}
