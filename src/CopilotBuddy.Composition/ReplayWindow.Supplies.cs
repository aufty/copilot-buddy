using System.Diagnostics;
using System.Numerics;
using CopilotBuddy.Core;
using Windows.UI.Composition;

namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private readonly BuddyNeedsController buddyNeeds =
        new(new BuddyNeedsOptions(), new SystemRandomSource());
    private readonly System.Windows.Forms.Timer needsTimer = new() { Interval = 1000 };
    private readonly Dictionary<SupplyKind, SupplyItem> supplies = [];
    private readonly List<SupplyItem> supplyZOrder = [];
    private readonly Dictionary<SupplyItem, long> supplyLandingOrder = [];
    private SupplyItem? carriedSupply;
    private SupplyItem? currentSupplyTarget;
    private SupplyItem? engagedSupply;
    private SupplyItem? pressedSupply;
    private MouseButtons previousSupplyButtons;
    private MouseButtons previousSupplyPressButtons;
    private bool waitingForSupplyRelease;
    private bool supplyPressPending;
    private bool supplyDragging;
    private Point supplyDragStart;
    private long supplyLandingSequence;
    private long needsTimestamp;
    private CompositionScopedBatch? supplyRetrieval;
    private CompositionScopedBatch? supplyAttentionLanding;
    private CompositionScopedBatch? chairHop;
    private Action? chairHopContinuation;
    private bool chairHopEntering;
    private bool supplyPausedForAlert;
    private bool seated;
    private bool chairRecallPending;
    private bool ballPlayStarted;
    private int ballChasesRemaining;
    private string? supplyFeedbackMessage;
    private readonly List<CompositionObject> heartResources = [];
    private ContainerVisual? heartVisual;
    private readonly List<CompositionObject> foodParticleResources = [];
    private ContainerVisual? foodParticles;
    private CompositionScopedBatch? foodParticleBatch;
    private readonly List<CompositionObject> waterParticleResources = [];
    private ContainerVisual? waterParticles;
    private CompositionScopedBatch? waterParticleBatch;

    private bool SupplyEngaged => engagedSupply is not null;
    private string? SupplyMessage => supplyFeedbackMessage ?? buddyNeeds.CurrentRequest switch
        {
            BuddyNeed.Food => "I'm hungry!",
            BuddyNeed.Water => "I'm thirsty!",
            BuddyNeed.Play => "I want to play!",
            _ => null
        };

    private void StartNeedsHost()
    {
        needsTimestamp = Stopwatch.GetTimestamp();
        needsTimer.Tick += (_, _) =>
        {
            AdvanceNeeds();
            TryStartSupplyRetrieval();
        };
        needsTimer.Start();
    }

    private void AdvanceNeeds()
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(needsTimestamp, now);
        needsTimestamp = now;
        if (!buddyActive)
        {
            return;
        }
        BuddyNeed? previous = buddyNeeds.CurrentRequest;
        buddyNeeds.Tick(elapsed);
        if (previous != buddyNeeds.CurrentRequest)
        {
            RefreshAttention();
        }
    }

    private SupplyItem? Chair => supplies.GetValueOrDefault(SupplyKind.Chair);

    private void SelectSupply(SupplyKind kind)
    {
        if (SkillsBlocked || !buddyActive)
        {
            return;
        }
        if (supplies.TryGetValue(kind, out SupplyItem? existing))
        {
            RequestSupplyRecall(existing);
            return;
        }
        if (carriedSupply is not null || supplyPressPending || supplyDragging)
        {
            return;
        }
        SupplyItem supply = new(compositor!, root!, kind, dpiScale, ClientSize);
        supplies.Add(kind, supply);
        supplyZOrder.Add(supply);
        supply.Landed += OnSupplyLanded;
        supply.ThrowCompleted += OnBallThrowCompleted;
        supply.ConsumptionCompleted += OnSupplyConsumed;
        carriedSupply = supply;
        supply.Follow(PointToClient(Cursor.Position));
        previousSupplyButtons = Control.MouseButtons;
        waitingForSupplyRelease = true;
        skillMenu.SetSupplyState(kind, deployed: true);
        UpdateSupplyAlertPause(HasSessionAction);
        UpdatePointerRouting();
    }

    private void PollSupplyPlacement(Point clientPoint)
    {
        if (supplyPausedForAlert || carriedSupply is not { IsCarried: true } supply)
        {
            return;
        }
        supply.Follow(clientPoint);
        MouseButtons buttons = Control.MouseButtons;
        bool leftDown = (buttons & MouseButtons.Left) != 0;
        bool leftWasDown = (previousSupplyButtons & MouseButtons.Left) != 0;
        previousSupplyButtons = buttons;
        if (waitingForSupplyRelease)
        {
            if (!leftDown)
            {
                waitingForSupplyRelease = false;
            }
            return;
        }
        if (leftDown && !leftWasDown)
        {
            supply.Drop();
            carriedSupply = null;
            RefreshAttention();
        }
    }

    private void OnSupplyLanded(object? sender, EventArgs args)
    {
        if (sender is not SupplyItem supply || !supplies.ContainsValue(supply))
        {
            return;
        }
        if (ReferenceEquals(carriedSupply, supply))
        {
            carriedSupply = null;
            waitingForSupplyRelease = false;
        }
        if (!ReferenceEquals(engagedSupply, supply))
        {
            supplyLandingOrder[supply] = ++supplyLandingSequence;
        }
        if (placementPending)
        {
            placementPending = false;
            RefreshPlacement();
        }
        TryStartSupplyRetrieval();
    }

    private void OnBallThrowCompleted(object? sender, EventArgs args)
    {
        if (sender is not SupplyItem supply || !ReferenceEquals(engagedSupply, supply))
        {
            return;
        }
        if (placementPending)
        {
            placementPending = false;
            RefreshPlacement();
        }
        TryStartSupplyRetrieval();
    }

    private bool TryStartSupplyRetrieval()
    {
        if (!buddyActive || supplyPausedForAlert || supplyRetrieval is not null ||
            supplyAttentionLanding is not null || chairHop is not null ||
            dragging || releasingDrag || supplyDragging || carriedSupply is not null || presentDragging ||
            sessionLaunchJump is not null || handoffWindowJump is not null || visitingHandoffWindow ||
            controller is null || !controller.IsVisible)
        {
            return false;
        }
        SupplyItem? supply = NextSupplyObjective();
        if (supply is null)
        {
            return false;
        }
        if (TryFinishAttentionLandingBeforeSupply())
        {
            return true;
        }
        if (seated && supply.Kind != SupplyKind.Chair)
        {
            BeginChairHop(entering: false, () => TryStartSupplyRetrieval());
            return true;
        }

        StopAttentionBounce();
        passiveTimer.Stop();
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        PresentationSnapshot current = controller.Snapshot;
        double targetDip = Math.Clamp(
            supply.CenterX / dpiScale - spriteWidth / 2,
            0,
            Math.Max(0, ClientSize.Width / dpiScale - spriteWidth));
        double distance = Math.Abs(targetDip - current.X);
        TimeSpan duration = presentation.Attention.ReducedMotion
            ? TimeSpan.FromMilliseconds(1)
            : TimeSpan.FromSeconds(Math.Max(0.18, distance / (presentation.Wander.Speed * 1.65)));
        using Vector3KeyFrameAnimation movement = compositor!.CreateVector3KeyFrameAnimation();
        using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
        movement.Duration = duration;
        movement.InsertExpressionKeyFrame(0, "this.StartingValue");
        movement.InsertKeyFrame(1, new Vector3((float)(targetDip * dpiScale), (float)(baseline * dpiScale), 0), easing);
        if (!presentation.Attention.ReducedMotion && distance > 1)
        {
            ShowFrame(targetDip < current.X ? SpriteFrame.WalkLeft1 : SpriteFrame.WalkRight1);
            StartWalkingFrames(targetDip < current.X);
        }
        else
        {
            ShowFrame(SpriteFrame.Standing);
        }
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        supplyRetrieval = batch;
        currentSupplyTarget = supply;
        batch.Completed += (_, _) =>
        {
            if (!ReferenceEquals(supplyRetrieval, batch) || !ReferenceEquals(currentSupplyTarget, supply))
            {
                return;
            }
            supplyRetrieval = null;
            currentSupplyTarget = null;
            batch.Dispose();
            controller.PlaceAt(targetDip);
            sprite.Offset = PositionOf(controller.Snapshot);
            if (supply.Kind == SupplyKind.Chair)
            {
                BeginChairHop(entering: true);
                return;
            }
            engagedSupply = supply;
            supplyLandingOrder.Remove(supply);
            if (supply.Kind == SupplyKind.Ball)
            {
                if (!ballPlayStarted)
                {
                    ballPlayStarted = true;
                    ballChasesRemaining = Random.Shared.Next(2, 6);
                    BeginBallThrow(supply);
                    return;
                }
                ballChasesRemaining--;
                if (ballChasesRemaining > 0)
                {
                    BeginBallThrow(supply);
                    return;
                }
            }
            if (supply.Kind == SupplyKind.Food)
            {
                BeginEating(supply);
                return;
            }
            if (supply.Kind == SupplyKind.Water)
            {
                BeginDrinking(supply);
                return;
            }
            CompleteSupply(supply);
        };
        sprite.StartAnimation(nameof(sprite.Offset), movement);
        batch.End();
        return true;
    }

    private bool TryFinishAttentionLandingBeforeSupply()
    {
        if (attentionBounce is null)
        {
            return false;
        }

        Vector3 current = CurrentRenderedBuddyOffset();
        StopAttentionBounce();
        passiveTimer.Stop();
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        controller!.PlaceAt(current.X / dpiScale);
        Vector3 landed = PositionOf(controller.Snapshot with { HopOffset = 0 });
        if (current.Y >= landed.Y - 0.5f)
        {
            sprite.Offset = landed;
            return false;
        }

        ShowFrame(SpriteFrame.Standing);
        using Vector3KeyFrameAnimation landing = compositor!.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.55f, 0), new Vector2(0.85f, 0.35f));
        landing.Duration = TimeSpan.FromMilliseconds(180);
        landing.InsertKeyFrame(0, current);
        landing.InsertKeyFrame(1, landed, easing);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        supplyAttentionLanding = batch;
        batch.Completed += (_, _) =>
        {
            if (!Disposing && !IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() => CompleteSupplyAttentionLanding(batch, landed));
            }
        };
        sprite.StartAnimation(nameof(sprite.Offset), landing);
        batch.End();
        return true;
    }

    private void CompleteSupplyAttentionLanding(CompositionScopedBatch batch, Vector3 landed)
    {
        if (!ReferenceEquals(supplyAttentionLanding, batch))
        {
            return;
        }
        supplyAttentionLanding = null;
        batch.Dispose();
        sprite!.Offset = landed;
        modelTimestamp = Stopwatch.GetTimestamp();
        TryStartSupplyRetrieval();
    }

    private SupplyItem? NextSupplyObjective()
    {
        if (engagedSupply is not null)
        {
            return engagedSupply is { Kind: SupplyKind.Ball, IsCarried: false, IsMoving: false }
                ? engagedSupply
                : null;
        }
        SupplyItem? next = supplies.Values
            .Where(supply => supply.Kind != SupplyKind.Chair &&
                !ReferenceEquals(supply, engagedSupply) &&
                !supply.IsCarried && !supply.IsMoving)
            .OrderBy(supply => supplyLandingOrder.GetValueOrDefault(supply, long.MaxValue))
            .FirstOrDefault();
        if (next is not null)
        {
            return next;
        }
        if (supplies.Values.Any(supply => supply.Kind != SupplyKind.Chair))
        {
            return null;
        }
        return Chair is { IsCarried: false, IsMoving: false, IsOccupied: false } chair
            ? chair
            : null;
    }

    private void BeginBallThrow(SupplyItem ball)
    {
        ShowFrame(SpriteFrame.Wave1);
        double direction = Random.Shared.Next(2) == 0 ? -1 : 1;
        double edgeMargin = 90 * dpiScale;
        if (ball.CenterX < edgeMargin)
        {
            direction = 1;
        }
        else if (ball.CenterX > ClientSize.Width - edgeMargin)
        {
            direction = -1;
        }
        double horizontalVelocity = direction * Random.Shared.Next(145, 361);
        double upwardVelocity = Random.Shared.Next(255, 451);
        double floorRestitution = 0.42 + Random.Shared.NextDouble() * 0.28;
        ball.ThrowBall(horizontalVelocity, upwardVelocity, presentation.Physics, floorRestitution);
    }

    private void BeginEating(SupplyItem food)
    {
        PresentationSnapshot snapshot = controller!.Snapshot;
        Point face = new(
            (int)Math.Round((snapshot.X + spriteWidth * 0.58) * dpiScale),
            (int)Math.Round((baseline + spriteHeight * 0.3) * dpiScale));
        supplyFeedbackMessage = "munch munch munch!";
        RefreshAttention();
        StopAttentionBounce();
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.Offset = PositionOf(controller.Snapshot);
        ShowFrame(SpriteFrame.Wave1);
        StartAlternatingFrames(SpriteFrame.Wave1, SpriteFrame.Wave2, 0.14);
        ShowFoodParticles(face);
        food.ConsumeAt(face);
    }

    private void OnSupplyConsumed(object? sender, EventArgs args)
    {
        if (sender is SupplyItem supply && ReferenceEquals(engagedSupply, supply))
        {
            CompleteSupply(supply);
        }
    }

    private void ShowFoodParticles(Point face)
    {
        ClearFoodParticles();
        ContainerVisual particles = compositor!.CreateContainerVisual();
        root!.Children.InsertAtTop(particles);
        foodParticles = particles;
        TimeSpan duration = TimeSpan.FromMilliseconds(1450);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foodParticleBatch = batch;
        for (int index = 0; index < 12; index++)
        {
            float size = Random.Shared.Next(1, 3) * dpiScale;
            SpriteVisual crumb = compositor.CreateSpriteVisual();
            crumb.Size = new Vector2(size);
            CompositionColorBrush brush = compositor.CreateColorBrush(index % 3 == 0
                ? Windows.UI.Color.FromArgb(255, 205, 72, 62)
                : Windows.UI.Color.FromArgb(255, 244, 192, 68));
            crumb.Brush = brush;
            float delay = 0.2f + (index % 3) * 0.22f + Random.Shared.NextSingle() * 0.08f;
            Vector3 start = new(
                face.X + Random.Shared.Next(-3, 4) * dpiScale,
                face.Y + Random.Shared.Next(-2, 3) * dpiScale,
                0);
            Vector3 end = start + new Vector3(
                Random.Shared.Next(-18, 19) * dpiScale,
                Random.Shared.Next(8, 25) * dpiScale,
                0);
            crumb.Offset = start;
            using Vector3KeyFrameAnimation scatter = compositor.CreateVector3KeyFrameAnimation();
            scatter.Duration = duration;
            scatter.InsertKeyFrame(0, start);
            scatter.InsertKeyFrame(delay, start);
            scatter.InsertKeyFrame(Math.Min(1, delay + 0.24f), end);
            using ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = duration;
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(delay, 0);
            fade.InsertKeyFrame(Math.Min(1, delay + 0.02f), 1);
            fade.InsertKeyFrame(Math.Min(1, delay + 0.24f), 0);
            particles.Children.InsertAtTop(crumb);
            crumb.StartAnimation(nameof(crumb.Offset), scatter);
            crumb.StartAnimation(nameof(crumb.Opacity), fade);
            foodParticleResources.Add(crumb);
            foodParticleResources.Add(brush);
        }
        batch.Completed += (_, _) =>
        {
            if (ReferenceEquals(foodParticleBatch, batch))
            {
                foodParticleBatch = null;
                batch.Dispose();
                ClearFoodParticles();
            }
        };
        batch.End();
    }

    private void BeginDrinking(SupplyItem water)
    {
        PresentationSnapshot snapshot = controller!.Snapshot;
        Point face = new(
            (int)Math.Round((snapshot.X + spriteWidth * 0.57) * dpiScale),
            (int)Math.Round((baseline + spriteHeight * 0.3) * dpiScale));
        supplyFeedbackMessage = "glug glug glug!";
        RefreshAttention();
        StopAttentionBounce();
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.Offset = PositionOf(controller.Snapshot);
        ShowFrame(SpriteFrame.Wave1);
        StartAlternatingFrames(SpriteFrame.Wave1, SpriteFrame.Wave2, 0.18);
        ShowWaterParticles(face);
        water.DrinkAt(face);
    }

    private void ShowWaterParticles(Point face)
    {
        ClearWaterParticles();
        ContainerVisual particles = compositor!.CreateContainerVisual();
        root!.Children.InsertAtTop(particles);
        waterParticles = particles;
        TimeSpan duration = TimeSpan.FromMilliseconds(1650);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        waterParticleBatch = batch;
        for (int index = 0; index < 12; index++)
        {
            float width = Random.Shared.Next(1, 3) * dpiScale;
            float height = Random.Shared.Next(2, 5) * dpiScale;
            SpriteVisual drop = compositor.CreateSpriteVisual();
            drop.Size = new Vector2(width, height);
            CompositionColorBrush brush = compositor.CreateColorBrush(index % 4 == 0
                ? Windows.UI.Color.FromArgb(255, 209, 242, 255)
                : Windows.UI.Color.FromArgb(255, 63, 165, 224));
            drop.Brush = brush;
            float delay = 0.2f + (index % 3) * 0.22f + Random.Shared.NextSingle() * 0.09f;
            Vector3 start = new(
                face.X + Random.Shared.Next(-2, 5) * dpiScale,
                face.Y + Random.Shared.Next(0, 4) * dpiScale,
                0);
            Vector3 end = start + new Vector3(
                Random.Shared.Next(-8, 9) * dpiScale,
                Random.Shared.Next(18, 34) * dpiScale,
                0);
            drop.Offset = start;
            using Vector3KeyFrameAnimation falling = compositor.CreateVector3KeyFrameAnimation();
            falling.Duration = duration;
            falling.InsertKeyFrame(0, start);
            falling.InsertKeyFrame(delay, start);
            falling.InsertKeyFrame(Math.Min(1, delay + 0.25f), end);
            using ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = duration;
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(delay, 0);
            fade.InsertKeyFrame(Math.Min(1, delay + 0.02f), 0.9f);
            fade.InsertKeyFrame(Math.Min(1, delay + 0.25f), 0);
            particles.Children.InsertAtTop(drop);
            drop.StartAnimation(nameof(drop.Offset), falling);
            drop.StartAnimation(nameof(drop.Opacity), fade);
            waterParticleResources.Add(drop);
            waterParticleResources.Add(brush);
        }
        batch.Completed += (_, _) =>
        {
            if (ReferenceEquals(waterParticleBatch, batch))
            {
                waterParticleBatch = null;
                batch.Dispose();
                ClearWaterParticles();
            }
        };
        batch.End();
    }

    private void ClearWaterParticles()
    {
        waterParticleBatch?.Dispose();
        waterParticleBatch = null;
        if (waterParticles is not null)
        {
            root?.Children.Remove(waterParticles);
            waterParticles.Children.RemoveAll();
            waterParticles.Dispose();
            waterParticles = null;
        }
        foreach (CompositionObject resource in waterParticleResources)
        {
            resource.Dispose();
        }
        waterParticleResources.Clear();
    }

    private void UpdateSupplyAlertPause(bool paused)
    {
        if (supplyPausedForAlert == paused)
        {
            return;
        }
        supplyPausedForAlert = paused;
        foreach (SupplyItem supply in supplies.Values)
        {
            supply.SetPaused(paused);
        }
        SetParticleAnimationsPaused(foodParticleResources, paused);
        SetParticleAnimationsPaused(waterParticleResources, paused);
        if (paused)
        {
            CancelSupplyRetrieval();
            if (chairHop is not null && chairHopEntering)
            {
                CancelChairHop();
            }
            if (seated)
            {
                BeginChairHop(entering: false);
            }
        }
        else if (!paused)
        {
            TryStartSupplyRetrieval();
        }
    }

    private static void SetParticleAnimationsPaused(
        IEnumerable<CompositionObject> resources,
        bool paused)
    {
        foreach (Visual visual in resources.OfType<Visual>())
        {
            SetAnimationPaused(visual, nameof(visual.Offset), paused);
            SetAnimationPaused(visual, nameof(visual.Opacity), paused);
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

    private void ClearFoodParticles()
    {
        foodParticleBatch?.Dispose();
        foodParticleBatch = null;
        if (foodParticles is not null)
        {
            root?.Children.Remove(foodParticles);
            foodParticles.Children.RemoveAll();
            foodParticles.Dispose();
            foodParticles = null;
        }
        foreach (CompositionObject resource in foodParticleResources)
        {
            resource.Dispose();
        }
        foodParticleResources.Clear();
    }

    private void RequestSupplyRecall(SupplyItem supply)
    {
        if (supply.IsBuddyOwned)
        {
            skillMenu.SetSupplyState(supply.Kind, deployed: true, recallPending: true);
            return;
        }
        if (supply.IsOccupied)
        {
            if (dragging || releasingDrag)
            {
                chairRecallPending = true;
                skillMenu.SetSupplyState(supply.Kind, deployed: true, recallPending: true);
                return;
            }
            BeginChairHop(entering: false, () => RemoveSupply(supply));
            return;
        }
        RemoveSupply(supply);
    }

    private bool RemoveSupply(SupplyItem supply, bool resume = true)
    {
        if (!supplies.TryGetValue(supply.Kind, out SupplyItem? current) ||
            !ReferenceEquals(current, supply))
        {
            return false;
        }
        if (ReferenceEquals(currentSupplyTarget, supply))
        {
            CancelSupplyRetrieval();
        }
        if (ReferenceEquals(engagedSupply, supply))
        {
            engagedSupply = null;
            ballPlayStarted = false;
            ballChasesRemaining = 0;
            supplyFeedbackMessage = null;
        }
        if (ReferenceEquals(carriedSupply, supply))
        {
            carriedSupply = null;
            waitingForSupplyRelease = false;
        }
        if (ReferenceEquals(pressedSupply, supply))
        {
            pressedSupply = null;
            supplyPressPending = false;
            supplyDragging = false;
            Capture = false;
        }
        supplies.Remove(supply.Kind);
        supplyZOrder.Remove(supply);
        supplyLandingOrder.Remove(supply);
        supply.Landed -= OnSupplyLanded;
        supply.ThrowCompleted -= OnBallThrowCompleted;
        supply.ConsumptionCompleted -= OnSupplyConsumed;
        supply.Dispose();
        skillMenu.SetSupplyState(supply.Kind, deployed: false);
        if (supply.Kind == SupplyKind.Chair)
        {
            seated = false;
            chairRecallPending = false;
        }
        RefreshAttention();
        UpdatePointerRouting();
        if (resume)
        {
            StartPassiveMotion();
        }
        return true;
    }

    private void CancelSupplyRetrieval()
    {
        CompositionScopedBatch? batch = supplyRetrieval;
        supplyRetrieval = null;
        currentSupplyTarget = null;
        if (batch is null)
        {
            return;
        }
        Vector3 current = CurrentRenderedBuddyOffset();
        batch.Dispose();
        sprite!.StopAnimation(nameof(sprite.Offset));
        controller!.PlaceAt(current.X / dpiScale);
        sprite.Offset = PositionOf(controller.Snapshot);
    }

    private void InterruptSupplyMotionForSkill()
    {
        CancelSupplyRetrieval();
        if (chairHop is not null)
        {
            CancelChairHop();
        }
    }

    private void SetBuddyIdleForSupplyPickup()
    {
        CancelSupplyRetrieval();
        Vector3 current = CurrentRenderedBuddyOffset();
        passiveTimer.Stop();
        controller!.PlaceAt(current.X / dpiScale);
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.Offset = PositionOf(controller.Snapshot);
        controller.ResetWandering();
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        ShowFrame(SpriteFrame.Standing);
        StartBreathing();
    }

    private void CancelSupplyInteractionForPickup(SupplyItem supply)
    {
        if (ReferenceEquals(currentSupplyTarget, supply))
        {
            CancelSupplyRetrieval();
        }
        if (ReferenceEquals(engagedSupply, supply) && !supply.IsBuddyOwned)
        {
            engagedSupply = null;
            ballPlayStarted = false;
            ballChasesRemaining = 0;
            supplyFeedbackMessage = null;
            RefreshAttention();
        }
        if (supply.Kind == SupplyKind.Chair && chairHop is not null && chairHopEntering)
        {
            CancelChairHop();
        }
    }

    private void BeginChairHop(bool entering, Action? continuation = null)
    {
        SupplyItem? chair = Chair;
        if (chair is null || entering == seated || chairHop is not null)
        {
            continuation?.Invoke();
            return;
        }
        CancelSupplyRetrieval();
        passiveTimer.Stop();
        StopAttentionBounce();
        chairHopContinuation = continuation;
        chairHopEntering = entering;
        Vector3 renderedPosition = CurrentRenderedBuddyOffset();
        controller!.PlaceAt(renderedPosition.X / dpiScale);
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.Offset = PositionOf(controller.Snapshot);
        if (!entering)
        {
            chair.DetachFromBuddy(sprite, controller.Snapshot.X, spriteWidth);
            seated = false;
        }
        controller.ResetWandering();
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        ShowFrame(SpriteFrame.Standing);
        Vector3 basePosition = PositionOf(controller.Snapshot with { HopOffset = 0 });
        if (presentation.Attention.ReducedMotion)
        {
            sprite.Offset = basePosition;
            CompleteChairHop(chair, entering);
            return;
        }
        using Vector3KeyFrameAnimation hop = compositor!.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction launch = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1));
        using CubicBezierEasingFunction land = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.6f, 0), new Vector2(0.8f, 0.2f));
        hop.Duration = TimeSpan.FromMilliseconds(520);
        hop.InsertKeyFrame(0, basePosition);
        hop.InsertKeyFrame(0.45f, basePosition - new Vector3(0, 12 * dpiScale, 0), launch);
        hop.InsertKeyFrame(1, basePosition, land);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        chairHop = batch;
        batch.Completed += (_, _) =>
        {
            if (!Disposing && !IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    if (!ReferenceEquals(chairHop, batch))
                    {
                        return;
                    }
                    chairHop = null;
                    batch.Dispose();
                    sprite.Offset = basePosition;
                    CompleteChairHop(chair, entering);
                });
            }
        };
        sprite.StartAnimation(nameof(sprite.Offset), hop);
        batch.End();
    }

    private void CompleteChairHop(SupplyItem chair, bool entering)
    {
        if (entering && supplies.GetValueOrDefault(SupplyKind.Chair) == chair &&
            !chair.IsCarried && !chair.IsMoving)
        {
            chair.AttachToBuddy(sprite!);
            seated = true;
            controller!.ResetWandering();
        }
        Action? continuation = chairHopContinuation;
        chairHopContinuation = null;
        chairHopEntering = false;
        if (continuation is not null)
        {
            continuation();
        }
        else
        {
            StartPassiveMotion();
        }
    }

    private void CancelChairHop()
    {
        CompositionScopedBatch? batch = chairHop;
        chairHop = null;
        chairHopContinuation = null;
        chairHopEntering = false;
        batch?.Dispose();
        Vector3 current = CurrentRenderedBuddyOffset();
        sprite!.StopAnimation(nameof(sprite.Offset));
        controller!.PlaceAt(current.X / dpiScale);
        sprite.Offset = PositionOf(controller.Snapshot);
    }

    private SupplyItem? FindSupply(Point clientPoint)
    {
        for (int index = supplyZOrder.Count - 1; index >= 0; index--)
        {
            SupplyItem supply = supplyZOrder[index];
            if (supply.HitTest(clientPoint))
            {
                return supply;
            }
        }
        return null;
    }

    private bool HitSupply(Point clientPoint) => FindSupply(clientPoint) is not null;

    private bool HitOccupiedChair(Point clientPoint)
    {
        if (!seated || Chair is not { } chair)
        {
            return false;
        }
        Vector3 buddyPosition = bubblePosition?.Position ?? sprite!.Offset;
        return chair.HitTestAttached(clientPoint, buddyPosition);
    }

    private void PollSupplyPress(Point clientPoint)
    {
        if (carriedSupply is not null || waitingForSupplyRelease)
        {
            previousSupplyPressButtons = Control.MouseButtons;
            return;
        }
        MouseButtons buttons = Control.MouseButtons;
        bool leftDown = (buttons & MouseButtons.Left) != 0;
        bool leftWasDown = (previousSupplyPressButtons & MouseButtons.Left) != 0;
        previousSupplyPressButtons = buttons;
        if (leftDown && !leftWasDown && !supplyPressPending && !supplyDragging)
        {
            BeginSupplyPress(clientPoint);
        }
        else if (leftDown && (supplyPressPending || supplyDragging))
        {
            TrackSupplyPress(clientPoint);
        }
        else if (!leftDown && leftWasDown && (supplyPressPending || supplyDragging))
        {
            TrackSupplyPress(clientPoint);
            EndSupplyPress(clientPoint);
        }
    }

    private void BeginSupplyPress(Point clientPoint)
    {
        pressedSupply = FindSupply(clientPoint);
        if (pressedSupply is null)
        {
            return;
        }
        supplyDragStart = clientPoint;
        supplyPressPending = true;
        supplyDragging = false;
        Capture = true;
    }

    private bool TrackSupplyPress(Point clientPoint)
    {
        if (!supplyPressPending && !supplyDragging)
        {
            return false;
        }
        if (supplyPressPending && HasMovedForDrag(supplyDragStart, clientPoint))
        {
            supplyPressPending = false;
            supplyDragging = true;
            SupplyItem supply = pressedSupply!;
            CancelSupplyInteractionForPickup(supply);
            SetBuddyIdleForSupplyPickup();
            supplyZOrder.Remove(supply);
            supplyZOrder.Add(supply);
            supply.BeginDrag(
                supplyDragStart,
                presentation.Physics,
                directDrag,
                presentation.Attention.ReducedMotion,
                DispatchNativeDrag);
            inputTimer.Start();
            TryStartSupplyRetrieval();
        }
        if (supplyDragging)
        {
            pendingPointer = clientPoint;
            pointerPending = true;
        }
        return true;
    }

    private bool EndSupplyPress(Point clientPoint)
    {
        if (!supplyPressPending && !supplyDragging)
        {
            return false;
        }
        supplyPressPending = false;
        if (supplyDragging)
        {
            pendingPointer = clientPoint;
            pointerPending = true;
            FlushPointer();
            supplyDragging = false;
            inputTimer.Stop();
            pressedSupply?.ReleaseDrag();
        }
        pressedSupply = null;
        Capture = false;
        UpdatePointerRouting();
        return true;
    }

    private void CancelOrReleaseSupplyPress()
    {
        supplyPressPending = false;
        if (supplyDragging)
        {
            FlushPointer();
            supplyDragging = false;
            inputTimer.Stop();
            pressedSupply?.ReleaseDrag();
        }
        pressedSupply = null;
    }

    private void CompleteSupply(SupplyItem supply)
    {
        BuddyNeed? fulfilled = supply.Kind.Need();
        if (!RemoveSupply(supply, resume: false))
        {
            return;
        }
        engagedSupply = null;
        ballPlayStarted = false;
        ballChasesRemaining = 0;
        supplyFeedbackMessage = null;
        ClearFoodParticles();
        ClearWaterParticles();
        if (fulfilled is { } need)
        {
            buddyNeeds.Fulfill(need);
        }
        ShowHeart();
        RefreshAttention();
        UpdatePointerRouting();
        if (placementPending)
        {
            placementPending = false;
            RefreshPlacement();
        }
        else
        {
            StartPassiveMotion();
        }
    }

    private void ShowHeart()
    {
        ClearHeart();
        ContainerVisual heart = compositor!.CreateContainerVisual();
        string[] pixels =
        [
            ".RR.RR.",
            "RRRRRRR",
            "RRRRRRR",
            ".RRRRR.",
            "..RRR..",
            "...R..."
        ];
        float pixelSize = 2 * dpiScale;
        heart.Size = new Vector2(pixels[0].Length * pixelSize, pixels.Length * pixelSize);
        for (int y = 0; y < pixels.Length; y++)
        {
            for (int x = 0; x < pixels[y].Length; x++)
            {
                if (pixels[y][x] != 'R')
                {
                    continue;
                }
                SpriteVisual pixel = compositor.CreateSpriteVisual();
                pixel.Size = new Vector2(pixelSize);
                pixel.Offset = new Vector3(x * pixelSize, y * pixelSize, 0);
                CompositionColorBrush brush = compositor.CreateColorBrush(
                    Windows.UI.Color.FromArgb(255, 245, 75, 105));
                pixel.Brush = brush;
                heart.Children.InsertAtTop(pixel);
                heartResources.Add(pixel);
                heartResources.Add(brush);
            }
        }
        PresentationSnapshot snapshot = controller!.Snapshot;
        Vector3 start = new(
            (float)((snapshot.X + spriteWidth / 2) * dpiScale - heart.Size.X / 2),
            (float)(baseline * dpiScale - heart.Size.Y - 5 * dpiScale),
            0);
        heart.Offset = start;
        root!.Children.InsertAtTop(heart);
        heartVisual = heart;
        using Vector3KeyFrameAnimation rise = compositor.CreateVector3KeyFrameAnimation();
        rise.Duration = TimeSpan.FromMilliseconds(900);
        rise.InsertKeyFrame(0, start);
        rise.InsertKeyFrame(1, start - new Vector3(0, 18 * dpiScale, 0));
        using ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = rise.Duration;
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(0.65f, 1);
        fade.InsertKeyFrame(1, 0);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (ReferenceEquals(heartVisual, heart))
            {
                ClearHeart();
            }
        };
        heart.StartAnimation(nameof(heart.Offset), rise);
        heart.StartAnimation(nameof(heart.Opacity), fade);
        batch.End();
    }

    private void ClearHeart()
    {
        if (heartVisual is not null)
        {
            root?.Children.Remove(heartVisual);
            heartVisual.Dispose();
            heartVisual = null;
        }
        foreach (CompositionObject resource in heartResources)
        {
            resource.Dispose();
        }
        heartResources.Clear();
    }

    private void RelayoutSupply()
    {
        foreach (SupplyItem supply in supplies.Values)
        {
            supply.Relayout(dpiScale, ClientSize);
        }
        if (seated && Chair is { } chair)
        {
            chair.ReattachToBuddy(sprite!);
        }
        ClearHeart();
        ClearFoodParticles();
        ClearWaterParticles();
    }

    private void StopNeedsHost()
    {
        needsTimer.Stop();
        needsTimer.Dispose();
        supplyRetrieval?.Dispose();
        supplyRetrieval = null;
        currentSupplyTarget = null;
        supplyAttentionLanding?.Dispose();
        supplyAttentionLanding = null;
        chairHop?.Dispose();
        chairHop = null;
        chairHopContinuation = null;
        supplyPausedForAlert = false;
        supplyFeedbackMessage = null;
        foreach (SupplyItem supply in supplies.Values)
        {
            supply.Landed -= OnSupplyLanded;
            supply.ThrowCompleted -= OnBallThrowCompleted;
            supply.ConsumptionCompleted -= OnSupplyConsumed;
            supply.Dispose();
        }
        supplies.Clear();
        supplyZOrder.Clear();
        supplyLandingOrder.Clear();
        carriedSupply = null;
        pressedSupply = null;
        engagedSupply = null;
        seated = false;
        ClearHeart();
        ClearFoodParticles();
        ClearWaterParticles();
    }
}
