namespace CopilotBuddy.Core;

public enum BuddyNeed
{
    Food,
    Water,
    Play
}

public sealed class BuddyNeedsOptions
{
    public double RequestThreshold { get; init; } = 0.35;
    public double NeglectThreshold { get; init; } = 0.1;
    public double FoodSecondsFullToEmpty { get; init; } = 30 * 60;
    public double WaterSecondsFullToEmpty { get; init; } = 24 * 60;
    public double PlaySecondsFullToEmpty { get; init; } = 36 * 60;
    public double RateVariation { get; init; } = 0.18;
    public double RelationshipGain { get; init; } = 0.035;
    public double RelationshipNeglectPerSecond { get; init; } = 0.00025;

    internal void Validate()
    {
        if (RequestThreshold <= 0 || RequestThreshold >= 1 ||
            NeglectThreshold < 0 || NeglectThreshold >= RequestThreshold ||
            FoodSecondsFullToEmpty <= 0 || WaterSecondsFullToEmpty <= 0 ||
            PlaySecondsFullToEmpty <= 0 || RateVariation < 0 || RateVariation >= 1 ||
            RelationshipGain < 0 || RelationshipNeglectPerSecond < 0)
        {
            throw new InvalidOperationException("Buddy needs settings contain an invalid threshold, duration, or rate.");
        }
    }
}

public sealed class BuddyNeedsController
{
    private readonly BuddyNeedsOptions options;
    private readonly IRandomSource random;
    private readonly Dictionary<BuddyNeed, NeedState> needs = [];

    public BuddyNeedsController(BuddyNeedsOptions options, IRandomSource random)
    {
        options.Validate();
        this.options = options;
        this.random = random;
        foreach (BuddyNeed need in Enum.GetValues<BuddyNeed>())
        {
            needs[need] = new NeedState(
                0.72 + random.NextDouble() * 0.28,
                CreateDepletionRate(need));
        }
    }

    public BuddyNeed? CurrentRequest { get; private set; }
    public double Relationship { get; private set; } = 0.5;
    public double Level(BuddyNeed need) => needs[need].Level;

    public void Tick(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        double seconds = elapsed.TotalSeconds;
        foreach (NeedState state in needs.Values)
        {
            double previousLevel = state.Level;
            state.Level = Math.Max(0, previousLevel - state.DepletionPerSecond * seconds);
            double neglectedSeconds = previousLevel <= options.NeglectThreshold
                ? seconds
                : state.Level < options.NeglectThreshold
                    ? seconds - (previousLevel - options.NeglectThreshold) / state.DepletionPerSecond
                    : 0;
            if (neglectedSeconds > 0)
            {
                Relationship = Math.Max(0,
                    Relationship - options.RelationshipNeglectPerSecond * neglectedSeconds);
            }
        }

        CurrentRequest ??= needs
            .Where(pair => pair.Value.Level <= options.RequestThreshold)
            .OrderBy(pair => pair.Value.Level)
            .Select(pair => (BuddyNeed?)pair.Key)
            .FirstOrDefault();
    }

    public void Fulfill(BuddyNeed need)
    {
        NeedState state = needs[need];
        bool requested = CurrentRequest == need;
        bool neglected = state.Level <= options.NeglectThreshold;
        state.Level = 1;
        state.DepletionPerSecond = CreateDepletionRate(need);
        Relationship = Math.Min(1, Relationship + (requested
            ? options.RelationshipGain * (neglected ? 0.45 : 1)
            : options.RelationshipGain * 0.2));
        if (requested)
        {
            CurrentRequest = null;
        }
    }

    public void SatisfyAll()
    {
        foreach (NeedState state in needs.Values)
        {
            state.Level = 1;
        }
        CurrentRequest = null;
    }

    private double CreateDepletionRate(BuddyNeed need)
    {
        double seconds = need switch
        {
            BuddyNeed.Food => options.FoodSecondsFullToEmpty,
            BuddyNeed.Water => options.WaterSecondsFullToEmpty,
            BuddyNeed.Play => options.PlaySecondsFullToEmpty,
            _ => throw new ArgumentOutOfRangeException(nameof(need))
        };
        double multiplier = 1 - options.RateVariation + random.NextDouble() * options.RateVariation * 2;
        return multiplier / seconds;
    }

    private sealed class NeedState(double level, double depletionPerSecond)
    {
        public double Level { get; set; } = level;
        public double DepletionPerSecond { get; set; } = depletionPerSecond;
    }
}
