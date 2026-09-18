using System.Diagnostics;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Composition.Interactions;

namespace CopilotBuddy.Composition;

internal sealed class NativeDragMotion : IInteractionTrackerOwner, IDisposable
{
    private const float DirectCorrectionRate = 20;
    private const float DirectCorrectionSeconds = 0.35f;
    private const float DirectPredictionSeconds = 0.03f;
    private const float RotationSpringStrength = 70;
    private const float RotationDamping = 11;
    private const float MaximumDragAngle = 65;
    private static int nextRotationGeneration;
    private readonly InteractionTracker tracker;
    private readonly CompositionPropertySet motion;
    private readonly CompositionPropertySet? directClock;
    private readonly System.Threading.Timer? directSettleTimer;
    private readonly List<(CompositionPropertySet Clock, long Started)> clocks = [];
    private readonly Visual visual;
    private readonly Action<Action> dispatch;
    private readonly float omega;
    private readonly Vector3 bounds;
    private readonly float gravity;
    private readonly float restitution;
    private readonly Action<Vector3, Vector3, bool> report;
    private readonly bool direct;
    private readonly float maximumSpeed;
    private readonly bool rotate;
    private readonly Vector3 originalCenterPoint;
    private readonly Vector2 grabPoint;
    private readonly Vector2 visualCenter;
    private readonly int rotationGeneration;
    private Vector3 position;
    private Vector3 velocity;
    private Vector3 directTarget;
    private Vector3 directVelocity;
    private Vector3 directCorrection;
    private long directClockStarted;
    private long directSampleTimestamp;
    private long sampleTimestamp;
    private long rotationTimestamp;
    private long releaseTimestamp;
    private float rotationAngle;
    private float angularVelocity;
    private float releaseAngle;
    private float releaseSpin;
    private float impactSeconds;
    private int releaseRequest;
    private bool directSettled;
    private bool landingRotationPending;
    private bool disposed;

    public int MovingRetargets { get; private set; }

    public NativeDragMotion(Compositor compositor, Visual visual, Vector3 initialPosition,
        Vector3 maximumPosition, double responseSeconds, float maximumSpeed, float gravity, float restitution, bool direct,
        Vector2 grabPoint, bool rotate, Action<Action> dispatch, Action<Vector3, Vector3, bool> report)
    {
        this.visual = visual;
        this.dispatch = dispatch;
        this.report = report;
        this.direct = direct;
        this.maximumSpeed = maximumSpeed;
        this.rotate = rotate;
        originalCenterPoint = visual.CenterPoint;
        this.grabPoint = Vector2.Clamp(grabPoint, Vector2.Zero, visual.Size);
        visualCenter = visual.Size / 2;
        rotationGeneration = Interlocked.Increment(ref nextRotationGeneration);
        bounds = maximumPosition;
        this.gravity = gravity;
        this.restitution = restitution;
        position = initialPosition;
        omega = (float)(2 / responseSeconds);
        sampleTimestamp = rotationTimestamp = Stopwatch.GetTimestamp();
        if (rotate)
        {
            visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            visual.RotationAngleInDegrees = 0;
            visual.CenterPoint = new Vector3(this.grabPoint, 0);
            visual.Properties.InsertScalar("DragRotationGeneration", rotationGeneration);
        }
        motion = compositor.CreatePropertySet();
        motion.InsertVector4("State", new Vector4(initialPosition.X, initialPosition.Y, 0, 0));
        tracker = InteractionTracker.CreateWithOwner(compositor, this);
        tracker.MinPosition = Vector3.Zero;
        tracker.MaxPosition = maximumPosition;
        tracker.TryUpdatePosition(initialPosition);
        if (direct)
        {
            directTarget = initialPosition;
            directClockStarted = directSampleTimestamp = Stopwatch.GetTimestamp();
            motion.InsertVector4("Input", new Vector4(initialPosition.X, initialPosition.Y, 0, 0));
            motion.InsertVector3("Correction", Vector3.Zero);
            motion.InsertScalar("SampleTime", 0);
            motion.InsertScalar("PredictionSeconds", DirectPredictionSeconds);
            motion.InsertScalar("CorrectionRate", DirectCorrectionRate);
            motion.InsertScalar("CorrectionSeconds", DirectCorrectionSeconds);
            directClock = compositor.CreatePropertySet();
            directClock.InsertScalar("Seconds", 0);
            using ScalarKeyFrameAnimation elapsed = compositor.CreateScalarKeyFrameAnimation();
            elapsed.Duration = TimeSpan.FromHours(1);
            using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
            elapsed.InsertKeyFrame(0, 0);
            elapsed.InsertKeyFrame(1, 3600, linear);
            directClock.StartAnimation("Seconds", elapsed);
            string age = "Max(0, clock.Seconds - motion.SampleTime)";
            string predictionAge = $"Min({age}, motion.PredictionSeconds)";
            string correctionDecay =
                $"({age} < motion.CorrectionSeconds ? Pow(2.718281828, -motion.CorrectionRate * {age}) : 0)";
            using ExpressionAnimation tracking = compositor.CreateExpressionAnimation(
                $"Clamp(Vector3(motion.Input.X + motion.Input.Z * {predictionAge} + motion.Correction.X * {correctionDecay}, " +
                $"motion.Input.Y + motion.Input.W * {predictionAge} + motion.Correction.Y * {correctionDecay}, 0), " +
                "Vector3(0, 0, 0), bounds)");
            tracking.SetReferenceParameter("motion", motion);
            tracking.SetReferenceParameter("clock", directClock);
            tracking.SetVector3Parameter("bounds", maximumPosition);
            tracker.TryUpdatePositionWithAnimation(tracking);
            directSettleTimer = new System.Threading.Timer(_ => dispatch(() =>
            {
                if (!disposed && releaseRequest == 0)
                {
                    directSettled = true;
                    UpdateDragRotation(Vector3.Zero);
                    report(directTarget, Vector3.Zero, false);
                }
            }));
        }
        else
        {
            using ExpressionAnimation tracking = compositor.CreateExpressionAnimation(
                "Clamp(Vector3(motion.State.X, motion.State.Y, 0), Vector3(0, 0, 0), bounds)");
            tracking.SetReferenceParameter("motion", motion);
            tracking.SetVector3Parameter("bounds", maximumPosition);
            tracker.TryUpdatePositionWithAnimation(tracking);
        }
        using ExpressionAnimation following = compositor.CreateExpressionAnimation("Vector3(tracker.Position.X, tracker.Position.Y, 0)");
        following.SetReferenceParameter("tracker", tracker);
        visual.StartAnimation(nameof(Visual.Offset), following);
    }

