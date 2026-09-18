namespace CopilotBuddy.Core;

public sealed class PresentationOptions
{
    public SpriteOptions Sprite { get; init; } = new();
    public WanderOptions Wander { get; init; } = new();
    public BreathingOptions Breathing { get; init; } = new();
    public PhysicsOptions Physics { get; init; } = new();
    public AttentionOptions Attention { get; init; } = new();
    public BubbleOptions Bubble { get; init; } = new();
    public string PipeName { get; init; } = "CopilotBuddy.Presentation.v1";

    public void Validate()
    {
        Sprite.Validate();
        Wander.Validate();
        Breathing.Validate();
        Physics.Validate();
        Attention.Validate();
        Bubble.Validate();
        if (string.IsNullOrWhiteSpace(PipeName))
        {
            throw new InvalidOperationException("PipeName must not be empty.");
        }
    }
}

public sealed class SpriteOptions
{
    public string Path { get; init; } = "Assets/Sprites/Buddies/sprout.png";
    public int Scale { get; init; } = 2;
    public Dictionary<SpriteFrame, FrameRectangle> Frames { get; init; } = [];

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new InvalidOperationException("Sprite.Path must not be empty.");
        }
        if (Scale < 1 || Scale != Math.Truncate((double)Scale))
        {
            throw new InvalidOperationException("Sprite.Scale must be a positive integer.");
        }

        foreach (SpriteFrame frame in Enum.GetValues<SpriteFrame>())
        {
            if (!Frames.TryGetValue(frame, out FrameRectangle? rectangle))
            {
                throw new InvalidOperationException($"Sprite frame '{frame}' is missing.");
            }
            rectangle.Validate(frame);
        }
    }
}

public sealed class BreathingOptions
{
    public double DurationSeconds { get; init; } = 2.2;
    public double Amount { get; init; } = 0.035;

    internal void Validate()
    {
        if (DurationSeconds <= 0 || Amount < 0 || Amount > 0.15)
        {
            throw new InvalidOperationException("Breathing requires a positive duration and an amount from 0 through 0.15.");
        }
    }
}

public sealed class PhysicsOptions
{
    public double DragResponseSeconds { get; init; } = 0.12;
    public double Gravity { get; init; } = 1_600;
    public double MaximumReleaseSpeed { get; init; } = 1_100;
    public double WallRestitution { get; init; } = 0.4;
    public double LandingDurationSeconds { get; init; } = 0.38;
    public double MaximumLandingSquish { get; init; } = 0.22;

    internal void Validate()
    {
        if (DragResponseSeconds <= 0 || Gravity <= 0 || MaximumReleaseSpeed <= 0 ||
            WallRestitution < 0 || WallRestitution > 1 || LandingDurationSeconds <= 0 ||
            MaximumLandingSquish < 0 || MaximumLandingSquish > 0.4)
        {
            throw new InvalidOperationException("Physics settings contain an invalid force, duration, or normalized amount.");
        }
    }
}

public sealed class FrameRectangle
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    internal void Validate(SpriteFrame frame)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0)
        {
            throw new InvalidOperationException($"Sprite frame '{frame}' has an invalid rectangle.");
        }
    }
}

public sealed class WanderOptions
{
    public double Speed { get; init; } = 80;
    public double IdleMinimumSeconds { get; init; } = 1.5;
    public double IdleMaximumSeconds { get; init; } = 4;
    public double PreferredMinimumMove { get; init; } = 96;
    public double WalkFrameSeconds { get; init; } = 0.2;

    internal void Validate()
    {
        if (Speed <= 0 || IdleMinimumSeconds < 0 || IdleMaximumSeconds < IdleMinimumSeconds ||
            PreferredMinimumMove < 0 || WalkFrameSeconds <= 0)
        {
            throw new InvalidOperationException("Wander settings contain an invalid range or non-positive timing.");
        }
    }
}

public sealed class AttentionOptions
{
    public double HopHeight { get; init; } = 10;
    public double HopDurationSeconds { get; init; } = 0.7;
    public double WaveFrameSeconds { get; init; } = 0.25;
    public bool ReducedMotion { get; init; }
    public double ReducedMotionWaveFrameSeconds { get; init; } = 0.5;

    internal void Validate()
    {
        if (HopHeight < 0 || HopDurationSeconds <= 0 || WaveFrameSeconds <= 0 || ReducedMotionWaveFrameSeconds <= 0)
        {
            throw new InvalidOperationException("Attention settings contain a negative distance or non-positive timing.");
        }
    }
}

public sealed class BubbleOptions
{
    public double MinimumWidth { get; init; } = 160;
    public double MaximumWidth { get; init; } = 360;
    public double MaximumHeight { get; init; } = 220;
    public double Padding { get; init; } = 14;

    internal void Validate()
    {
        if (MinimumWidth <= 0 || MaximumWidth < MinimumWidth || MaximumHeight <= 0 || Padding < 0)
        {
            throw new InvalidOperationException("Bubble settings contain an invalid size range.");
        }
    }
}