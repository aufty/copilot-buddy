using System.Numerics;
using CopilotBuddy.Core;
using Windows.UI.Composition;
using Windows.UI.Composition.Interactions;

namespace CopilotBuddy.Composition;

internal sealed class SupplyItem : IDisposable
{
    private const int PixelScale = 2;
    private readonly Compositor compositor;
    private readonly ContainerVisual parent;
    private readonly ContainerVisual root;
    private readonly PositionObserver positionObserver;
    private readonly List<CompositionObject> resources = [];
    private CompositionScopedBatch? dropBatch;
    private CompositionScopedBatch? throwBatch;
    private CompositionScopedBatch? consumptionBatch;
    private NativeDragMotion? dragMotion;
    private Vector3 grabOffset;
    private bool releasingDrag;
    private bool occupied;
    private float dpiScale;
    private Size clientSize;
    private Vector3 position;

    public SupplyItem(
        Compositor compositor,
        ContainerVisual parent,
        SupplyKind kind,
        float dpiScale,
        Size clientSize)
    {
        this.compositor = compositor;
        this.parent = parent;
        Kind = kind;
        root = compositor.CreateContainerVisual();
        parent.Children.InsertAtTop(root);
        Rebuild(dpiScale, clientSize);
        positionObserver = new PositionObserver(root);
    }

    public event EventHandler? Landed;
    public event EventHandler? ThrowCompleted;
    public event EventHandler? ConsumptionCompleted;
    public SupplyKind Kind { get; }
    public bool IsCarried { get; private set; } = true;
    public bool IsDropping => dropBatch is not null;
    public bool IsMoving => dropBatch is not null || throwBatch is not null ||
        consumptionBatch is not null || dragMotion is not null;
    public bool IsOccupied => occupied;
    public bool IsBuddyOwned => consumptionBatch is not null;
    public bool CanGrab => !occupied && !IsBuddyOwned;
    public float CenterX => CurrentPosition.X + root.Size.X / 2;
    public float Width => root.Size.X;
    public float Height => root.Size.Y;
    public Vector3 CurrentPosition => IsMoving ? positionObserver.Position : position;

    public void SetPaused(bool paused)
    {
        SetAnimationPaused(root, nameof(root.Offset), paused);
        SetAnimationPaused(root, nameof(root.Scale), paused);
        SetAnimationPaused(root, nameof(root.Opacity), paused);
        SetAnimationPaused(root, nameof(root.RotationAngleInDegrees), paused);
    }

    public void Follow(Point point)
    {
        if (!IsCarried)
        {
            return;
        }
        position = new Vector3(
            Math.Clamp(point.X - root.Size.X / 2, 0, Math.Max(0, clientSize.Width - root.Size.X)),
            Math.Clamp(point.Y - root.Size.Y / 2, 0, Math.Max(0, clientSize.Height - root.Size.Y)),
            0);
        root.Offset = position;
    }

    public bool HitTest(Point point)
    {
        if (!CanGrab || IsCarried)
        {
            return false;
        }
        Vector3 current = CurrentPosition;
        return new RectangleF(current.X, current.Y, root.Size.X, root.Size.Y).Contains(point);
    }

    public bool HitTestAttached(Point point, Vector3 buddyPosition)
    {
        if (!occupied)
        {
            return false;
        }
        Vector3 local = position;
        return new RectangleF(
            buddyPosition.X + local.X,
            buddyPosition.Y + local.Y,
            root.Size.X,
            root.Size.Y).Contains(point);
    }

    public void BringToFront()
    {
        if (root.Parent == parent)
        {
            parent.Children.Remove(root);
            parent.Children.InsertAtTop(root);
        }
    }