    public void SetTarget(Vector3 target)
    {
        if (disposed || releaseRequest != 0)
        {
            return;
        }
        if (CurrentVelocity().Length() > 5)
        {
            MovingRetargets++;
        }
        if (direct)
        {
            long now = Stopwatch.GetTimestamp();
            Vector3 current = CurrentDirectPosition(now);
            double seconds = Stopwatch.GetElapsedTime(directSampleTimestamp, now).TotalSeconds;
            Vector3 measuredVelocity = seconds is > 0 and <= 0.1
                ? (target - directTarget) / (float)seconds
                : Vector3.Zero;
            measuredVelocity = Vector3.Clamp(measuredVelocity,
                new Vector3(-maximumSpeed), new Vector3(maximumSpeed));
            directVelocity = Vector3.Lerp(directVelocity, measuredVelocity, 0.4f);
            directTarget = target;
            directCorrection = current - target;
            directSampleTimestamp = now;
            directSettled = false;
            motion.InsertVector4("Input",
                new Vector4(target.X, target.Y, directVelocity.X, directVelocity.Y));
            motion.InsertVector3("Correction", directCorrection);
            motion.InsertScalar("SampleTime",
                (float)Stopwatch.GetElapsedTime(directClockStarted, now).TotalSeconds);
            directSettleTimer!.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
            return;
        }
        Compositor compositor = motion.Compositor;
        double clockSeconds = Math.Max(2, 20 / omega);
        for (int index = clocks.Count - 2; index >= 0; index--)
        {
            if (Stopwatch.GetElapsedTime(clocks[index].Started).TotalSeconds > clockSeconds + 1)
            {
                clocks[index].Clock.Dispose();
                clocks.RemoveAt(index);
            }
        }
        CompositionPropertySet clock = compositor.CreatePropertySet();
        clocks.Add((clock, Stopwatch.GetTimestamp()));
        clock.InsertScalar("Seconds", 0);
        using ScalarKeyFrameAnimation elapsed = compositor.CreateScalarKeyFrameAnimation();
        elapsed.Duration = TimeSpan.FromSeconds(clockSeconds);
        using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
        elapsed.InsertKeyFrame(0, 0);
        elapsed.InsertKeyFrame(1, (float)clockSeconds, linear);
        clock.StartAnimation("Seconds", elapsed);
        string horizontalError = "(this.StartingValue.X - destination.X)";
        string verticalError = "(this.StartingValue.Y - destination.Y)";
        string horizontalImpulse = $"(this.StartingValue.Z + omega * {horizontalError})";
        string verticalImpulse = $"(this.StartingValue.W + omega * {verticalError})";
        string decay = "Pow(2.718281828, -omega * clock.Seconds)";
        using ExpressionAnimation spring = compositor.CreateExpressionAnimation(
            $"Vector4(destination.X + ({horizontalError} + {horizontalImpulse} * clock.Seconds) * {decay}, " +
            $"destination.Y + ({verticalError} + {verticalImpulse} * clock.Seconds) * {decay}, " +
            $"(this.StartingValue.Z - omega * {horizontalImpulse} * clock.Seconds) * {decay}, " +
            $"(this.StartingValue.W - omega * {verticalImpulse} * clock.Seconds) * {decay})");
        spring.SetReferenceParameter("clock", clock);
        spring.SetVector3Parameter("destination", target);
        spring.SetScalarParameter("omega", omega);
        motion.StartAnimation("State", spring);
    }

