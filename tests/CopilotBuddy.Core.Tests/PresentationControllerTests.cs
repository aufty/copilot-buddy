using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class PresentationControllerTests
{
    [Fact]
    public void WalkingSpeedMultiplierKeepsMovementAndPlanDurationInSync()
    {
        PresentationController normal = CreateController();
        PresentationController slow = CreateController();
        slow.SetWalkingSpeedMultiplier(0.5);
        normal.Tick(TimeSpan.FromSeconds(2));
        slow.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(VisualState.Walking, slow.Snapshot.State);
        Assert.Equal(normal.PlanPassiveMotion()!.Value.Duration.TotalSeconds * 2,
            slow.PlanPassiveMotion()!.Value.Duration.TotalSeconds, 6);
        double start = normal.Snapshot.X;
        normal.Tick(TimeSpan.FromMilliseconds(100));
        slow.Tick(TimeSpan.FromMilliseconds(100));
        Assert.Equal((normal.Snapshot.X - start) / 2, slow.Snapshot.X - start, 6);
        double remaining = slow.PlanPassiveMotion()!.Value.Duration.TotalSeconds;
        slow.SetWalkingSpeedMultiplier(1);
        Assert.Equal(remaining / 2, slow.PlanPassiveMotion()!.Value.Duration.TotalSeconds, 6);
    }

    [Fact]
    public void WalkingSlowdownDoesNotSlowAttentionOrIdleBreathing()
    {
        PresentationController normal = CreateController();
        PresentationController slow = CreateController();
        slow.SetWalkingSpeedMultiplier(0.5);
        normal.Tick(TimeSpan.FromMilliseconds(400));
        slow.Tick(TimeSpan.FromMilliseconds(400));
        Assert.Equal(normal.Snapshot, slow.Snapshot);
        normal.ShowMessage("Question");
        slow.ShowMessage("Question");
        normal.Tick(TimeSpan.FromMilliseconds(300));
        slow.Tick(TimeSpan.FromMilliseconds(300));
        Assert.Equal(normal.Snapshot, slow.Snapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidWalkingSpeedIsRejected(double multiplier) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateController().SetWalkingSpeedMultiplier(multiplier));

    [Fact]
    public void StartsCenteredAndIdle()
    {
        PresentationController controller = CreateController();

        PresentationSnapshot snapshot = controller.Snapshot;

        Assert.Equal(VisualState.Idle, snapshot.State);
        Assert.Equal(50, snapshot.X);
        Assert.Equal(SpriteFrame.Standing, snapshot.Frame);
    }

    [Fact]
    public void ShowInterruptsWanderingAndDismissStartsIdle()
    {
        PresentationController controller = CreateController();
        controller.Tick(TimeSpan.FromSeconds(2));

        controller.ShowMessage("Build needs approval");

        Assert.Equal(VisualState.Attention, controller.Snapshot.State);
        Assert.Equal("Build needs approval", controller.Snapshot.Message);

        controller.DismissMessage();

        Assert.Equal(VisualState.Idle, controller.Snapshot.State);
        Assert.Null(controller.Snapshot.Message);
    }

    [Fact]
    public void HiddenControllerDoesNotAdvance()
    {
        PresentationController controller = CreateController();
        controller.ShowMessage("Paused");
        controller.SetVisible(false);

        controller.Tick(TimeSpan.FromMilliseconds(350));

        Assert.Equal(SpriteFrame.Wave1, controller.Snapshot.Frame);
        Assert.Equal(0, controller.Snapshot.HopOffset);
    }

    [Fact]
    public void RepeatedShowRestartsAttentionAtBaseline()
    {
        PresentationController controller = CreateController();
        controller.ShowMessage("First");
        controller.Tick(TimeSpan.FromMilliseconds(300));

        controller.ShowMessage("Second");

        Assert.Equal("Second", controller.Snapshot.Message);
        Assert.Equal(SpriteFrame.Wave1, controller.Snapshot.Frame);
        Assert.Equal(0, controller.Snapshot.HopOffset);
    }

    [Fact]
    public void ReducedMotionDisablesHopAndSlowsWave()
    {
        PresentationOptions options = ValidOptions(reducedMotion: true);
        PresentationController controller = new(options, new SequenceRandom(0), 0, 100);
        controller.ShowMessage("Accessible");

        controller.Tick(TimeSpan.FromMilliseconds(300));

        Assert.Equal(0, controller.Snapshot.HopOffset);
        Assert.Equal(SpriteFrame.Wave1, controller.Snapshot.Frame);
    }

    [Fact]
    public void IdleBreathingSquashesWidthAndStretchesHeight()
    {
        PresentationController controller = CreateController();

        controller.Tick(TimeSpan.FromSeconds(0.5));

        Assert.True(controller.Snapshot.ScaleX < 1);
        Assert.True(controller.Snapshot.ScaleY > 1);
    }

    [Fact]
    public void ReducedMotionDisablesIdleBreathing()
    {
        PresentationOptions options = ValidOptions(reducedMotion: true);
        PresentationController controller = new(options, new SequenceRandom(0), 0, 100);

        controller.Tick(TimeSpan.FromSeconds(0.5));

        Assert.Equal(1, controller.Snapshot.ScaleX);
        Assert.Equal(1, controller.Snapshot.ScaleY);
    }

    [Fact]
    public void WanderStaysInsideUpdatedBounds()
    {
        PresentationController controller = CreateController();
        controller.SetHorizontalBounds(20, 30);

        for (int index = 0; index < 100; index++)
        {
            controller.Tick(TimeSpan.FromMilliseconds(100));
            Assert.InRange(controller.Snapshot.X, 20, 30);
        }
    }

    [Fact]
    public void ResetWanderingStopsAtCurrentPositionAndStartsIdle()
    {
        PresentationController controller = CreateController();
        controller.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(VisualState.Walking, controller.Snapshot.State);
        double position = controller.Snapshot.X;

        controller.ResetWandering();

        Assert.Equal(VisualState.Idle, controller.Snapshot.State);
        Assert.Equal(SpriteFrame.Standing, controller.Snapshot.Frame);
        Assert.Equal(position, controller.Snapshot.X);
        Assert.Equal(position, controller.PlanPassiveMotion()!.Value.End.X);
    }

    [Fact]
    public void DragUsesSpringLagInsteadOfSnappingToPointer()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 20);
        controller.UpdateDrag(90, 80);

        controller.Tick(TimeSpan.FromMilliseconds(16));

        Assert.Equal(VisualState.Dragging, controller.Snapshot.State);
        Assert.InRange(controller.Snapshot.X, 50.01, 89.99);
        Assert.InRange(controller.Snapshot.HopOffset, 0.01, 59.99);
    }

    [Fact]
    public void RapidDragReversalSettlesWithoutOscillatingPastTarget()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(100, 0);
        controller.Tick(TimeSpan.FromMilliseconds(150));

        controller.UpdateDrag(0, 0);
        for (int index = 0; index < 120; index++)
        {
            controller.Tick(TimeSpan.FromMilliseconds(8));
        }

        Assert.InRange(controller.Snapshot.X, 0, 0.1);
    }

    [Fact]
    public void AnalyticDragMatchesAcrossDifferentUpdateSizes()
    {
        PresentationController singleStep = CreateController();
        PresentationController multipleSteps = CreateController();
        foreach (PresentationController controller in new[] { singleStep, multipleSteps })
        {
            controller.SetMaximumLift(200);
            controller.BeginDrag(50, 0);
            controller.UpdateDrag(80, 100);
        }

        singleStep.Tick(TimeSpan.FromMilliseconds(40));
        for (int index = 0; index < 5; index++)
        {
            multipleSteps.Tick(TimeSpan.FromMilliseconds(8));
        }

        Assert.Equal(singleStep.Snapshot.X, multipleSteps.Snapshot.X, 8);
        Assert.Equal(singleStep.Snapshot.HopOffset, multipleSteps.Snapshot.HopOffset, 8);

        singleStep.EndDrag();
        multipleSteps.EndDrag();
        singleStep.Tick(TimeSpan.FromMilliseconds(8));
        multipleSteps.Tick(TimeSpan.FromMilliseconds(8));

        Assert.Equal(singleStep.Snapshot.X, multipleSteps.Snapshot.X, 8);
        Assert.Equal(singleStep.Snapshot.HopOffset, multipleSteps.Snapshot.HopOffset, 8);
    }

    [Fact]
    public void HoverStopsWalkingAndLeavingStartsFreshIdle()
    {
        PresentationController controller = CreateController();
        controller.Tick(TimeSpan.FromSeconds(1.1));
        controller.Tick(TimeSpan.FromMilliseconds(100));
        double walkingX = controller.Snapshot.X;

        controller.SetHovered(true);
        controller.Tick(TimeSpan.FromSeconds(2));

        Assert.Equal(VisualState.Idle, controller.Snapshot.State);
        Assert.Equal(SpriteFrame.Standing, controller.Snapshot.Frame);
        Assert.Equal(walkingX, controller.Snapshot.X);

        controller.SetHovered(false);
        controller.Tick(TimeSpan.FromMilliseconds(500));

        Assert.Equal(VisualState.Idle, controller.Snapshot.State);
        Assert.Equal(walkingX, controller.Snapshot.X);
    }

    [Fact]
    public void ReleasedDragFallsAndSquishesOnLanding()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 20);
        controller.UpdateDrag(80, 100);
        for (int index = 0; index < 12; index++)
        {
            controller.Tick(TimeSpan.FromMilliseconds(16));
        }
        controller.EndDrag();

        for (int index = 0; index < 500 && controller.Snapshot.State != VisualState.Landing; index++)
        {
            controller.Tick(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(VisualState.Landing, controller.Snapshot.State);
        Assert.Equal(0, controller.Snapshot.HopOffset);
        Assert.True(controller.Snapshot.ScaleX > 1);
        Assert.True(controller.Snapshot.ScaleY < 1);
    }

    [Fact]
    public void SnapshotInterpolationBlendsContinuousVisualValues()
    {
        PresentationSnapshot previous = new(true, VisualState.Airborne, 10, SpriteFrame.Standing, 20, 1, 1, null);
        PresentationSnapshot current = new(true, VisualState.Landing, 30, SpriteFrame.Wave1, 0, 1.2, 0.8, "Current");

        PresentationSnapshot result = PresentationSnapshot.Interpolate(previous, current, 0.25);

        Assert.Equal(15, result.X);
        Assert.Equal(15, result.HopOffset);
        Assert.Equal(1.05, result.ScaleX, 5);
        Assert.Equal(0.95, result.ScaleY, 5);
        Assert.Equal(VisualState.Landing, result.State);
        Assert.Equal(SpriteFrame.Wave1, result.Frame);
        Assert.Equal("Current", result.Message);
    }

    [Fact]
    public void InvalidConfigurationFailsEagerly()
    {
        PresentationOptions options = ValidOptions();
        options.Sprite.Frames.Remove(SpriteFrame.Wave2);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Wave2", exception.Message);
    }

    [Fact]
    public void PreviewMatchesDragAndReversalWithoutAdvancingController()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(85, 100);
        controller.Tick(TimeSpan.FromMilliseconds(80));
        controller.UpdateDrag(25, 40);
        PresentationSnapshot before = controller.Snapshot;
        TimeSpan step = TimeSpan.FromSeconds(1.0 / 120);

        IReadOnlyList<PresentationSnapshot> preview = controller.PreviewInteraction(step, 180);

        Assert.Equal(before, controller.Snapshot);
        Assert.Equal(before, preview[0]);
        foreach (PresentationSnapshot expected in preview.Skip(1))
        {
            controller.Tick(step);
            Assert.Equal(expected, controller.Snapshot);
        }
    }

    [Fact]
    public void PreviewIncludesFlingWallBounceAndLanding()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(100, 150);
        controller.Tick(TimeSpan.FromMilliseconds(80));
        controller.EndDrag();
        TimeSpan step = TimeSpan.FromSeconds(1.0 / 120);
        PresentationSnapshot before = controller.Snapshot;

        IReadOnlyList<PresentationSnapshot> preview = controller.PreviewInteraction(step, 600);

        Assert.Equal(before, controller.Snapshot);
        Assert.Contains(preview, snapshot => snapshot.State == VisualState.Landing && snapshot.ScaleY < 1);
        Assert.Equal(VisualState.Idle, preview[^1].State);
        Assert.Equal(0, preview[^1].HopOffset);
        Assert.Contains(preview.Zip(preview.Skip(1)), pair => pair.Second.X < pair.First.X);
        foreach (PresentationSnapshot expected in preview.Skip(1))
        {
            controller.Tick(step);
            Assert.Equal(expected.X, controller.Snapshot.X, 8);
            Assert.Equal(expected.HopOffset, controller.Snapshot.HopOffset, 8);
            Assert.Equal(expected.ScaleX, controller.Snapshot.ScaleX, 8);
            Assert.Equal(expected.ScaleY, controller.Snapshot.ScaleY, 8);
            Assert.InRange(expected.X, 0, 100);
            Assert.InRange(expected.HopOffset, 0, 200);
        }
    }

    [Fact]
    public void RegrabbingAirborneBuddyPreservesItsPosition()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(90, 100);
        controller.Tick(TimeSpan.FromMilliseconds(80));
        controller.EndDrag();
        controller.Tick(TimeSpan.FromMilliseconds(30));
        PresentationSnapshot before = controller.Snapshot;

        controller.BeginDrag(before.X + 5, before.HopOffset + 10);
        controller.Tick(TimeSpan.FromMilliseconds(16));

        Assert.Equal(before.X, controller.Snapshot.X);
        Assert.Equal(before.HopOffset, controller.Snapshot.HopOffset);
        Assert.Equal(VisualState.Dragging, controller.Snapshot.State);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(101, 10)]
    [InlineData(8, 0)]
    [InlineData(8, 1201)]
    public void PreviewRejectsInvalidSamplingLimits(int milliseconds, int steps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateController().PreviewInteraction(TimeSpan.FromMilliseconds(milliseconds), steps));
    }

    [Fact]
    public void PassivePlanMatchesIdleAndNextWalkingDestination()
    {
        PresentationController controller = CreateController();
        PassiveMotionPlan idle = controller.PlanPassiveMotion()!.Value;
        Assert.Equal(VisualState.Idle, idle.Start.State);
        Assert.Equal(idle.Start.X, idle.End.X);
        Assert.Equal(TimeSpan.FromSeconds(1), idle.Duration);

        controller.Tick(idle.Duration);
        PassiveMotionPlan walk = controller.PlanPassiveMotion()!.Value;
        Assert.Equal(VisualState.Walking, walk.Start.State);
        Assert.NotEqual(walk.Start.X, walk.End.X);
        Assert.Equal(Math.Abs(walk.End.X - walk.Start.X) / 80, walk.Duration.TotalSeconds, 6);

        controller.Tick(walk.Duration);
        Assert.Equal(walk.End.X, controller.Snapshot.X, 6);
        Assert.Equal(VisualState.Idle, controller.Snapshot.State);
    }

    [Fact]
    public void HoverAndDragSuspendPassivePlanning()
    {
        PresentationController controller = CreateController();
        controller.SetHovered(true);
        Assert.Null(controller.PlanPassiveMotion());
        controller.SetHovered(false);
        Assert.NotNull(controller.PlanPassiveMotion());
        controller.BeginDrag(50, 0);
        Assert.Null(controller.PlanPassiveMotion());
        controller.EndDrag();
        Assert.Null(controller.PlanPassiveMotion());
    }

    [Fact]
    public void FlickThenStationaryPointerNeedsOnlyOneRetarget()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(55, 10);
        Assert.False(controller.TryUpdateDrag(55, 10));
        Assert.True(controller.TryUpdateDrag(95, 110));
        TimeSpan step = TimeSpan.FromSeconds(1.0 / 120);
        IReadOnlyList<PresentationSnapshot> uninterrupted = controller.PreviewInteraction(step, 120);

        foreach (PresentationSnapshot expected in uninterrupted.Skip(1))
        {
            Assert.False(controller.TryUpdateDrag(95, 110));
            controller.Tick(step);
            Assert.Equal(expected, controller.Snapshot);
        }

        Assert.InRange(controller.Snapshot.X, 89.99, 90);
        Assert.InRange(controller.Snapshot.HopOffset, 99.99, 100);
        Assert.True(controller.TryUpdateDrag(25, 110));
        Assert.True(controller.TryUpdateDrag(25, 80));
    }

    [Fact]
    public void ClampedPointerChangesDoNotRetargetTheSameBoundary()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        Assert.False(controller.TryUpdateDrag(500, 500));
        controller.BeginDrag(50, 0);
        Assert.True(controller.TryUpdateDrag(500, 500));
        Assert.False(controller.TryUpdateDrag(600, 600));
        Assert.True(controller.TryUpdateDrag(99, 199));
        controller.EndDrag();
        Assert.False(controller.TryUpdateDrag(50, 0));
    }

    [Fact]
    public void DragSettlesExactlyAndPreviewEndsAtRest()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.SetDragSettlingTolerance(0.25);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(90, 100);
        TimeSpan step = TimeSpan.FromSeconds(1.0 / 120);
        PresentationSnapshot before = controller.Snapshot;

        IReadOnlyList<PresentationSnapshot> preview = controller.PreviewInteraction(step, 120);

        Assert.Equal(before, controller.Snapshot);
        Assert.InRange(preview.Count, 3, 120);
        Assert.Equal(90, preview[^1].X);
        Assert.Equal(100, preview[^1].HopOffset);
        foreach (PresentationSnapshot expected in preview.Skip(1))
        {
            controller.Tick(step);
            Assert.Equal(expected, controller.Snapshot);
        }
        controller.Tick(TimeSpan.FromMilliseconds(100));
        Assert.Equal(preview[^1], controller.Snapshot);
        Assert.True(controller.TryUpdateDrag(70, 80));
        controller.Tick(step);
        Assert.InRange(controller.Snapshot.X, 70.01, 89.99);
        Assert.InRange(controller.Snapshot.HopOffset, 80.01, 99.99);
    }

    [Fact]
    public void PassingNearTargetAtSpeedDoesNotSettle()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.SetDragSettlingTolerance(0.25);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(100, 100);
        controller.Tick(TimeSpan.FromMilliseconds(40));
        PresentationSnapshot moving = controller.Snapshot;
        controller.UpdateDrag(moving.X, moving.HopOffset);

        controller.Tick(TimeSpan.FromMilliseconds(0.1));

        Assert.InRange(controller.Snapshot.X - moving.X, 0.001, 0.25);
        Assert.True(controller.Snapshot.HopOffset > moving.HopOffset);
    }

    [Fact]
    public void ReleasingSettledDragHasNoResidualHorizontalFling()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.SetDragSettlingTolerance(0.25);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(90, 100);
        for (int sample = 0; sample < 120; sample++)
        {
            controller.Tick(TimeSpan.FromSeconds(1.0 / 120));
        }
        controller.EndDrag();
        controller.Tick(TimeSpan.FromMilliseconds(16));
        Assert.Equal(90, controller.Snapshot.X);
        Assert.True(controller.Snapshot.HopOffset < 100);
    }

    [Fact]
    public void ExternalDragPoseOwnsReleasePositionAndVelocity()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.UpdateDrag(90, 100);
        controller.SetExternalDragPose(65, 70, 120, 80);
        Assert.Equal(65, controller.Snapshot.X);
        Assert.Equal(70, controller.Snapshot.HopOffset);

        controller.EndDragFromPose(66, 71, 120, 80);

        Assert.Equal(VisualState.Airborne, controller.Snapshot.State);
        Assert.Equal(66, controller.Snapshot.X);
        Assert.Equal(71, controller.Snapshot.HopOffset);
        controller.Tick(TimeSpan.FromMilliseconds(10));
        Assert.Equal(67.2, controller.Snapshot.X, 6);
        Assert.True(controller.Snapshot.HopOffset > 71);
    }

    [Fact]
    public void ExternalDragPoseClampsBoundsAndRejectsNonfiniteValues()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.SetExternalDragPose(-10, 300, 0, 0);
        Assert.Equal(0, controller.Snapshot.X);
        Assert.Equal(200, controller.Snapshot.HopOffset);
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetExternalDragPose(double.NaN, 0, 0, 0));
    }

    [Fact]
    public void ExternalFlightCanBeRegrabbedAndHandedOffToLanding()
    {
        PresentationController controller = CreateController();
        controller.SetMaximumLift(200);
        controller.BeginDrag(50, 0);
        controller.EndDragFromPose(60, 150, 0, 0);
        controller.SetExternalFlightPose(75, 120);
        Assert.Equal(VisualState.Airborne, controller.Snapshot.State);
        controller.BeginDrag(75, 120);
        Assert.Equal(75, controller.Snapshot.X);
        Assert.Equal(120, controller.Snapshot.HopOffset);
        controller.EndDrag();
        controller.SetExternalFlightPose(80, 0, -600);
        controller.Tick(TimeSpan.FromMilliseconds(8));
        Assert.Equal(VisualState.Landing, controller.Snapshot.State);
        Assert.Equal(80, controller.Snapshot.X);
        controller.Tick(TimeSpan.FromMilliseconds(40));
        Assert.True(controller.Snapshot.ScaleY < 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetExternalFlightPose(0, double.NaN));
    }

    private static PresentationController CreateController() =>
        new(ValidOptions(), new SequenceRandom(0, 1, 0, 1), 0, 100);

    private static PresentationOptions ValidOptions(bool reducedMotion = false) => new()
    {
        Sprite = new SpriteOptions
        {
            Frames = Enum.GetValues<SpriteFrame>().ToDictionary(
                frame => frame,
                _ => new FrameRectangle { Width = 16, Height = 16 })
        },
        Wander = new WanderOptions
        {
            Speed = 80,
            IdleMinimumSeconds = 1,
            IdleMaximumSeconds = 1,
            PreferredMinimumMove = 20,
            WalkFrameSeconds = 0.2
        },
        Attention = new AttentionOptions { ReducedMotion = reducedMotion },
        Breathing = new BreathingOptions { DurationSeconds = 2, Amount = 0.04 },
        Physics = new PhysicsOptions()
    };

    private sealed class SequenceRandom(params double[] values) : IRandomSource
    {
        private int index;
        public double NextDouble() => values.Length == 0 ? 0 : values[index++ % values.Length];
    }
}