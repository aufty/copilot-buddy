namespace TaskbarBuddy.Core;

public readonly record struct PassiveMotionPlan(PresentationSnapshot Start, PresentationSnapshot End, TimeSpan Duration);

public interface IPresentationApi
{
    event EventHandler? ActionRequested;
    void ShowMessage(string text);
    void DismissMessage();
    void SetVisible(bool visible);
}

public sealed class PresentationController : IPresentationApi
{
    private readonly PresentationOptions options;
    private readonly WanderController wander;
    private double attentionElapsed;
    private double breathingElapsed;
    private double minimumX;
    private double maximumX;
    private double maximumLift;
    private double interactionX;
    private double liftOffset;
    private double velocityX;
    private double velocityLift;
    private double dragTargetX;
    private double dragTargetLift;
    private double dragOffsetX;
    private double dragOffsetLift;
    private double dragSettlingTolerance;
    private double landingElapsed;
    private double landingSquish;
    private bool isDragging;
    private bool isAirborne;
    private bool isLanding;
    private bool isHovered;

    public PresentationController(PresentationOptions options, IRandomSource random, double minimumX, double maximumX)
    {
        options.Validate();
        this.options = options;
        this.minimumX = minimumX;
        this.maximumX = maximumX;
        wander = new WanderController(options.Wander, random, minimumX, maximumX);
        interactionX = wander.X;
    }

    public event EventHandler? ActionRequested;
    public bool IsVisible { get; private set; } = true;
    public string? Message { get; private set; }

    public PresentationSnapshot Snapshot
    {
        get
        {
            if (isDragging || isAirborne || isLanding)
            {
                (double landingScaleX, double landingScaleY) = GetLandingScale();
                VisualState state = isDragging
                    ? VisualState.Dragging
                    : isAirborne ? VisualState.Airborne : VisualState.Landing;
                return new(IsVisible, state, interactionX, SpriteFrame.Standing, liftOffset, landingScaleX, landingScaleY, Message);
            }

            if (Message is not null)
            {
                double waveInterval = options.Attention.ReducedMotion
                    ? options.Attention.ReducedMotionWaveFrameSeconds
                    : options.Attention.WaveFrameSeconds;
                SpriteFrame frame = (int)(attentionElapsed / waveInterval) % 2 == 0
                    ? SpriteFrame.Wave1
                    : SpriteFrame.Wave2;
                return new(IsVisible, VisualState.Attention, wander.X, frame, GetHopOffset(), 1, 1, Message);
            }

            bool visuallyIdle = isHovered || wander.State == VisualState.Idle;
            SpriteFrame wanderFrame = visuallyIdle
                ? SpriteFrame.Standing
                : GetWalkFrame();
            (double scaleX, double scaleY) = GetBreathingScale(visuallyIdle);
            return new(IsVisible, visuallyIdle ? VisualState.Idle : wander.State, wander.X, wanderFrame, 0, scaleX, scaleY, null);
        }
    }

    public void ShowMessage(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Message = text;
        attentionElapsed = 0;
    }

    public void DismissMessage()
    {
        if (Message is null)
        {
            return;
        }
        Message = null;
        attentionElapsed = 0;
        breathingElapsed = 0;
        wander.StartNewIdle();
    }

    public void SetVisible(bool visible)
    {
        if (IsVisible == visible)
        {
            return;
        }
        IsVisible = visible;
        if (visible && Message is not null)
        {
            attentionElapsed = 0;
        }
    }