    public void Tick()
    {
        if (!disposed && releaseRequest == 0)
        {
            long now = Stopwatch.GetTimestamp();
            UpdateDragRotation(direct ? CurrentDirectVelocity(now) : CurrentVelocity());
        }
    }

    public void Release()
    {
        if (!disposed && releaseRequest == 0)
        {
            directSettleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            releaseRequest = 1;
            Compositor compositor = motion.Compositor;
            CompositionPropertySet clock = compositor.CreatePropertySet();
            clocks.Add((clock, Stopwatch.GetTimestamp()));
            clock.InsertScalar("Seconds", 0);
            using ScalarKeyFrameAnimation elapsed = compositor.CreateScalarKeyFrameAnimation();
            float duration = (maximumSpeed + MathF.Sqrt(maximumSpeed * maximumSpeed + 2 * gravity * bounds.Y)) / gravity + 1;
            elapsed.Duration = TimeSpan.FromSeconds(duration);
            using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
            elapsed.InsertKeyFrame(0, 0);
            elapsed.InsertKeyFrame(1, duration, linear);
            clock.StartAnimation("Seconds", elapsed);
            motion.InsertScalar("Width", Math.Max(1, bounds.X));
            motion.InsertScalar("Floor", bounds.Y);
            motion.InsertScalar("Gravity", gravity);
            motion.InsertScalar("Restitution", restitution);
            string horizontalSpeed = direct ? "releaseVelocity.X" : "this.StartingValue.Z";
            string verticalSpeed = direct ? "releaseVelocity.Y" : "this.StartingValue.W";
            if (direct)
            {
                long now = Stopwatch.GetTimestamp();
                Vector3 releasePosition = Vector3.Clamp(CurrentDirectPosition(now), Vector3.Zero, bounds);
                Vector3 releaseVelocity = Vector3.Clamp(CurrentDirectVelocity(now),
                    new Vector3(-maximumSpeed), new Vector3(maximumSpeed));
                motion.InsertVector4("State",
                    new Vector4(releasePosition.X, releasePosition.Y, releaseVelocity.X, releaseVelocity.Y));
                StartFlightRotation(releasePosition, releaseVelocity, now);
            }
            else
            {
                Vector3 releaseVelocity = CurrentVelocity();
                using ExpressionAnimation capture = compositor.CreateExpressionAnimation(
                    $"Vector4(Clamp(this.StartingValue.X, 0, bounds.X), Clamp(this.StartingValue.Y, 0, bounds.Y), Clamp({horizontalSpeed}, -limit, limit), Clamp({verticalSpeed}, -limit, limit))");
                capture.SetVector3Parameter("bounds", bounds);
                capture.SetScalarParameter("limit", maximumSpeed);
                capture.SetVector3Parameter("releaseVelocity", releaseVelocity);
                motion.StartAnimation("State", capture);
                StartFlightRotation(position, releaseVelocity, Stopwatch.GetTimestamp());
            }
            StartFlightScalar("ImpactSpeed", "Sqrt(m.State.W * m.State.W + 2 * m.Gravity * (m.Floor - m.State.Y))", clock);
            StartFlightScalar("ImpactTime", "(-m.State.W + m.ImpactSpeed) / m.Gravity", clock);
            StartFlightScalar("Time", "Min(clock.Seconds, m.ImpactTime)", clock);
            StartFlightScalar("Speed", "Abs(m.State.Z)", clock);
            StartFlightScalar("WallTime", "(m.State.Z >= 0 ? m.Width - m.State.X : m.State.X) / Max(m.Speed, 0.001)", clock);
            StartFlightScalar("AfterWall", "Max(0, m.Time - m.WallTime)", clock);
            StartFlightScalar("Leg", "m.Restitution > 0 && m.Restitution < 1 ? Floor(Ln(1 + m.AfterWall * m.Speed * (1 - m.Restitution) / m.Width) / Ln(1 / m.Restitution)) : 0", clock);
            StartFlightScalar("LegStart", "m.Restitution > 0 && m.Restitution < 1 ? m.Width / Max(m.Speed, 0.001) * (Pow(m.Restitution, -m.Leg) - 1) / (1 - m.Restitution) : 0", clock);
            StartFlightScalar("Travel", "(m.AfterWall - m.LegStart) * m.Speed * Pow(m.Restitution, m.Leg + 1)", clock);
            StartFlightScalar("Horizontal", "m.Time <= m.WallTime ? m.State.X + m.State.Z * m.Time : (m.Restitution == 1 ? m.Width - Abs(Mod(Mod(m.State.X + m.State.Z * m.Time, 2 * m.Width) + 2 * m.Width, 2 * m.Width) - m.Width) : ((m.State.Z >= 0) == (Mod(m.Leg, 2) == 0) ? m.Width - m.Travel : m.Travel))", clock);
            using ExpressionAnimation tracking = compositor.CreateExpressionAnimation(
                "Vector3(m.Horizontal, Min(m.Floor, m.State.Y + m.State.W * m.Time + 0.5 * m.Gravity * m.Time * m.Time), clock.Seconds >= m.ImpactTime ? 1 + m.ImpactSpeed : 0)");
            tracking.SetReferenceParameter("m", motion);
            tracking.SetReferenceParameter("clock", clock);
            tracker.MaxPosition = new Vector3(bounds.X, bounds.Y, maximumSpeed + gravity * duration + 1);
            releaseRequest = tracker.TryUpdatePositionWithAnimation(tracking);
        }
    }

