using System.Diagnostics;
using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Composition.Interactions;

namespace TaskbarBuddy.Composition;

internal sealed class NativeDragMotion : IInteractionTrackerOwner, IDisposable
{
    private readonly InteractionTracker tracker;
    private readonly CompositionPropertySet motion;
    private readonly List<(CompositionPropertySet Clock, long Started)> clocks = [];
    private readonly Action<Action> dispatch;
    private readonly float omega;
    private readonly Vector3 bounds;
    private readonly float gravity;
    private readonly float restitution;
    private readonly Action<Vector3, Vector3, bool> report;
    private readonly bool direct;
    private readonly float maximumSpeed;
    private Vector3 position;
    private Vector3 velocity;
    private long sampleTimestamp;
    private int releaseRequest;
    private bool disposed;

    public int MovingRetargets { get; private set; }

    public NativeDragMotion(Compositor compositor, Visual visual, Vector3 initialPosition,
        Vector3 maximumPosition, double responseSeconds, float maximumSpeed, float gravity, float restitution, bool direct,
        Action<Action> dispatch, Action<Vector3, Vector3, bool> report)
    {
        this.dispatch = dispatch;
        this.report = report;
        this.direct = direct;
        this.maximumSpeed = maximumSpeed;
        bounds = maximumPosition;
        this.gravity = gravity;
        this.restitution = restitution;
        position = initialPosition;
        omega = (float)(2 / responseSeconds);
        sampleTimestamp = Stopwatch.GetTimestamp();
        motion = compositor.CreatePropertySet();
        motion.InsertVector4("State", new Vector4(initialPosition.X, initialPosition.Y, 0, 0));
        tracker = InteractionTracker.CreateWithOwner(compositor, this);
        tracker.MinPosition = Vector3.Zero;
        tracker.MaxPosition = maximumPosition;
        tracker.TryUpdatePosition(initialPosition);
        using ExpressionAnimation tracking = compositor.CreateExpressionAnimation(
            "Clamp(Vector3(motion.State.X, motion.State.Y, 0), Vector3(0, 0, 0), bounds)");
        tracking.SetReferenceParameter("motion", motion);
        tracking.SetVector3Parameter("bounds", maximumPosition);
        tracker.TryUpdatePositionWithAnimation(tracking);
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
            motion.InsertVector4("State", new Vector4(target.X, target.Y, 0, 0));
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

    public void Release()
    {
        if (!disposed && releaseRequest == 0)
        {
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
            using ExpressionAnimation capture = compositor.CreateExpressionAnimation(
                $"Vector4(Clamp(this.StartingValue.X, 0, bounds.X), Clamp(this.StartingValue.Y, 0, bounds.Y), Clamp({horizontalSpeed}, -limit, limit), Clamp({verticalSpeed}, -limit, limit))");
            capture.SetVector3Parameter("bounds", bounds);
            capture.SetScalarParameter("limit", maximumSpeed);
            capture.SetVector3Parameter("releaseVelocity", CurrentVelocity());
            motion.StartAnimation("State", capture);
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
            if (releaseRequest != 0)
            {
                if (requestId < releaseRequest)
                {
                    return;
                }
                bool landed = updatedPosition.Z >= 1;
                report(new Vector3(updatedPosition.X, updatedPosition.Y, 0),
                    landed ? new Vector3(0, updatedPosition.Z - 1, 0) : Vector3.Zero, landed);
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
        tracker.Dispose();
        foreach ((CompositionPropertySet clock, _) in clocks)
        {
            clock.Dispose();
        }
        motion.Dispose();
    }
}