    public void Tick(TimeSpan elapsed)
    {
        if (!IsVisible || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        if (isDragging)
        {
            double seconds = Math.Min(elapsed.TotalSeconds, 0.1);
            (interactionX, velocityX) = SmoothDrag(interactionX, velocityX, dragTargetX, seconds);
            (liftOffset, velocityLift) = SmoothDrag(liftOffset, velocityLift, dragTargetLift, seconds);
            interactionX = Math.Clamp(interactionX, minimumX, maximumX);
            liftOffset = Math.Clamp(liftOffset, 0, maximumLift);
            double distanceX = interactionX - dragTargetX;
            double distanceLift = liftOffset - dragTargetLift;
            double settlingSpeed = dragSettlingTolerance / options.Physics.DragResponseSeconds;
            if (dragSettlingTolerance > 0 &&
                distanceX * distanceX + distanceLift * distanceLift <= dragSettlingTolerance * dragSettlingTolerance &&
                velocityX * velocityX + velocityLift * velocityLift <= settlingSpeed * settlingSpeed)
            {
                interactionX = dragTargetX;
                liftOffset = dragTargetLift;
                velocityX = 0;
                velocityLift = 0;
            }
        }
        else if (isAirborne || isLanding)
        {
            TickInteraction(Math.Min(elapsed.TotalSeconds, 0.1));
        }
        else if (Message is not null)
        {
            attentionElapsed += elapsed.TotalSeconds;
        }
        else if (isHovered)
        {
            breathingElapsed += elapsed.TotalSeconds;
        }
        else
        {
            wander.Tick(elapsed.TotalSeconds);
            if (wander.State == VisualState.Idle)
            {
                breathingElapsed += elapsed.TotalSeconds;
            }
            else
            {
                breathingElapsed = 0;
            }
        }
    }

    public void SetHorizontalBounds(double minimumX, double maximumX)
    {
        this.minimumX = minimumX;
        this.maximumX = maximumX;
        interactionX = Math.Clamp(interactionX, minimumX, maximumX);
        dragTargetX = Math.Clamp(dragTargetX, minimumX, maximumX);
        wander.SetBounds(minimumX, maximumX);
    }

    public void SetMaximumLift(double maximumLift)
    {
        this.maximumLift = Math.Max(0, maximumLift);
        liftOffset = Math.Clamp(liftOffset, 0, this.maximumLift);
        dragTargetLift = Math.Clamp(dragTargetLift, 0, this.maximumLift);
    }

    public void SetHovered(bool hovered)
    {
        if (isHovered == hovered)
        {
            return;
        }

        isHovered = hovered;
        breathingElapsed = 0;
        if (!hovered && !isDragging && !isAirborne && !isLanding && Message is null)
        {
            wander.StartNewIdle();
        }
    }

    public void BeginDrag(double pointerX, double pointerLift)
    {
        if (!IsVisible)
        {
            return;
        }

        PresentationSnapshot current = Snapshot;
        interactionX = current.X;
        liftOffset = current.HopOffset;
        dragOffsetX = interactionX - pointerX;
        dragOffsetLift = liftOffset - pointerLift;
        dragTargetX = interactionX;
        dragTargetLift = liftOffset;
        velocityX = 0;
        velocityLift = 0;
        landingElapsed = 0;
        isDragging = true;
        isAirborne = false;
        isLanding = false;
    }

    public void UpdateDrag(double pointerX, double pointerLift)
    {
        TryUpdateDrag(pointerX, pointerLift);
    }

    public bool TryUpdateDrag(double pointerX, double pointerLift)
    {
        if (!isDragging)
        {
            return false;
        }
        double targetX = Math.Clamp(pointerX + dragOffsetX, minimumX, maximumX);
        double targetLift = Math.Clamp(pointerLift + dragOffsetLift, 0, maximumLift);
        if (targetX == dragTargetX && targetLift == dragTargetLift)
        {
            return false;
        }
        dragTargetX = targetX;
        dragTargetLift = targetLift;
        return true;
    }

    public void EndDrag()
    {
        EndDragFromPose(interactionX, liftOffset, velocityX, velocityLift);
    }

    public void SetExternalDragPose(double positionX, double lift, double horizontalVelocity, double verticalVelocity)
    {
        if (!double.IsFinite(positionX) || !double.IsFinite(lift) ||
            !double.IsFinite(horizontalVelocity) || !double.IsFinite(verticalVelocity))
        {
            throw new ArgumentOutOfRangeException(nameof(positionX));
        }
        if (!isDragging)
        {
            return;
        }
        interactionX = Math.Clamp(positionX, minimumX, maximumX);
        liftOffset = Math.Clamp(lift, 0, maximumLift);
        velocityX = horizontalVelocity;
        velocityLift = verticalVelocity;
    }

    public void SetExternalFlightPose(double positionX, double lift, double verticalVelocity = 0)
    {
        if (!double.IsFinite(positionX) || !double.IsFinite(lift) || !double.IsFinite(verticalVelocity))
        {
            throw new ArgumentOutOfRangeException(nameof(positionX));
        }
        if (isAirborne)
        {
            interactionX = Math.Clamp(positionX, minimumX, maximumX);
            liftOffset = Math.Clamp(lift, 0, maximumLift);
            velocityLift = verticalVelocity;
        }
    }

    public void EndDragFromPose(double positionX, double lift, double horizontalVelocity, double verticalVelocity)
    {
        if (!isDragging)
        {
            return;
        }
        SetExternalDragPose(positionX, lift, horizontalVelocity, verticalVelocity);
        isDragging = false;
        isAirborne = true;
        velocityX = Math.Clamp(velocityX, -options.Physics.MaximumReleaseSpeed, options.Physics.MaximumReleaseSpeed);
        velocityLift = Math.Clamp(velocityLift, -options.Physics.MaximumReleaseSpeed, options.Physics.MaximumReleaseSpeed);
    }

    public void CancelDrag()
    {
        if (!isDragging)
        {
            return;
        }
        isDragging = false;
        isAirborne = false;
        isLanding = false;
        liftOffset = 0;
        velocityX = 0;
        velocityLift = 0;
        wander.PlaceAt(interactionX);
        wander.StartNewIdle();
    }

    public void SetDragSettlingTolerance(double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }
        dragSettlingTolerance = tolerance;
    }