    private void StartFlightScalar(string name, string expression, CompositionPropertySet clock)
    {
        motion.InsertScalar(name, 0);
        using ExpressionAnimation animation = motion.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("m", motion);
        animation.SetReferenceParameter("clock", clock);
        motion.StartAnimation(name, animation);
    }

    private Vector3 CurrentVelocity() =>
        Stopwatch.GetElapsedTime(sampleTimestamp).TotalMilliseconds > 100 ? Vector3.Zero : velocity;

    private Vector3 CurrentDirectPosition(long timestamp)
    {
        float age = (float)Math.Max(0, Stopwatch.GetElapsedTime(directSampleTimestamp, timestamp).TotalSeconds);
        float predictionAge = Math.Min(age, DirectPredictionSeconds);
        float correctionDecay = age < DirectCorrectionSeconds
            ? MathF.Exp(-DirectCorrectionRate * age)
            : 0;
        return directTarget + directVelocity * predictionAge + directCorrection * correctionDecay;
    }

    private Vector3 CurrentDirectVelocity(long timestamp)
    {
        float age = (float)Math.Max(0, Stopwatch.GetElapsedTime(directSampleTimestamp, timestamp).TotalSeconds);
        return age <= 0.1f ? directVelocity : Vector3.Zero;
    }

    private void UpdateDragRotation(Vector3 currentVelocity)
    {
        if (!rotate)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        float seconds = Math.Min(0.05f, (float)Stopwatch.GetElapsedTime(rotationTimestamp, now).TotalSeconds);
        rotationTimestamp = now;
        float width = Math.Max(1, visual.Size.X);
        float anchorBias = (visualCenter.X - grabPoint.X) / width * 36;
        float velocityLean = -currentVelocity.X / Math.Max(1, maximumSpeed) * 48;
        float target = Math.Clamp(anchorBias + velocityLean, -MaximumDragAngle, MaximumDragAngle);
        angularVelocity += (RotationSpringStrength * (target - rotationAngle) -
            RotationDamping * angularVelocity) * seconds;
        rotationAngle += angularVelocity * seconds;
        visual.RotationAngleInDegrees = rotationAngle;
    }

