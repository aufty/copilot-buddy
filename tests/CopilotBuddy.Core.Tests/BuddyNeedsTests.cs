using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class BuddyNeedsTests
{
    [Fact]
    public void NeedsDepleteAtDifferentRandomizedRatesAndRequestTheLowest()
    {
        BuddyNeedsController needs = CreateNeeds(new SequenceRandomSource(0, 0.5, 1, 0, 0.5, 1));

        needs.Tick(TimeSpan.FromSeconds(6));

        BuddyNeed lowest = Enum.GetValues<BuddyNeed>().MinBy(needs.Level);
        Assert.Equal(lowest, needs.CurrentRequest);
        Assert.NotEqual(needs.Level(BuddyNeed.Food), needs.Level(BuddyNeed.Water));
        Assert.NotEqual(needs.Level(BuddyNeed.Water), needs.Level(BuddyNeed.Play));
    }

    [Fact]
    public void FulfillingRequestedNeedRestoresItAndImprovesRelationship()
    {
        BuddyNeedsController needs = CreateNeeds(new SequenceRandomSource(0, 0, 0, 0, 0, 0));
        needs.Tick(TimeSpan.FromSeconds(6));
        BuddyNeed requested = needs.CurrentRequest!.Value;
        double relationship = needs.Relationship;

        needs.Fulfill(requested);

        Assert.Equal(1, needs.Level(requested));
        Assert.Null(needs.CurrentRequest);
        Assert.True(needs.Relationship > relationship);
    }

    [Fact]
    public void ProlongedNeglectLowersRelationship()
    {
        BuddyNeedsController needs = CreateNeeds(new SequenceRandomSource(0, 0, 0, 0, 0, 0));
        needs.Tick(TimeSpan.FromSeconds(8));
        double relationship = needs.Relationship;

        needs.Tick(TimeSpan.FromSeconds(8));

        Assert.True(needs.Relationship < relationship);
    }

    [Theory]
    [InlineData(BuddyNeed.Food)]
    [InlineData(BuddyNeed.Water)]
    [InlineData(BuddyNeed.Play)]
    public void ASpecificNeedCanBeRequested(BuddyNeed need)
    {
        BuddyNeedsController needs = CreateNeeds(new SequenceRandomSource(0, 0, 0));

        needs.Request(need);

        Assert.Equal(need, needs.CurrentRequest);
        Assert.Equal(0.5, needs.Level(need));
    }

    [Fact]
    public void SatisfyingAllRestoresEveryNeedAndClearsTheRequest()
    {
        BuddyNeedsController needs = CreateNeeds(new SequenceRandomSource(0, 0, 0, 0, 0, 0));
        needs.Tick(TimeSpan.FromSeconds(8));

        needs.SatisfyAll();

        Assert.All(Enum.GetValues<BuddyNeed>(), need => Assert.Equal(1, needs.Level(need)));
        Assert.Null(needs.CurrentRequest);
    }

    private static BuddyNeedsController CreateNeeds(IRandomSource random) => new(
        new BuddyNeedsOptions
        {
            RequestThreshold = 0.5,
            NeglectThreshold = 0.2,
            FoodSecondsFullToEmpty = 10,
            WaterSecondsFullToEmpty = 8,
            PlaySecondsFullToEmpty = 12,
            RateVariation = 0.1,
            RelationshipNeglectPerSecond = 0.01
        },
        random);

    private sealed class SequenceRandomSource(params double[] values) : IRandomSource
    {
        private int index;
        public double NextDouble() => values[index++ % values.Length];
    }
}
