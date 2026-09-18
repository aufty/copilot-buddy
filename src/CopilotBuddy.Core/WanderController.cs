namespace CopilotBuddy.Core;

public interface IRandomSource
{
    double NextDouble();
}

public sealed class SystemRandomSource(int? seed = null) : IRandomSource
{
    private readonly Random random = seed is null ? Random.Shared : new Random(seed.Value);
    public double NextDouble() => random.NextDouble();
}

internal sealed class WanderController
{
    private const int WaypointAttempts = 8;
    private readonly WanderOptions options;
    private readonly IRandomSource random;
    private double minimumX;
    private double maximumX;
    private double targetX;
    private double idleRemaining;

    public WanderController(WanderOptions options, IRandomSource random, double minimumX, double maximumX)
    {
        this.options = options;
        this.random = random;
        SetBounds(minimumX, maximumX);
        X = minimumX + (maximumX - minimumX) / 2;
        targetX = X;
        StartNewIdle();
    }

    public VisualState State { get; private set; } = VisualState.Idle;
    public HorizontalDirection Direction { get; private set; } = HorizontalDirection.Right;
    public double X { get; private set; }
    public double MovementElapsed { get; private set; }
    public double SpeedMultiplier { get; set; } = 1;
    internal double Destination => targetX;
    internal double SecondsUntilTransition => State == VisualState.Idle
        ? Math.Max(0, idleRemaining)
        : Math.Abs(targetX - X) / (options.Speed * SpeedMultiplier);

    public void SetBounds(double minimum, double maximum)
    {
        if (maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "Maximum bound must be at least the minimum bound.");
        }
        minimumX = minimum;
        maximumX = maximum;
        X = Math.Clamp(X, minimumX, maximumX);
        targetX = Math.Clamp(targetX, minimumX, maximumX);
    }

    public void StartNewIdle()
    {
        State = VisualState.Idle;
        MovementElapsed = 0;
        idleRemaining = Lerp(options.IdleMinimumSeconds, options.IdleMaximumSeconds, random.NextDouble());
    }

    public void PlaceAt(double x)
    {
        X = Math.Clamp(x, minimumX, maximumX);
        targetX = X;
    }

    public void Tick(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return;
        }

        if (State == VisualState.Idle)
        {
            idleRemaining -= elapsedSeconds;
            if (idleRemaining <= 0)
            {
                ChooseWaypoint();
            }
            return;
        }

        MovementElapsed += elapsedSeconds * SpeedMultiplier;
        double distance = options.Speed * SpeedMultiplier * elapsedSeconds;
        if (Math.Abs(targetX - X) <= distance)
        {
            X = targetX;
            StartNewIdle();
            return;
        }

        X += Direction == HorizontalDirection.Right ? distance : -distance;
        X = Math.Clamp(X, minimumX, maximumX);
    }

    private void ChooseWaypoint()
    {
        double available = maximumX - minimumX;
        if (available <= 0)
        {
            StartNewIdle();
            return;
        }

        double candidate = X;
        for (int attempt = 0; attempt < WaypointAttempts; attempt++)
        {
            candidate = Lerp(minimumX, maximumX, random.NextDouble());
            if (Math.Abs(candidate - X) >= Math.Min(options.PreferredMinimumMove, available))
            {
                break;
            }
        }

        if (Math.Abs(candidate - X) < Math.Min(options.PreferredMinimumMove, available))
        {
            candidate = X - minimumX >= maximumX - X ? minimumX : maximumX;
        }

        targetX = candidate;
        Direction = targetX < X ? HorizontalDirection.Left : HorizontalDirection.Right;
        State = VisualState.Walking;
        MovementElapsed = 0;
    }

    private static double Lerp(double minimum, double maximum, double amount) => minimum + (maximum - minimum) * amount;
}