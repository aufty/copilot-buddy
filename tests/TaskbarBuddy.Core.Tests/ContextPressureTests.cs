using TaskbarBuddy.Core;

namespace TaskbarBuddy.Core.Tests;

public sealed class ContextPressureTests
{
    [Theory]
    [InlineData(139999, 1000000, false)]
    [InlineData(140000, 1000000, true)]
    [InlineData(80000, 200000, false)]
    [InlineData(80001, 200000, true)]
    [InlineData(140000, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 100, false)]
    [InlineData(double.NaN, 100, false)]
    [InlineData(double.PositiveInfinity, 100, false)]
    public void EitherThresholdCanTriggerHeavyContext(double tokens, double limit, bool expected) =>
        Assert.Equal(expected, ContextPressure.IsContextHeavy(tokens, limit));

    [Fact]
    public void HeavyStateNeedsAnExplicitFirstClickBeforeShowingTheWarning()
    {
        ContextPressure pressure = new();
        Assert.False(pressure.Reveal());
        Assert.Equal(1, pressure.WalkSpeedMultiplier);
        pressure.Update(new("first", "Refactor", 140000, 1000000));
        Assert.True(pressure.IsHeavy);
        Assert.Equal(0.5, pressure.WalkSpeedMultiplier);
        Assert.Null(pressure.Presented);
        Assert.Null(pressure.Message);
        Assert.True(pressure.Reveal());
        Assert.Equal("first", pressure.Presented!.SessionId);
        Assert.Equal("Refactor session is getting heavy on context. I recommend compacting or handing off to a new session", pressure.Message);
        pressure.Acknowledge("first");
        Assert.Null(pressure.Presented);
        Assert.True(pressure.IsHeavy);
    }

    [Fact]
    public void SelectedSessionStaysStableAcrossUsageAndTitleUpdates()
    {
        ContextPressure pressure = new();
        pressure.Update(new("first", "First", 140000, 1000000));
        pressure.Reveal();
        pressure.Update(new("second", "Second", 190000, 200000));
        pressure.Update(new("first", "Renamed", 150000, 1000000));
        Assert.Equal("first", pressure.Presented!.SessionId);
        Assert.StartsWith("Renamed session", pressure.Message);
        pressure.Acknowledge("second");
        Assert.Equal("first", pressure.Presented.SessionId);
    }

    [Fact]
    public void CompactionAndClosingClearWarningsWithoutLosingOtherHeavySessions()
    {
        ContextPressure pressure = new();
        pressure.Update(new("first", "First", 140000, 1000000));
        pressure.Update(new("second", "Second", 90000, 200000));
        pressure.Reveal();
        pressure.Update(new("first", "First", 10000, 200000));
        Assert.Null(pressure.Presented);
        Assert.True(pressure.IsHeavy);
        pressure.Reveal();
        Assert.Equal("second", pressure.Presented!.SessionId);
        pressure.Remove("second");
        Assert.False(pressure.IsHeavy);
        Assert.Null(pressure.Message);
        Assert.Equal(1, pressure.WalkSpeedMultiplier);
    }
}