    private void StartFlightRotation(Vector3 releasePosition, Vector3 releaseVelocity, long timestamp)
    {
        if (!rotate)
        {
            return;
        }
        UpdateDragRotation(releaseVelocity);
        Vector2 lever = grabPoint - visualCenter;
        float radiusSquared = Math.Max(visual.Size.LengthSquared() * 0.015f, lever.LengthSquared());
        float inducedSpin = (lever.X * releaseVelocity.Y - lever.Y * releaseVelocity.X) /
            radiusSquared * 180 / MathF.PI;
        float speed = new Vector2(releaseVelocity.X, releaseVelocity.Y).Length();
        float direction = MathF.Sign(inducedSpin);
        if (direction == 0)
        {
            direction = MathF.Sign(releaseVelocity.X);
        }
        if (direction == 0)
        {
            direction = grabPoint.X < visualCenter.X ? 1 : -1;
        }
        float verticalDistance = Math.Max(0, bounds.Y - releasePosition.Y);
        impactSeconds = (-releaseVelocity.Y +
            MathF.Sqrt(releaseVelocity.Y * releaseVelocity.Y + 2 * gravity * verticalDistance)) / gravity;
        float minimumSpin = speed < maximumSpeed * 0.08f || impactSeconds < 0.12f
            ? 0
            : Math.Min(1080, 360 / impactSeconds);
        releaseSpin = Math.Clamp(angularVelocity + inducedSpin, -1080, 1080);
        if (Math.Abs(releaseSpin) < minimumSpin)
        {
            releaseSpin = direction * minimumSpin;
        }
        releaseAngle = rotationAngle;
        releaseTimestamp = timestamp;
    }

    private void UpdateFlightRotation(bool landed)
    {
        if (!rotate)
        {
            return;
        }
        float seconds = Math.Min(impactSeconds,
            (float)Stopwatch.GetElapsedTime(releaseTimestamp).TotalSeconds);
        rotationAngle = releaseAngle + releaseSpin * seconds;
        visual.RotationAngleInDegrees = rotationAngle;
        landingRotationPending = landed;
    }

    private void StartUprightAnimation()
    {
        if (!rotate)
        {
            return;
        }
        float settledAngle = MathF.IEEERemainder(rotationAngle, 360);
        visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        visual.RotationAngleInDegrees = settledAngle;
        Compositor compositor = visual.Compositor;
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        using ScalarKeyFrameAnimation upright = compositor.CreateScalarKeyFrameAnimation();
        using CubicBezierEasingFunction settle = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.75f), new Vector2(0.25f, 1));
        upright.Duration = TimeSpan.FromMilliseconds(420);
        upright.InsertKeyFrame(0, settledAngle);
        upright.InsertKeyFrame(0.72f, -settledAngle * 0.08f, settle);
        upright.InsertKeyFrame(1, 0, settle);
        batch.Completed += (_, _) =>
        {
            try
            {
                if (visual.Properties.TryGetScalar("DragRotationGeneration", out float generation) ==
                        CompositionGetValueStatus.Succeeded &&
                    generation == rotationGeneration)
                {
                    visual.RotationAngleInDegrees = 0;
                    visual.CenterPoint = originalCenterPoint;
                }
            }
            catch (ObjectDisposedException)
            {
                // A present can be consumed while its landing animation is finishing.
            }
            batch.Dispose();
        };
        visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), upright);
        batch.End();
        landingRotationPending = false;
    }

    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        Vector3 updatedPosition = args.Position;
        int requestId = args.RequestId;
        long timestamp = Stopwatch.GetTimestamp();
        dispatch(() =>
        {
            if (disposed)
            {
                return;
            }
            if (direct && directSettled && releaseRequest == 0)
            {
                return;
            }
            if (releaseRequest != 0)
            {
                if (requestId < releaseRequest)
                {
                    return;
                }
                bool landed = updatedPosition.Z >= 1;
                UpdateFlightRotation(landed);
                report(new Vector3(updatedPosition.X, updatedPosition.Y, 0),
                    landed ? new Vector3(0, updatedPosition.Z - 1, 0) : Vector3.Zero, landed);
                if (landed)
                {
                    StartUprightAnimation();
                }
                return;
            }
            double seconds = Stopwatch.GetElapsedTime(sampleTimestamp, timestamp).TotalSeconds;
            if (seconds >= 0.025)
            {
                velocity = Vector3.Clamp((updatedPosition - position) / (float)seconds,
                    new Vector3(-maximumSpeed), new Vector3(maximumSpeed));
                sampleTimestamp = timestamp;
                position = updatedPosition;
            }
            UpdateDragRotation(velocity);
            report(updatedPosition, velocity, false);
        });
    }

    public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
    {
    }

    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) =>
        dispatch(() =>
        {
            if (!disposed)
            {
                throw new InvalidOperationException($"Native drag request {args.RequestId} was ignored.");
            }
        });

    public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }
    public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }
    public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args) { }

    public void Dispose()
    {
        disposed = true;
        if (rotate && !landingRotationPending)
        {
            visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            visual.RotationAngleInDegrees = 0;
            visual.CenterPoint = originalCenterPoint;
        }
        tracker.Dispose();
        foreach ((CompositionPropertySet clock, _) in clocks)
        {
            clock.Dispose();
        }
        directClock?.Dispose();
        directSettleTimer?.Dispose();
        motion.Dispose();
    }
}