    public void BeginDrag(
        Point point,
        PhysicsOptions physics,
        bool direct,
        bool reducedMotion,
        Action<Action> dispatch)
    {
        if (!CanGrab)
        {
            return;
        }
        FinishMotionAt(point);
        IsCarried = true;
        BringToFront();
        grabOffset = position - new Vector3(point.X, point.Y, 0);
        releasingDrag = false;
        dragMotion = new NativeDragMotion(
            compositor,
            root,
            position,
            new Vector3(Math.Max(0, clientSize.Width - root.Size.X),
                Math.Max(0, clientSize.Height - root.Size.Y), 0),
            physics.DragResponseSeconds,
            (float)physics.MaximumReleaseSpeed * dpiScale,
            (float)physics.Gravity * dpiScale,
            (float)physics.WallRestitution,
            direct,
            new Vector2(point.X, point.Y) - new Vector2(position.X, position.Y),
            !reducedMotion,
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
            new Vector3(Math.Max(0, clientSize.Width - root.Size.X),
                Math.Max(0, clientSize.Height - root.Size.Y), 0));
        dragMotion.SetTarget(target);
    }

    public void TickDrag() => dragMotion?.Tick();

    public void ReleaseDrag()
    {
        if (dragMotion is null || releasingDrag)
        {
            return;
        }
        releasingDrag = true;
        dragMotion.Release();
    }

    public void AttachToBuddy(ContainerVisual buddy)
    {
        if (Kind != SupplyKind.Chair || occupied)
        {
            return;
        }
        FinishMotion();
        if (root.Parent == parent)
        {
            parent.Children.Remove(root);
        }
        buddy.Children.InsertAtTop(root);
        position = new Vector3(
            (buddy.Size.X - root.Size.X) / 2,
            buddy.Size.Y - root.Size.Y,
            0);
        root.Offset = position;
        occupied = true;
        IsCarried = false;
    }

    public void ReattachToBuddy(ContainerVisual buddy)
    {
        if (!occupied)
        {
            return;
        }
        if (root.Parent is ContainerVisual current)
        {
            current.Children.Remove(root);
        }
        buddy.Children.InsertAtTop(root);
        position = new Vector3(
            (buddy.Size.X - root.Size.X) / 2,
            buddy.Size.Y - root.Size.Y,
            0);
        root.Offset = position;
    }

    public void DetachFromBuddy(ContainerVisual buddy, double buddyXDip, double buddyWidthDip)
    {
        if (!occupied)
        {
            return;
        }
        if (root.Parent is ContainerVisual current)
        {
            current.Children.Remove(root);
        }
        parent.Children.InsertBelow(root, buddy);
        position = new Vector3(
            Math.Clamp((float)((buddyXDip + buddyWidthDip / 2) * dpiScale - root.Size.X / 2),
                0, Math.Max(0, clientSize.Width - root.Size.X)),
            clientSize.Height - root.Size.Y,
            0);
        root.Offset = position;
        occupied = false;
        IsCarried = false;
    }

