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
    private Rectangle gatherBounds;
    private Rectangle storeBounds;
    private Rectangle ballBounds;
    private Rectangle foodBounds;
    private Rectangle waterBounds;
    private Rectangle chairBounds;
    private Rectangle settingsBounds;
    private bool summonHovered;
    private bool inquireHovered;
    private bool handoffHovered;
    private bool gatherHovered;
    private bool storeHovered;
    private bool settingsHovered;
    private SupplyKind? supplyHovered;
    private readonly HashSet<SupplyKind> deployedSupplies = [];
    private readonly HashSet<SupplyKind> pendingRecalls = [];
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
        ClientSize = new Size(560, 454);
        AccessibleName = "Copilot Buddy special skills";
        dismissTimer.Tick += (_, _) => DismissOnOutsideClick();
    }

    public event EventHandler? SummonRequested;
    public event EventHandler? InquireRequested;
    public event EventHandler? HandoffRequested;
    public event EventHandler? GatherRequested;
    public event EventHandler? StoreRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<SupplyKindEventArgs>? SupplyRequested;

    public void SetSupplyState(SupplyKind kind, bool deployed, bool recallPending = false)
    {
        if (deployed) deployedSupplies.Add(kind);
        else deployedSupplies.Remove(kind);
        if (recallPending) pendingRecalls.Add(kind);
        else pendingRecalls.Remove(kind);
        Invalidate();
    }

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
        ClientSize = new Size((int)Math.Round(560 * scale), (int)Math.Round(454 * scale));
        Location = location;
        summonBounds = new Rectangle((int)(12 * scale), (int)(61 * scale),
            (int)(336 * scale), (int)(74 * scale));
        inquireBounds = new Rectangle((int)(12 * scale), (int)(137 * scale),
            (int)(336 * scale), (int)(74 * scale));
        handoffBounds = new Rectangle((int)(12 * scale), (int)(213 * scale),
            (int)(336 * scale), (int)(74 * scale));
        gatherBounds = new Rectangle((int)(12 * scale), (int)(289 * scale),
            (int)(336 * scale), (int)(74 * scale));
        storeBounds = new Rectangle((int)(12 * scale), (int)(365 * scale),
            (int)(336 * scale), (int)(74 * scale));
        ballBounds = new Rectangle((int)(370 * scale), (int)(61 * scale), (int)(178 * scale), (int)(74 * scale));
        foodBounds = new Rectangle((int)(370 * scale), (int)(137 * scale), (int)(178 * scale), (int)(74 * scale));
        waterBounds = new Rectangle((int)(370 * scale), (int)(213 * scale), (int)(178 * scale), (int)(74 * scale));
        chairBounds = new Rectangle((int)(370 * scale), (int)(289 * scale), (int)(178 * scale), (int)(74 * scale));
        settingsBounds = new Rectangle((int)(510 * scale), (int)(12 * scale),
            (int)(38 * scale), (int)(38 * scale));
        waitForMouseRelease = true;
        previousButtons = MouseButtons.None;
        summonHovered = false;
        inquireHovered = false;
        handoffHovered = false;
        gatherHovered = false;
        storeHovered = false;
        settingsHovered = false;
        supplyHovered = null;
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
            (int)(348 * scale), (int)(53 * scale));
        Rectangle supplyHeadingBounds = new((int)(370 * scale), (int)(12 * scale),
            (int)(132 * scale), (int)(38 * scale));
        TextRenderer.DrawText(args.Graphics, "SUPPLIES", headingFont, supplyHeadingBounds,
            Color.FromArgb(255, 203, 77), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        args.Graphics.DrawLine(divider, (int)(370 * scale), (int)(53 * scale),
            (int)(548 * scale), (int)(53 * scale));
        args.Graphics.DrawLine(divider, (int)(359 * scale), (int)(12 * scale),
            (int)(359 * scale), ClientSize.Height - (int)(13 * scale));

        DrawItem(args.Graphics, summonBounds, summonHovered, "Summon", summonShortcut, scale);
        DrawItem(args.Graphics, inquireBounds, inquireHovered, "Inquire", "Alt+Shift+G", scale);
        DrawItem(args.Graphics, handoffBounds, handoffHovered, "Handoff", "Alt+Shift+H", scale);
        DrawItem(args.Graphics, gatherBounds, gatherHovered, "Gather", "Alt+Shift+T", scale);
        DrawItem(args.Graphics, storeBounds, storeHovered, "Store", "Alt+Shift+S", scale);
        DrawSupply(args.Graphics, ballBounds, supplyHovered == SupplyKind.Ball, SupplyKind.Ball, "BALL", scale);
        DrawSupply(args.Graphics, foodBounds, supplyHovered == SupplyKind.Food, SupplyKind.Food, "FOOD", scale);
        DrawSupply(args.Graphics, waterBounds, supplyHovered == SupplyKind.Water, SupplyKind.Water, "WATER", scale);
        DrawSupply(args.Graphics, chairBounds, supplyHovered == SupplyKind.Chair, SupplyKind.Chair, "CHAIR", scale);
        DrawSettings(args.Graphics, scale);
    }

    private void DrawSettings(Graphics graphics, float scale)
    {
        using SolidBrush background = new(settingsHovered ? Color.FromArgb(63, 83, 96) : Color.FromArgb(42, 39, 51));
        using Pen edge = new(settingsHovered ? Color.FromArgb(255, 203, 77) : Color.FromArgb(75, 71, 88),
            Math.Max(1, 2 * scale));
        graphics.FillRectangle(background, settingsBounds);
        graphics.DrawRectangle(edge, settingsBounds.X, settingsBounds.Y,
            settingsBounds.Width - 1, settingsBounds.Height - 1);

        int unit = Math.Max(2, (int)Math.Round(3 * scale));
        int centerX = settingsBounds.Left + settingsBounds.Width / 2;
        int centerY = settingsBounds.Top + settingsBounds.Height / 2;
        using SolidBrush gear = new(Color.FromArgb(247, 244, 231));
        graphics.FillRectangle(gear, centerX - 3 * unit, centerY - 2 * unit, 6 * unit, 4 * unit);
        graphics.FillRectangle(gear, centerX - 2 * unit, centerY - 3 * unit, 4 * unit, 6 * unit);
        graphics.FillRectangle(background, centerX - unit, centerY - unit, 2 * unit, 2 * unit);
    }

    private void DrawSupply(Graphics graphics, Rectangle bounds, bool hovered, SupplyKind kind, string label, float scale)
    {
        using SolidBrush background = new(hovered ? Color.FromArgb(63, 83, 96) : Color.FromArgb(42, 39, 51));
        using Pen edge = new(hovered ? Color.FromArgb(255, 203, 77) : Color.FromArgb(75, 71, 88),
            Math.Max(1, 2 * scale));
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        DrawSupplyIcon(graphics, new Point(bounds.X + (int)(28 * scale), bounds.Y + bounds.Height / 2),
            kind, deployedSupplies.Contains(kind), scale);
        Rectangle labelBounds = new(bounds.X + (int)(58 * scale), bounds.Y, bounds.Width - (int)(66 * scale), bounds.Height);
        string displayedLabel = pendingRecalls.Contains(kind) ? $"{label}..." : label;
        TextRenderer.DrawText(graphics, displayedLabel, itemFont, labelBounds, Color.FromArgb(247, 244, 231),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static void DrawSupplyIcon(
        Graphics graphics,
        Point center,
        SupplyKind kind,
        bool silhouette,
        float scale)
    {
        int size = Math.Max(12, (int)(24 * scale));
        Rectangle icon = new(center.X - size / 2, center.Y - size / 2, size, size);
        Color color = kind switch
        {
            SupplyKind.Ball => Color.FromArgb(172, 90, 215),
            SupplyKind.Food => Color.FromArgb(244, 192, 68),
            SupplyKind.Water => Color.FromArgb(63, 165, 224),
            SupplyKind.Chair => Color.FromArgb(181, 132, 78),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Color outlineColor = silhouette ? Color.FromArgb(128, color) : Color.FromArgb(31, 29, 38);
        using SolidBrush brush = new(silhouette ? Color.Transparent : color);
        using Pen outline = new(outlineColor, Math.Max(1, 2 * scale));
        if (kind == SupplyKind.Water)
        {
            Point[] drop =
            [
                new(center.X, icon.Top),
                new(icon.Right, icon.Bottom - size / 3),
                new(center.X, icon.Bottom),
                new(icon.Left, icon.Bottom - size / 3)
            ];
            if (!silhouette) graphics.FillPolygon(brush, drop);
            graphics.DrawPolygon(outline, drop);
        }
        else if (kind == SupplyKind.Chair)
        {
            Rectangle back = new(icon.Left + size / 6, icon.Top, size * 2 / 3, size * 2 / 3);
            Rectangle seat = new(icon.Left, icon.Top + size / 2, size, size / 3);
            if (!silhouette)
            {
                graphics.FillRectangle(brush, back);
                graphics.FillRectangle(brush, seat);
            }
            graphics.DrawRectangle(outline, back);
            graphics.DrawRectangle(outline, seat);
        }
        else
        {
            if (!silhouette) graphics.FillEllipse(brush, icon);
            graphics.DrawEllipse(outline, icon);
        }
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
        bool gather = gatherBounds.Contains(args.Location);
        bool store = storeBounds.Contains(args.Location);
        bool settings = settingsBounds.Contains(args.Location);
        SupplyKind? supply = ballBounds.Contains(args.Location) ? SupplyKind.Ball
            : foodBounds.Contains(args.Location) ? SupplyKind.Food
            : waterBounds.Contains(args.Location) ? SupplyKind.Water
            : chairBounds.Contains(args.Location) ? SupplyKind.Chair
            : null;
        if (summonHovered != summon || inquireHovered != inquire ||
            handoffHovered != hovered || gatherHovered != gather || storeHovered != store ||
            settingsHovered != settings || supplyHovered != supply)
        {
            summonHovered = summon;
            inquireHovered = inquire;
            handoffHovered = hovered;
            gatherHovered = gather;
            storeHovered = store;
            settingsHovered = settings;
            supplyHovered = supply;
            Cursor = summon || inquire || hovered || gather || store || settings || supply is not null
                ? Cursors.Hand
                : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        if (summonHovered || inquireHovered || handoffHovered || gatherHovered || storeHovered ||
            settingsHovered || supplyHovered is not null)
        {
            summonHovered = false;
            inquireHovered = false;
            handoffHovered = false;
            gatherHovered = false;
            storeHovered = false;
            settingsHovered = false;
            supplyHovered = null;
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
        else if (args.Button == MouseButtons.Left && gatherBounds.Contains(args.Location))
        {
            Hide();
            GatherRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Button == MouseButtons.Left && storeBounds.Contains(args.Location))
        {
            Hide();
            StoreRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Button == MouseButtons.Left && settingsBounds.Contains(args.Location))
        {
            Hide();
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Button == MouseButtons.Left)
        {
            SupplyKind? supply = ballBounds.Contains(args.Location) ? SupplyKind.Ball
                : foodBounds.Contains(args.Location) ? SupplyKind.Food
                : waterBounds.Contains(args.Location) ? SupplyKind.Water
                : chairBounds.Contains(args.Location) ? SupplyKind.Chair
                : null;
            if (supply is not null)
            {
                Hide();
                SupplyRequested?.Invoke(this, new SupplyKindEventArgs(supply.Value));
            }
        }
    }

    internal sealed class SupplyKindEventArgs(SupplyKind kind) : EventArgs
    {
        public SupplyKind Kind { get; } = kind;
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
