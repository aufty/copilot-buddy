using System.Numerics;
using TaskbarBuddy.Core;
using Windows.UI.Composition;

namespace TaskbarBuddy.Composition;

internal sealed class ArtifactPresent : IDisposable
{
    private const int FrameWidth = 12;
    private const int FrameHeight = 14;
    private const int SpriteScale = 2;
    private static readonly Windows.UI.Color[] PresetColors =
    [
        Windows.UI.Color.FromArgb(255, 76, 201, 240),
        Windows.UI.Color.FromArgb(255, 255, 105, 120),
        Windows.UI.Color.FromArgb(255, 174, 108, 255),
        Windows.UI.Color.FromArgb(255, 64, 214, 138),
        Windows.UI.Color.FromArgb(255, 255, 132, 214),
        Windows.UI.Color.FromArgb(255, 64, 132, 255),
        Windows.UI.Color.FromArgb(255, 255, 151, 62)
    ];

    private readonly Compositor compositor;
    private readonly ContainerVisual parent;
    private readonly string spritePath;
    private readonly bool rainbow;
    private readonly Windows.UI.Color color = PresetColors[Random.Shared.Next(PresetColors.Length)];
    private readonly List<CompositionObject> resources = [];
    private ContainerVisual root;
    private ContainerVisual closedFrame;
    private ContainerVisual openFrame;
    private CompositionScopedBatch? dropBatch;
    private CompositionScopedBatch? openingBatch;
    private NativeDragMotion? dragMotion;
    private float dpiScale;
    private Size clientSize;
    private Vector3 position;
    private Vector3 grabOffset;
    private bool releasingDrag;
    private bool opened;
    private bool producerCompleted;

    public ArtifactPresent(
        Compositor compositor,
        ContainerVisual parent,
        string spritePath,
        string artifactPath,
        string label,
        float dpiScale,
        Size clientSize,
        double buddyXDip,
        double buddyWidthDip,
        bool reducedMotion,
        bool? forceRainbow = null,
        bool readyInitially = false)
    {
        this.compositor = compositor;
        this.parent = parent;
        this.spritePath = spritePath;
        rainbow = forceRainbow ?? Random.Shared.Next(9) == 0;
        ArtifactPath = artifactPath;
        Label = label;
        IsReady = readyInitially;
        producerCompleted = readyInitially;
        root = compositor.CreateContainerVisual();
        closedFrame = compositor.CreateContainerVisual();
        openFrame = compositor.CreateContainerVisual();
        parent.Children.InsertAtTop(root);
        Rebuild(dpiScale, clientSize);
        TossNear(buddyXDip, buddyWidthDip, reducedMotion);
    }

    public event EventHandler? Opened;
    public event EventHandler? MotionCompleted;
    public string ArtifactPath { get; }
    public string Label { get; }
    public bool IsReady { get; private set; }
    public bool IsOpened => opened;
    public bool IsMoving => dragMotion is not null;