    public void Drop()
    {
        if (!IsCarried)
        {
            return;
        }
        IsCarried = false;
        Vector3 landing = new(position.X, clientSize.Height - root.Size.Y, 0);
        if (landing.Y <= position.Y || compositor is null)
        {
            position = landing;
            root.Offset = landing;
            Landed?.Invoke(this, EventArgs.Empty);
            return;
        }

        using Vector3KeyFrameAnimation fall = compositor.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction gravity = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.55f, 0), new Vector2(0.85f, 0.35f));
        using CubicBezierEasingFunction rebound = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 0.8f), new Vector2(0.28f, 1));
        fall.Duration = TimeSpan.FromMilliseconds(Math.Clamp(
            280 + (landing.Y - position.Y) * 0.7, 280, 720));
        fall.InsertKeyFrame(0, position);
        fall.InsertKeyFrame(0.78f, landing, gravity);
        fall.InsertKeyFrame(0.9f, landing - new Vector3(0, 4 * dpiScale, 0), rebound);
        fall.InsertKeyFrame(1, landing, gravity);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        dropBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(dropBatch, batch))
            {
                return;
            }
            dropBatch = null;
            position = landing;
            root.Offset = landing;
            batch.Dispose();
            Landed?.Invoke(this, EventArgs.Empty);
        };
        root.StartAnimation(nameof(root.Offset), fall);
        batch.End();
    }

    public void ThrowBall(
        double horizontalVelocityDip,
        double upwardVelocityDip,
        PhysicsOptions physics,
        double floorRestitution)
    {
        if (Kind != SupplyKind.Ball || IsCarried || IsMoving ||
            !double.IsFinite(horizontalVelocityDip) || !double.IsFinite(upwardVelocityDip) ||
            horizontalVelocityDip == 0 || upwardVelocityDip <= 0 ||
            !double.IsFinite(floorRestitution) || floorRestitution <= 0 || floorRestitution >= 1)
        {
            return;
        }

        const float stepSeconds = 1f / 120;
        const int maximumSteps = 360;
        float floor = clientSize.Height - root.Size.Y;
        float maximumX = Math.Max(0, clientSize.Width - root.Size.X);
        float x = position.X;
        float y = floor;
        float velocityX = (float)horizontalVelocityDip * dpiScale;
        float velocityY = -(float)upwardVelocityDip * dpiScale;
        float gravity = (float)physics.Gravity * dpiScale;
        List<Vector3> samples = [new Vector3(x, y, 0)];
        for (int step = 1; step <= maximumSteps; step++)
        {
            velocityY += gravity * stepSeconds;
            x += velocityX * stepSeconds;
            y += velocityY * stepSeconds;
            if (x < 0)
            {
                x = 0;
                velocityX = Math.Abs(velocityX) * (float)physics.WallRestitution;
            }
            else if (x > maximumX)
            {
                x = maximumX;
                velocityX = -Math.Abs(velocityX) * (float)physics.WallRestitution;
            }
            if (y >= floor && velocityY > 0)
            {
                y = floor;
                velocityY = -velocityY * (float)floorRestitution;
                velocityX *= 0.82f;
                if (step > 36 &&
                    Math.Abs(velocityY) < 38 * dpiScale &&
                    Math.Abs(velocityX) < 32 * dpiScale)
                {
                    samples.Add(new Vector3(x, y, 0));
                    break;
                }
            }
            y = Math.Max(0, y);
            samples.Add(new Vector3(x, y, 0));
        }
        Vector3 landing = new(samples[^1].X, floor, 0);
        samples[^1] = landing;
        using Vector3KeyFrameAnimation toss = compositor.CreateVector3KeyFrameAnimation();
        using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
        toss.Duration = TimeSpan.FromSeconds(stepSeconds * (samples.Count - 1));
        for (int index = 0; index < samples.Count; index++)
        {
            toss.InsertKeyFrame((float)index / (samples.Count - 1), samples[index], linear);
        }
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        throwBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(throwBatch, batch))
            {
                return;
            }
            throwBatch = null;
            position = landing;
            root.Offset = landing;
            batch.Dispose();
            ThrowCompleted?.Invoke(this, EventArgs.Empty);
        };
        root.StartAnimation(nameof(root.Offset), toss);
        batch.End();
    }

    public void ConsumeAt(Point face)
    {
        if (Kind != SupplyKind.Food || IsCarried || IsMoving)
        {
            return;
        }

        Vector3 start = position;
        Vector3 bite = new(
            Math.Clamp(face.X - root.Size.X / 2, 0, Math.Max(0, clientSize.Width - root.Size.X)),
            Math.Clamp(face.Y - root.Size.Y / 2, 0, Math.Max(0, clientSize.Height - root.Size.Y)),
            0);
        Vector3 lower = bite + new Vector3(0, 3 * dpiScale, 0);
        root.CenterPoint = new Vector3(root.Size.X / 2, root.Size.Y / 2, 0);
        using Vector3KeyFrameAnimation movement = compositor.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction lift = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 0.8f), new Vector2(0.28f, 1));
        movement.Duration = TimeSpan.FromMilliseconds(1450);
        movement.InsertKeyFrame(0, start);
        movement.InsertKeyFrame(0.18f, bite, lift);
        movement.InsertKeyFrame(0.3f, lower);
        movement.InsertKeyFrame(0.4f, bite);
        movement.InsertKeyFrame(0.52f, lower);
        movement.InsertKeyFrame(0.62f, bite);
        movement.InsertKeyFrame(0.74f, lower);
        movement.InsertKeyFrame(0.84f, bite);
        movement.InsertKeyFrame(1, bite);
        using Vector3KeyFrameAnimation shrink = compositor.CreateVector3KeyFrameAnimation();
        shrink.Duration = movement.Duration;
        shrink.InsertKeyFrame(0, Vector3.One);
        shrink.InsertKeyFrame(0.32f, new Vector3(0.82f, 0.82f, 1));
        shrink.InsertKeyFrame(0.54f, new Vector3(0.62f, 0.62f, 1));
        shrink.InsertKeyFrame(0.76f, new Vector3(0.4f, 0.4f, 1));
        shrink.InsertKeyFrame(1, new Vector3(0.1f, 0.1f, 1));
        using ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = movement.Duration;
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(0.84f, 1);
        fade.InsertKeyFrame(1, 0);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        consumptionBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(consumptionBatch, batch))
            {
                return;
            }
            consumptionBatch = null;
            position = bite;
            root.Offset = bite;
            root.Scale = new Vector3(0.1f, 0.1f, 1);
            root.Opacity = 0;
            batch.Dispose();
            ConsumptionCompleted?.Invoke(this, EventArgs.Empty);
        };
        root.StartAnimation(nameof(root.Offset), movement);
        root.StartAnimation(nameof(root.Scale), shrink);
        root.StartAnimation(nameof(root.Opacity), fade);
        batch.End();
    }

    public void DrinkAt(Point face)
    {
        if (Kind != SupplyKind.Water || IsCarried || IsMoving)
        {
            return;
        }

        Vector3 start = position;
        Vector3 sip = new(
            Math.Clamp(face.X - root.Size.X * 0.35f, 0, Math.Max(0, clientSize.Width - root.Size.X)),
            Math.Clamp(face.Y - root.Size.Y * 0.65f, 0, Math.Max(0, clientSize.Height - root.Size.Y)),
            0);
        Vector3 lower = sip + new Vector3(0, 2 * dpiScale, 0);
        root.CenterPoint = new Vector3(root.Size.X / 2, root.Size.Y / 2, 0);
        using Vector3KeyFrameAnimation movement = compositor.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction lift = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 0.8f), new Vector2(0.28f, 1));
        movement.Duration = TimeSpan.FromMilliseconds(1650);
        movement.InsertKeyFrame(0, start);
        movement.InsertKeyFrame(0.2f, sip, lift);
        movement.InsertKeyFrame(0.32f, lower);
        movement.InsertKeyFrame(0.42f, sip);
        movement.InsertKeyFrame(0.54f, lower);
        movement.InsertKeyFrame(0.64f, sip);
        movement.InsertKeyFrame(0.76f, lower);
        movement.InsertKeyFrame(0.86f, sip);
        movement.InsertKeyFrame(1, sip);
        using ScalarKeyFrameAnimation tilt = compositor.CreateScalarKeyFrameAnimation();
        tilt.Duration = movement.Duration;
        tilt.InsertKeyFrame(0, 0);
        tilt.InsertKeyFrame(0.2f, -28);
        tilt.InsertKeyFrame(0.32f, -36);
        tilt.InsertKeyFrame(0.42f, -25);
        tilt.InsertKeyFrame(0.54f, -38);
        tilt.InsertKeyFrame(0.64f, -25);
        tilt.InsertKeyFrame(0.76f, -40);
        tilt.InsertKeyFrame(0.88f, -24);
        tilt.InsertKeyFrame(1, -20);
        using Vector3KeyFrameAnimation emptying = compositor.CreateVector3KeyFrameAnimation();
        emptying.Duration = movement.Duration;
        emptying.InsertKeyFrame(0, Vector3.One);
        emptying.InsertKeyFrame(0.42f, new Vector3(1, 0.82f, 1));
        emptying.InsertKeyFrame(0.64f, new Vector3(1, 0.58f, 1));
        emptying.InsertKeyFrame(0.86f, new Vector3(1, 0.3f, 1));
        emptying.InsertKeyFrame(1, new Vector3(0.8f, 0.12f, 1));
        using ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = movement.Duration;
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(0.88f, 1);
        fade.InsertKeyFrame(1, 0);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        consumptionBatch = batch;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(consumptionBatch, batch))
            {
                return;
            }
            consumptionBatch = null;
            position = sip;
            root.Offset = sip;
            root.Opacity = 0;
            batch.Dispose();
            ConsumptionCompleted?.Invoke(this, EventArgs.Empty);
        };
        root.StartAnimation(nameof(root.Offset), movement);
        root.StartAnimation(nameof(root.RotationAngleInDegrees), tilt);
        root.StartAnimation(nameof(root.Scale), emptying);
        root.StartAnimation(nameof(root.Opacity), fade);
        batch.End();
    }

    public void Relayout(float nextDpiScale, Size nextClientSize)
    {
        float logicalX = occupied ? 0 : CurrentPosition.X / dpiScale;
        float logicalY = occupied ? 0 : CurrentPosition.Y / dpiScale;
        Rebuild(nextDpiScale, nextClientSize);
        if (occupied)
        {
            return;
        }
        position = new Vector3(
            Math.Clamp(logicalX * dpiScale,
                0, Math.Max(0, clientSize.Width - root.Size.X)),
            IsCarried ? Math.Clamp(logicalY * dpiScale, 0, Math.Max(0, clientSize.Height - root.Size.Y))
                : clientSize.Height - root.Size.Y,
            0);
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
        string[] pixels = Kind switch
        {
            SupplyKind.Food =>
            [
                "....RR....",
                "...RYYR...",
                "..RYYYYR..",
                ".RYYYYYYR.",
                ".RYYYYYYR.",
                "..RYYYYR..",
                "...RRRR...",
                "....GG...."
            ],
            SupplyKind.Water =>
            [
                "....BB....",
                "...BBBB...",
                "..BBBBBB..",
                ".BBBBBBBB.",
                ".BBBBBBBB.",
                ".BBWWBBBB.",
                "..BBBBBB..",
                "...BBBB..."
            ],
            SupplyKind.Ball =>
            [
                "...PPPP...",
                "..PWWWWP..",
                ".PWWPPWWP.",
                ".PWPPPPWP.",
                ".PWPPPPWP.",
                ".PWWPPWWP.",
                "..PWWWWP..",
                "...PPPP..."
            ],
            SupplyKind.Chair =>
            [
                "DD......DD",
                "DTTTTTTTTD",
                "DTTTTTTTTD",
                "DTT....TTD",
                "DTT....TTD",
                "DTT....TTD",
                "DTTTTTTTTD",
                "DDDDDDDDDD"
            ],
            _ => throw new ArgumentOutOfRangeException()
        };
        float pixelSize = PixelScale * dpiScale;
        root.Size = new Vector2(pixels[0].Length * pixelSize, pixels.Length * pixelSize);
        for (int y = 0; y < pixels.Length; y++)
        {
            for (int x = 0; x < pixels[y].Length; x++)
            {
                Windows.UI.Color? color = pixels[y][x] switch
                {
                    'R' => Windows.UI.Color.FromArgb(255, 205, 72, 62),
                    'Y' => Windows.UI.Color.FromArgb(255, 244, 192, 68),
                    'G' => Windows.UI.Color.FromArgb(255, 84, 176, 92),
                    'B' => Windows.UI.Color.FromArgb(255, 63, 165, 224),
                    'W' => Windows.UI.Color.FromArgb(255, 220, 244, 250),
                    'P' => Windows.UI.Color.FromArgb(255, 172, 90, 215),
                    'T' => Windows.UI.Color.FromArgb(255, 181, 132, 78),
                    'D' => Windows.UI.Color.FromArgb(255, 91, 62, 40),
                    _ => null
                };
                if (color is null)
                {
                    continue;
                }
                SpriteVisual pixel = compositor.CreateSpriteVisual();
                pixel.Size = new Vector2(pixelSize);
                pixel.Offset = new Vector3(x * pixelSize, y * pixelSize, 0);
                CompositionColorBrush brush = compositor.CreateColorBrush(color.Value);
                pixel.Brush = brush;
                root.Children.InsertAtTop(pixel);
                resources.Add(pixel);
                resources.Add(brush);
            }
        }
    }

    private static void SetAnimationPaused(CompositionObject target, string propertyName, bool paused)
    {
        AnimationController? controller = target.TryGetAnimationController(propertyName);
        if (controller is null)
        {
            return;
        }
        if (paused)
        {
            controller.Pause();
        }
        else
        {
            controller.Resume();
        }
    }

    private void FinishMotionAt(Point point)
    {
        FinishMotion();
        position = new Vector3(
            Math.Clamp(point.X - root.Size.X / 2, 0, Math.Max(0, clientSize.Width - root.Size.X)),
            Math.Clamp(point.Y - root.Size.Y / 2, 0, Math.Max(0, clientSize.Height - root.Size.Y)),
            0);
        root.Offset = position;
    }

    private void FinishMotion()
    {
        dropBatch?.Dispose();
        dropBatch = null;
        throwBatch?.Dispose();
        throwBatch = null;
        root.StopAnimation(nameof(root.Offset));
        StopDragMotion();
        position = positionObserver.Position;
        root.Offset = position;
    }

    private void OnDragPose(Vector3 updatedPosition, Vector3 velocity, bool released)
    {
        position = updatedPosition;
        if (!released)
        {
            return;
        }
        IsCarried = false;
        StopDragMotion();
        Landed?.Invoke(this, EventArgs.Empty);
    }

    private void StopDragMotion()
    {
        root.StopAnimation(nameof(root.Offset));
        dragMotion?.Dispose();
        dragMotion = null;
        releasingDrag = false;
        root.Offset = position;
    }

    public void Dispose()
    {
        dropBatch?.Dispose();
        dropBatch = null;
        throwBatch?.Dispose();
        throwBatch = null;
        consumptionBatch?.Dispose();
        consumptionBatch = null;
        dragMotion?.Dispose();
        dragMotion = null;
        positionObserver.Dispose();
        root.StopAnimation(nameof(root.Offset));
        root.StopAnimation(nameof(root.Scale));
        root.StopAnimation(nameof(root.Opacity));
        root.StopAnimation(nameof(root.RotationAngleInDegrees));
        if (root.Parent is ContainerVisual current)
        {
            current.Children.Remove(root);
        }
        root.Children.RemoveAll();
        foreach (CompositionObject resource in resources)
        {
            resource.Dispose();
        }
        resources.Clear();
        root.Dispose();
    }

    private sealed class PositionObserver : IInteractionTrackerOwner, IDisposable
    {
        private readonly InteractionTracker tracker;
        private readonly object positionLock = new();
        private Vector3 position;

        public PositionObserver(Visual visual)
        {
            position = visual.Offset;
            tracker = InteractionTracker.CreateWithOwner(visual.Compositor, this);
            tracker.MinPosition = new Vector3(-1_000_000);
            tracker.MaxPosition = new Vector3(1_000_000);
            tracker.TryUpdatePosition(position);
            using ExpressionAnimation following = visual.Compositor.CreateExpressionAnimation("visual.Offset");
            following.SetReferenceParameter("visual", visual);
            tracker.TryUpdatePositionWithAnimation(following);
        }

        public Vector3 Position
        {
            get
            {
                lock (positionLock)
                {
                    return position;
                }
            }
        }

        public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
        {
            lock (positionLock)
            {
                position = args.Position;
            }
        }

        public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }
        public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args) { }
        public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }
        public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args) { }
        public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }
        public void Dispose() => tracker.Dispose();
    }
}
