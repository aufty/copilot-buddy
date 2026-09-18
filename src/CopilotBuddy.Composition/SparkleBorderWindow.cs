using System.Drawing.Drawing2D;

namespace CopilotBuddy.Composition;

internal sealed class SparkleBorderWindow : Form
{
    private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer closeTimer = new() { Interval = 850 };
    private int frame;

    public SparkleBorderWindow(Rectangle targetBounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Magenta;
        TransparencyKey = BackColor;
        TopMost = true;
        Bounds = Rectangle.Inflate(targetBounds, 8, 8);
        animationTimer.Tick += (_, _) =>
        {
            frame++;
            Invalidate();
        };
        closeTimer.Tick += (_, _) => Close();
        Shown += (_, _) =>
        {
            animationTimer.Start();
            closeTimer.Start();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x080000A0;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.None;
        Rectangle border = new(5, 5, ClientSize.Width - 11, ClientSize.Height - 11);
        Color[] colors =
        [
            Color.FromArgb(255, 221, 104),
            Color.FromArgb(247, 255, 255),
            Color.FromArgb(91, 216, 211)
        ];
        using Pen outline = new(colors[frame % colors.Length], 3);
        outline.DashStyle = DashStyle.Dot;
        outline.DashOffset = -frame * 2;
        args.Graphics.DrawRectangle(outline, border);

        int perimeter = Math.Max(1, 2 * (border.Width + border.Height));
        for (int index = 0; index < 20; index++)
        {
            int distance = (index * perimeter / 20 + frame * 13) % perimeter;
            Point point = PointOnPerimeter(border, distance);
            int radius = index % 3 == frame % 3 ? 4 : 2;
            using Pen sparkle = new(colors[(index + frame) % colors.Length], 2);
            args.Graphics.DrawLine(sparkle, point.X - radius, point.Y, point.X + radius, point.Y);
            args.Graphics.DrawLine(sparkle, point.X, point.Y - radius, point.X, point.Y + radius);
        }
    }

    private static Point PointOnPerimeter(Rectangle rectangle, int distance)
    {
        if (distance < rectangle.Width)
        {
            return new(rectangle.Left + distance, rectangle.Top);
        }
        distance -= rectangle.Width;
        if (distance < rectangle.Height)
        {
            return new(rectangle.Right, rectangle.Top + distance);
        }
        distance -= rectangle.Height;
        if (distance < rectangle.Width)
        {
            return new(rectangle.Right - distance, rectangle.Bottom);
        }
        return new(rectangle.Left, rectangle.Bottom - (distance - rectangle.Width));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animationTimer.Dispose();
            closeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
