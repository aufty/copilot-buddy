using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using TaskbarBuddy.Core;

namespace TaskbarBuddy.Composition;

internal sealed class MessageBubble : Form
{
    private readonly BubbleOptions options;
    private readonly string fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "VT323-Regular.ttf");
    private readonly PrivateFontCollection fonts = new();
    private bool fontRegistered;
    private string? message;
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

    public void Present(Form owner, string text, Point anchor, Rectangle area, float dpiScale)
    {
        int maximumWidth = Math.Max(1, Math.Min(area.Width, (int)(options.MaximumWidth * dpiScale)));
        int maximumHeight = Math.Max(1, Math.Min(area.Height, (int)(options.MaximumHeight * dpiScale)));
        if (message != text || scale != dpiScale || Width > maximumWidth || Height > maximumHeight)
        {
            message = text;
            scale = dpiScale;
            messageFont?.Dispose();
            messageFont = new Font(fonts.Families[0], 20 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            padding = Math.Min((int)(options.Padding * scale), Math.Max(0, (Math.Min(maximumWidth, maximumHeight) - 1) / 2));
            pointerHeight = Math.Min((int)(10 * scale), Math.Max(0, maximumHeight - padding * 2 - 1));
            Size measured = TextRenderer.MeasureText(text, messageFont,
                new Size(Math.Max(1, maximumWidth - padding * 2), int.MaxValue), TextFlags);
            ClientSize = new Size(Math.Clamp(measured.Width + padding * 2,
                    Math.Min(maximumWidth, (int)(options.MinimumWidth * scale)), maximumWidth),
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
        using GraphicsPath outline = new();
        outline.AddRoundedRectangle(new RectangleF(1, 1, Math.Max(1, Width - 3), Math.Max(1, bodyHeight - 2)), new SizeF(radius, radius));
        using SolidBrush background = new(Color.FromArgb(255, 253, 248));
        using Pen border = new(Color.FromArgb(42, 40, 37), Math.Max(1, 2 * scale));
        args.Graphics.FillPath(background, outline);
        args.Graphics.DrawPath(border, outline);
        PointF[] pointer = [new(pointerX - 8 * scale, bodyHeight - scale), new(pointerX, Height - 1), new(pointerX + 8 * scale, bodyHeight - scale)];
        args.Graphics.FillPolygon(background, pointer);
        args.Graphics.DrawLines(border, pointer);
        TextRenderer.DrawText(args.Graphics, message, messageFont, textBounds, Color.FromArgb(31, 29, 26), TextFlags);
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