    public bool CheckReady()
    {
        if (IsReady || !producerCompleted || !File.Exists(ArtifactPath))
        {
            return IsReady;
        }
        try
        {
            using FileStream stream = new(ArtifactPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            IsReady = stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        return IsReady;
    }

    public void MarkProducerCompleted() => producerCompleted = true;

    public bool HitTest(Point point)
    {
        if (opened)
        {
            return false;
        }
        RectangleF bounds = new(position.X, position.Y, root.Size.X, root.Size.Y);
        return bounds.Contains(point);
    }

    public Point BubbleAnchor => new(
        (int)Math.Round(position.X + root.Size.X / 2),
        (int)Math.Round(position.Y));

    public void BeginDrag(Point point, PhysicsOptions physics, bool direct, Action<Action> dispatch)
    {
        if (opened)
        {
            return;
        }
        FinishDrop();
        StopDragMotion();
        grabOffset = position - new Vector3(point.X, point.Y, 0);
        releasingDrag = false;
        dragMotion = new NativeDragMotion(
            compositor,
            root,
            position,
            new Vector3(Math.Max(0, clientSize.Width - root.Size.X), Math.Max(0, clientSize.Height - root.Size.Y), 0),
            physics.DragResponseSeconds,
            (float)physics.MaximumReleaseSpeed * dpiScale,
            (float)physics.Gravity * dpiScale,
            (float)physics.WallRestitution,
            direct,
            dispatch,
            OnDragPose);
    }

    public void DragTo(Point point)
    {
        if (dragMotion is null || releasingDrag)
        {
            return;
        }
        Vector3 target = Vector3.Clamp(
            new Vector3(point.X, point.Y, 0) + grabOffset,
            Vector3.Zero,
            new Vector3(Math.Max(0, clientSize.Width - root.Size.X), Math.Max(0, clientSize.Height - root.Size.Y), 0));
        dragMotion.SetTarget(target);
    }

    public void ReleaseDrag()
    {
        if (dragMotion is null || releasingDrag)
        {
            return;
        }
        releasingDrag = true;
        dragMotion.Release();
    }

    public void PopOpen(Point click)
    {
        if (!IsReady || opened)
        {
            return;
        }
        PopOpenCore(click);
    }

    public void PopOpenUnavailable(Point click)
    {
        if (opened)
        {
            return;
        }
        PopOpenCore(click);
    }

    private void PopOpenCore(Point click)
    {
        FinishDrop();
        StopDragMotion();
        opened = true;
        closedFrame.Opacity = 0;
        openFrame.Opacity = 1;

        float floor = clientSize.Height;
        float width = root.Size.X;
        float height = root.Size.Y;
        float direction = click.X <= position.X + width / 2 ? 1 : -1;
        float distance = Random.Shared.Next(9, 18) * dpiScale;
        float landingX = Math.Clamp(position.X + direction * distance, 0, Math.Max(0, clientSize.Width - width));
        float lift = Random.Shared.Next(13, 23) * dpiScale;
        Vector3 landing = new(landingX, floor - height, 0);

        using Vector3KeyFrameAnimation bounce = compositor.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction launch = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.18f, 0.85f), new Vector2(0.28f, 1));
        using CubicBezierEasingFunction fall = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.58f, 0), new Vector2(0.82f, 0.25f));
        bounce.Duration = TimeSpan.FromMilliseconds(620);
        bounce.InsertKeyFrame(0, position);
        bounce.InsertKeyFrame(0.38f, new Vector3(
            position.X + direction * distance * 0.65f,
            Math.Max(0, position.Y - lift),
            0), launch);
        bounce.InsertKeyFrame(0.72f, landing, fall);
        bounce.InsertKeyFrame(0.84f, landing - new Vector3(0, 5 * dpiScale, 0), launch);
        bounce.InsertKeyFrame(1, landing, fall);

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        openingBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(openingBatch, batch))
            {
                return;
            }
            openingBatch = null;
            position = landing;
            root.Offset = landing;
            batch.Dispose();
            Opened?.Invoke(this, EventArgs.Empty);
        };
        root.StartAnimation(nameof(root.Offset), bounce);
        batch.End();
    }

    public void ResetAfterFailedOpen()
    {
        if (!opened)
        {
            return;
        }
        root.StopAnimation(nameof(root.Offset));
        opened = false;
        closedFrame.Opacity = 1;
        openFrame.Opacity = 0;
        position = new Vector3(position.X, clientSize.Height - root.Size.Y, 0);
        root.Offset = position;
    }

    public void Relayout(float nextDpiScale, Size nextClientSize)
    {
        FinishDrop();
        float horizontalRatio = clientSize.Width <= root.Size.X
            ? 0
            : position.X / (clientSize.Width - root.Size.X);
        Rebuild(nextDpiScale, nextClientSize);
        position = new Vector3(
            Math.Clamp(horizontalRatio * Math.Max(0, clientSize.Width - root.Size.X), 0, Math.Max(0, clientSize.Width - root.Size.X)),
            clientSize.Height - root.Size.Y,
            0);
        root.Offset = position;
    }

    private void TossNear(double buddyXDip, double buddyWidthDip, bool reducedMotion)
    {
        float gap = 5 * dpiScale;
        float buddyLeft = (float)buddyXDip * dpiScale;
        float buddyRight = (float)(buddyXDip + buddyWidthDip) * dpiScale;
        float x = buddyRight + gap;
        float direction = 1;
        if (x + root.Size.X > clientSize.Width)
        {
            x = buddyLeft - gap - root.Size.X;
            direction = -1;
        }
        x += Random.Shared.Next(-3, 4) * dpiScale;
        position = new Vector3(
            Math.Clamp(x, 0, Math.Max(0, clientSize.Width - root.Size.X)),
            clientSize.Height - root.Size.Y,
            0);
        Vector3 start = new(
            Math.Clamp((buddyLeft + buddyRight - root.Size.X) / 2, 0, Math.Max(0, clientSize.Width - root.Size.X)),
            clientSize.Height - root.Size.Y - (reducedMotion ? 0 : 6 * dpiScale),
            0);
        root.Offset = start;
        if (reducedMotion)
        {
            root.Offset = position;
            return;
        }

        float firstLandingX = start.X + (position.X - start.X) * 0.72f;
        using Vector3KeyFrameAnimation toss = compositor.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction rise = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 0.8f), new Vector2(0.28f, 1));
        using CubicBezierEasingFunction fall = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.58f, 0), new Vector2(0.82f, 0.24f));
        toss.Duration = TimeSpan.FromMilliseconds(880);
        toss.InsertKeyFrame(0, start);
        toss.InsertKeyFrame(0.34f, new Vector3(
            start.X + (position.X - start.X) * 0.46f,
            position.Y - 15 * dpiScale,
            0), rise);
        toss.InsertKeyFrame(0.61f, new Vector3(firstLandingX, position.Y, 0), fall);
        toss.InsertKeyFrame(0.76f, new Vector3(
            firstLandingX + direction * 3 * dpiScale,
            position.Y - 5 * dpiScale,
            0), rise);
        toss.InsertKeyFrame(1, position, fall);

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        dropBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(dropBatch, batch))
            {
                return;
            }
            dropBatch = null;
            root.Offset = position;
            batch.Dispose();
        };
        root.StartAnimation(nameof(root.Offset), toss);
        batch.End();
    }

    private void FinishDrop()
    {
        CompositionScopedBatch? batch = dropBatch;
        dropBatch = null;
        batch?.Dispose();
        root.StopAnimation(nameof(root.Offset));
        root.Offset = position;
    }

    private void OnDragPose(Vector3 updatedPosition, Vector3 velocity, bool released)
    {
        position = updatedPosition;
        if (!released)
        {
            return;
        }
        StopDragMotion();
        MotionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void StopDragMotion()
    {
        root.StopAnimation(nameof(root.Offset));
        dragMotion?.Dispose();
        dragMotion = null;
        releasingDrag = false;
        root.Offset = position;
    }

    private void Rebuild(float nextDpiScale, Size nextClientSize)
    {
        dpiScale = nextDpiScale;
        clientSize = nextClientSize;
        root.Children.RemoveAll();
        foreach (CompositionObject resource in resources)
        {
            resource.Dispose();
        }
        resources.Clear();
        closedFrame.Dispose();
        openFrame.Dispose();
        closedFrame = BuildFrame(0);
        openFrame = BuildFrame(FrameWidth);
        closedFrame.Opacity = opened ? 0 : 1;
        openFrame.Opacity = opened ? 1 : 0;
        root.Size = new Vector2(FrameWidth * SpriteScale * dpiScale, FrameHeight * SpriteScale * dpiScale);
        root.Children.InsertAtTop(closedFrame);
        root.Children.InsertAtTop(openFrame);
    }

    private ContainerVisual BuildFrame(int frameX)
    {
        using Bitmap bitmap = new(spritePath);
        if (bitmap.Width != FrameWidth * 2 || bitmap.Height != FrameHeight)
        {
            throw new InvalidOperationException("The present sprite must contain two 12x14 frames.");
        }
        float pixelSize = SpriteScale * dpiScale;
        ContainerVisual frame = compositor.CreateContainerVisual();
        for (int y = 0; y < FrameHeight; y++)
        {
            for (int x = 0; x < FrameWidth; x++)
            {
                Color pixel = bitmap.GetPixel(frameX + x, y);
                if (pixel.A == 0 || pixel is { R: 255, G: 255, B: 255 })
                {
                    continue;
                }
                SpriteVisual visual = compositor.CreateSpriteVisual();
                visual.Size = new Vector2(pixelSize);
                visual.Offset = new Vector3(x * pixelSize, y * pixelSize, 0);
                visual.Brush = CreateBrush(pixel, x, y);
                frame.Children.InsertAtTop(visual);
                resources.Add(visual);
            }
        }
        return frame;
    }

    private CompositionColorBrush CreateBrush(Color pixel, int x, int y)
    {
        bool tint = pixel is { R: 255, G: 0, B: 255 };
        CompositionColorBrush brush = compositor.CreateColorBrush(tint
            ? color
            : Windows.UI.Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B));
        resources.Add(brush);
        if (tint && rainbow)
        {
            using ColorKeyFrameAnimation animation = compositor.CreateColorKeyFrameAnimation();
            animation.Duration = TimeSpan.FromSeconds(2.4);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            for (int step = 0; step <= 6; step++)
            {
                animation.InsertKeyFrame(step / 6f, Rainbow((step / 6f + (x + y) / 18f) % 1));
            }
            brush.StartAnimation(nameof(brush.Color), animation);
        }
        return brush;
    }

    private static Windows.UI.Color Rainbow(float hue)
    {
        float section = hue * 6;
        int index = (int)Math.Floor(section) % 6;
        byte rising = (byte)Math.Round((section - MathF.Floor(section)) * 255);
        byte falling = (byte)(255 - rising);
        return index switch
        {
            0 => Windows.UI.Color.FromArgb(255, 255, rising, 70),
            1 => Windows.UI.Color.FromArgb(255, falling, 255, 70),
            2 => Windows.UI.Color.FromArgb(255, 70, 255, rising),
            3 => Windows.UI.Color.FromArgb(255, 70, falling, 255),
            4 => Windows.UI.Color.FromArgb(255, rising, 70, 255),
            _ => Windows.UI.Color.FromArgb(255, 255, 70, falling)
        };
    }

    public void Dispose()
    {
        StopDragMotion();
        dropBatch?.Dispose();
        dropBatch = null;
        openingBatch?.Dispose();
        openingBatch = null;
        if (root.Parent == parent)
        {
            parent.Children.Remove(root);
        }
        root.Children.RemoveAll();
        closedFrame.Dispose();
        openFrame.Dispose();
        foreach (CompositionObject resource in resources)
        {
            resource.Dispose();
        }
        resources.Clear();
        root.Dispose();
    }
}
