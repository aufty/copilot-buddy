using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal sealed class MessageBubble : Form
{
    private readonly BubbleOptions options;
    private readonly string fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf");
    private readonly PrivateFontCollection fonts = new();
    private bool fontRegistered;
    private string? message;
    private MessageBubbleStyle style;
    private float scale;
    private Font? messageFont;
    private int padding;
    private int pointerHeight;
    private int pointerX;
    private Rectangle textBounds;
    private static readonly TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
        TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    public MessageBubble(BubbleOptions options)
    {
        this.options = options;
        fonts.AddFontFile(fontPath);
        fontRegistered = AddFontResourceEx(fontPath, 0x10, IntPtr.Zero) > 0;
        if (!fontRegistered)
        {
            throw new InvalidOperationException("Unable to load the bundled VT323 font.");
        }
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Magenta;
        TransparencyKey = BackColor;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool IsInteractive { get; set; }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000080;
            return parameters;
        }
    }

    public void Present(
        Form owner,
        string text,
        Point anchor,
        Rectangle area,
        float dpiScale,
        MessageBubbleStyle nextStyle = MessageBubbleStyle.Default)
    {
        bool ambient = nextStyle == MessageBubbleStyle.Ambient;
        int maximumWidth = Math.Max(1, Math.Min(area.Width,
            (int)(options.MaximumWidth * dpiScale * (ambient ? 0.78 : 1))));
        int maximumHeight = Math.Max(1, Math.Min(area.Height,
            (int)(options.MaximumHeight * dpiScale * (ambient ? 0.7 : 1))));
        if (message != text || style != nextStyle || scale != dpiScale ||
            Width > maximumWidth || Height > maximumHeight)
        {
            message = text;
            style = nextStyle;
            scale = dpiScale;
            messageFont?.Dispose();
            messageFont = new Font(fonts.Families[0], (ambient ? 17 : 20) * scale,
                FontStyle.Regular, GraphicsUnit.Pixel);
            double desiredPadding = ambient ? 9 : options.Padding;
            padding = Math.Min((int)(desiredPadding * scale),
                Math.Max(0, (Math.Min(maximumWidth, maximumHeight) - 1) / 2));
            pointerHeight = Math.Min((int)((ambient ? 7 : 10) * scale),
                Math.Max(0, maximumHeight - padding * 2 - 1));
            Size measured = TextRenderer.MeasureText(text, messageFont,
                new Size(Math.Max(1, maximumWidth - padding * 2), int.MaxValue), TextFlags);
            ClientSize = new Size(Math.Clamp(measured.Width + padding * 2,
                    Math.Min(maximumWidth, (int)((ambient ? 100 : options.MinimumWidth) * scale)), maximumWidth),
                Math.Min(maximumHeight, measured.Height + padding * 2 + pointerHeight));
            textBounds = new Rectangle(padding, padding, Math.Max(1, Width - padding * 2),
                Math.Max(1, Height - padding * 2 - pointerHeight));
            AccessibleName = text;
            Invalidate();
        }
        Location = new Point(Math.Clamp(anchor.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(anchor.Y - Height - (int)(2 * scale), area.Top, Math.Max(area.Top, area.Bottom - Height)));
        int nextPointer = Math.Clamp(anchor.X - Left, Math.Min(Width / 2, (int)(16 * scale)), Math.Max(Width / 2, Width - (int)(16 * scale)));
        if (pointerX != nextPointer)
        {
            pointerX = nextPointer;
            Invalidate();
        }
        if (!Visible)
        {
            Show(owner);
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.None;
        float bodyHeight = Height - pointerHeight - 1;
        float radius = Math.Max(1, Math.Min(6 * scale, Math.Min(Width, bodyHeight) / 2 - 1));
        RectangleF body = new(1, 1, Math.Max(1, Width - 3), Math.Max(1, bodyHeight - 2));
        float diameter = radius * 2;
        float pointerLeft = pointerX - 8 * scale;
        float pointerRight = pointerX + 8 * scale;
        using GraphicsPath outline = new();
        outline.StartFigure();
        outline.AddArc(body.Left, body.Top, diameter, diameter, 180, 90);
        outline.AddLine(body.Left + radius, body.Top, body.Right - radius, body.Top);
        outline.AddArc(body.Right - diameter, body.Top, diameter, diameter, 270, 90);
        outline.AddLine(body.Right, body.Top + radius, body.Right, body.Bottom - radius);
        outline.AddArc(body.Right - diameter, body.Bottom - diameter, diameter, diameter, 0, 90);
        outline.AddLine(body.Right - radius, body.Bottom, pointerRight, body.Bottom);
        outline.AddLine(pointerRight, body.Bottom, pointerX, Height - 1);
        outline.AddLine(pointerX, Height - 1, pointerLeft, body.Bottom);
        outline.AddLine(pointerLeft, body.Bottom, body.Left + radius, body.Bottom);
        outline.AddArc(body.Left, body.Bottom - diameter, diameter, diameter, 90, 90);
        outline.AddLine(body.Left, body.Bottom - radius, body.Left, body.Top + radius);
        outline.CloseFigure();
        bool ambient = style == MessageBubbleStyle.Ambient;
        using SolidBrush background = new(ambient
            ? Color.FromArgb(74, 73, 80)
            : Color.FromArgb(255, 253, 248));
        using Pen border = new(ambient
            ? Color.FromArgb(42, 41, 47)
            : Color.FromArgb(42, 40, 37), Math.Max(1, 2 * scale));
        args.Graphics.FillPath(background, outline);
        args.Graphics.DrawPath(border, outline);
        TextRenderer.DrawText(args.Graphics, message, messageFont, textBounds,
            ambient ? Color.FromArgb(235, 232, 226) : Color.FromArgb(31, 29, 26), TextFlags);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021)
        {
            message.Result = IsInteractive ? 1 : 3;
            return;
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            messageFont?.Dispose();
            fonts.Dispose();
            if (fontRegistered)
            {
                RemoveFontResourceEx(fontPath, 0x10, IntPtr.Zero);
                fontRegistered = false;
            }
        }
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string fileName, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveFontResourceEx(string fileName, uint flags, IntPtr reserved);
}

internal enum MessageBubbleStyle
{
    Default,
    Ambient
}