    public double WalkingSpeedMultiplier => wander.SpeedMultiplier;

    public void SetWalkingSpeedMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }
        wander.SpeedMultiplier = multiplier;
    }

    public void ResetWandering()
    {
        if (isDragging || isAirborne || isLanding)
        {
            return;
        }

        wander.PlaceAt(wander.X);
        wander.StartNewIdle();
        breathingElapsed = 0;
    }

    public PassiveMotionPlan? PlanPassiveMotion()
    {
        if (!IsVisible || isDragging || isAirborne || isLanding || isHovered || Message is not null)
        {
            return null;
        }
        PresentationSnapshot start = Snapshot;
        PresentationSnapshot end = start with { X = wander.State == VisualState.Walking ? wander.Destination : start.X };
        return new PassiveMotionPlan(start, end, TimeSpan.FromSeconds(wander.SecondsUntilTransition));
    }

    public IReadOnlyList<PresentationSnapshot> PreviewInteraction(TimeSpan step, int maximumSteps)
    {
        if (step <= TimeSpan.Zero || step > TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }
        if (maximumSteps < 1 || maximumSteps > 1200)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSteps));
        }
        if (!isDragging && !isAirborne && !isLanding)
        {
            return [Snapshot];
        }

        PresentationController preview = new(options, new SystemRandomSource(0), minimumX, maximumX)
        {
            maximumLift = maximumLift,
            interactionX = interactionX,
            liftOffset = liftOffset,
            velocityX = velocityX,
            velocityLift = velocityLift,
            dragTargetX = dragTargetX,
            dragTargetLift = dragTargetLift,
            dragSettlingTolerance = dragSettlingTolerance,
            landingElapsed = landingElapsed,
            landingSquish = landingSquish,
            isDragging = isDragging,
            isAirborne = isAirborne,
            isLanding = isLanding,
            isHovered = true,
            IsVisible = IsVisible,
            Message = Message
        };
        List<PresentationSnapshot> samples = new(maximumSteps + 1) { preview.Snapshot };
        for (int index = 0; index < maximumSteps; index++)
        {
            preview.Tick(step);
            samples.Add(preview.Snapshot);
            if (dragSettlingTolerance > 0 && preview.isDragging &&
                preview.interactionX == preview.dragTargetX && preview.liftOffset == preview.dragTargetLift &&
                preview.velocityX == 0 && preview.velocityLift == 0)
            {
                break;
            }
            if (!preview.isDragging && !preview.isAirborne && !preview.isLanding)
            {
                break;
            }
        }
        return samples;
    }

    public void RequestAction() => ActionRequested?.Invoke(this, EventArgs.Empty);

    private void TickInteraction(double elapsedSeconds)
    {
        const double maximumStep = 1.0 / 120;
        while (elapsedSeconds > 0)
        {
            double step = Math.Min(maximumStep, elapsedSeconds);
            if (isAirborne)
            {
                velocityLift -= options.Physics.Gravity * step;
                interactionX += velocityX * step;
                liftOffset += velocityLift * step;
                BounceOffWalls();
                if (liftOffset <= 0 && velocityLift <= 0)
                {
                    BeginLanding(-velocityLift);
                }
            }
            else if (isLanding)
            {
                landingElapsed += step;
                if (landingElapsed >= options.Physics.LandingDurationSeconds)
                {
                    isLanding = false;
                    landingSquish = 0;
                    wander.PlaceAt(interactionX);
                    wander.StartNewIdle();
                }
            }
            elapsedSeconds -= step;
        }

        interactionX = Math.Clamp(interactionX, minimumX, maximumX);
        liftOffset = Math.Clamp(liftOffset, 0, maximumLift);
    }

    private (double Position, double Velocity) SmoothDrag(double position, double velocity, double target, double elapsedSeconds)
    {
        double angularFrequency = 2 / options.Physics.DragResponseSeconds;
        double error = position - target;
        double intermediate = velocity + angularFrequency * error;
        double decay = Math.Exp(-angularFrequency * elapsedSeconds);
        double nextError = (error + intermediate * elapsedSeconds) * decay;
        double nextVelocity = (velocity - angularFrequency * intermediate * elapsedSeconds) * decay;
        return (target + nextError, nextVelocity);
    }

    private void BounceOffWalls()
    {
        if (interactionX < minimumX)
        {
            interactionX = minimumX;
            velocityX = Math.Abs(velocityX) * options.Physics.WallRestitution;
        }
        else if (interactionX > maximumX)
        {
            interactionX = maximumX;
            velocityX = -Math.Abs(velocityX) * options.Physics.WallRestitution;
        }
    }

    private void BeginLanding(double impactSpeed)
    {
        liftOffset = 0;
        velocityX = 0;
        velocityLift = 0;
        landingElapsed = 0;
        landingSquish = Math.Min(options.Physics.MaximumLandingSquish,
            options.Physics.MaximumLandingSquish * impactSpeed / 700);
        isAirborne = false;
        isLanding = true;
    }

    private (double ScaleX, double ScaleY) GetLandingScale()
    {
        if (!isLanding || options.Attention.ReducedMotion || landingSquish == 0)
        {
            return (1, 1);
        }

        double progress = landingElapsed / options.Physics.LandingDurationSeconds;
        double deformation = landingSquish * Math.Exp(-5 * progress) * Math.Cos(progress * Math.Tau * 1.5);
        return (1 + deformation * 0.75, 1 - deformation);
    }

    private SpriteFrame GetWalkFrame()
    {
        bool first = (int)(wander.MovementElapsed / options.Wander.WalkFrameSeconds) % 2 == 0;
        return wander.Direction switch
        {
            HorizontalDirection.Left => first ? SpriteFrame.WalkLeft1 : SpriteFrame.WalkLeft2,
            _ => first ? SpriteFrame.WalkRight1 : SpriteFrame.WalkRight2
        };
    }

    private double GetHopOffset()
    {
        if (options.Attention.ReducedMotion || options.Attention.HopHeight == 0)
        {
            return 0;
        }

        double phase = attentionElapsed % options.Attention.HopDurationSeconds / options.Attention.HopDurationSeconds;
        double normalizedHeight;
        if (phase < 0.5)
        {
            double progress = phase * 2;
            normalizedHeight = 1 - Math.Pow(1 - progress, 3);
        }
        else
        {
            double progress = (phase - 0.5) * 2;
            normalizedHeight = 1 - Math.Pow(progress, 3);
        }
        return options.Attention.HopHeight * normalizedHeight;
    }

    private (double ScaleX, double ScaleY) GetBreathingScale(bool visuallyIdle)
    {
        if (!visuallyIdle || options.Attention.ReducedMotion || options.Breathing.Amount == 0)
        {
            return (1, 1);
        }

        double phase = breathingElapsed / options.Breathing.DurationSeconds * Math.Tau;
        double breath = Math.Sin(phase) * options.Breathing.Amount;
        return (1 - breath * 0.65, 1 + breath);
    }
}

public readonly record struct PresentationSnapshot(
    bool IsVisible,
    VisualState State,
    double X,
    SpriteFrame Frame,
    double HopOffset,
    double ScaleX,
    double ScaleY,
    string? Message)
{
    public static PresentationSnapshot Interpolate(PresentationSnapshot previous, PresentationSnapshot current, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return current with
        {
            X = Lerp(previous.X, current.X, amount),
            HopOffset = Lerp(previous.HopOffset, current.HopOffset, amount),
            ScaleX = Lerp(previous.ScaleX, current.ScaleX, amount),
            ScaleY = Lerp(previous.ScaleY, current.ScaleY, amount)
        };
    }

    private static double Lerp(double start, double end, double amount) => start + (end - start) * amount;
}

public enum VisualState
{
    Idle,
    Walking,
    Attention,
    Dragging,
    Airborne,
    Landing
}

public enum HorizontalDirection
{
    Left,
    Right
}

public enum SpriteFrame
{
    Standing,
    WalkLeft1,
    WalkLeft2,
    WalkRight1,
    WalkRight2,
    Wave1,
    Wave2
}