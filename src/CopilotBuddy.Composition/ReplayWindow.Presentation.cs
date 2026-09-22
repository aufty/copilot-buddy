using System.Diagnostics;
using System.Numerics;
using CopilotBuddy.Core;
using Windows.UI.Composition;
using Windows.UI.Composition.Interactions;

namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private string DemoMessage =>
        $"Hello! Press {sessionSettings.Shortcuts.SkillMenu} to see what I can do!";
    private readonly CancellationTokenSource pipeCancellation = new();
    private NamedPipeCommandServer? pipeServer;
    private MessageBubble? bubble;
    private BubblePositionObserver? bubblePosition;
    private CompositionScopedBatch? attentionBounce;
    private bool attentionPressPending;
    private Point dragStart;
    private bool dragMoved;

    private void AddPresentationMenu()
    {
        AddSessionMenu();
    }

    private void StartPresentationHost()
    {
        bubblePosition = new BubblePositionObserver(sprite!);
        bubble = new MessageBubble(presentation.Bubble);
        bubble.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left && (HasSessionAction || HasNeedMessageAction))
            {
                OnActionRequested(bubble, EventArgs.Empty);
            }
            else if (args.Button == MouseButtons.Right)
            {
                ShowSkillMenu();
            }
        };
        controller!.ActionRequested += OnActionRequested;
        StartSessionIntegration();
        StartPresentHost();
        StartNeedsHost();
        StartAmbientQuips();
        pipeServer = new NamedPipeCommandServer(presentation.PipeName, HandleRequestAsync);
        _ = Task.Run(() => pipeServer.RunAsync(pipeCancellation.Token));
        pointerTimer.Start();
        if (!TaskbarMode)
        {
            StartPassiveMotion();
        }
    }

    private void StopPresentationHost()
    {
        StopAttentionBounce();
        StopSessionLaunchJump();
        StopAmbientQuips();
        StopNeedsHost();
        StopPresentHost();
        StopSessionIntegration();
        pipeCancellation.Cancel();
        if (controller is not null)
        {
            controller.ActionRequested -= OnActionRequested;
        }
        bubble?.Dispose();
        bubblePosition?.Dispose();
        bubblePosition = null;
    }

    private async Task<PresentationResponse> HandleRequestAsync(PresentationRequest request)
    {
        string? error = request.Version != PipeProtocol.Version
            ? $"Unsupported protocol version {request.Version}."
            : request.Command switch
            {
                PipeProtocol.Show when string.IsNullOrWhiteSpace(request.Text) => "The show command requires non-empty text.",
                PipeProtocol.Show when request.Text!.Length > PipeProtocol.MaximumMessageLength => $"Message exceeds {PipeProtocol.MaximumMessageLength} characters.",
                PipeProtocol.Show or PipeProtocol.Dismiss or PipeProtocol.Quip => null,
                PipeProtocol.Visible when request.Visible is null => "The visible command requires a boolean value.",
                PipeProtocol.Visible => null,
                _ => $"Unknown presentation command '{request.Command}'."
            };
        if (error is not null)
        {
            return new PresentationResponse(PipeProtocol.Version, false, error);
        }
        await InvokeAsync(() =>
        {
            switch (request.Command)
            {
                case PipeProtocol.Show:
                    ChangePresentation(() => controller!.ShowMessage(request.Text!));
                    break;
                case PipeProtocol.Dismiss:
                    ChangePresentation(() => controller!.DismissMessage());
                    break;
                case PipeProtocol.Visible:
                    ChangeVisibility(request.Visible!.Value);
                    break;
                case PipeProtocol.Quip:
                    TriggerAmbientQuip();
                    break;
            }
        }, pipeCancellation.Token);
        return new PresentationResponse(PipeProtocol.Version, true);
    }

    private async void OnActionRequested(object? sender, EventArgs args) =>
        await HandleActionRequestedAsync();

    private async Task HandleActionRequestedAsync()
    {
        if (DismissAmbientQuip())
        {
            return;
        }
        if (SnoozeNeedMessage(requirePresented: true))
        {
            return;
        }
        if (contextPressure.Presented is not null && await HandleContextActionAsync())
        {
            return;
        }
        if (attentionQueue.Current is not null)
        {
            await FocusNextSessionAsync();
            return;
        }
        if (await HandleContextActionAsync())
        {
            return;
        }
        ChangePresentation(() =>
        {
            if (controller!.Message is null)
            {
                controller.ShowMessage(DemoMessage);
            }
            else
            {
                controller.DismissMessage();
            }
        });
    }

    private void ChangePresentation(Action change)
    {
        AdvanceModel();
        change();
        if (SessionMessage is { } message && controller!.Message != message)
        {
            controller.ShowMessage(message);
        }
        UpdateBubble();
        if (buddyActive && !dragging && !releasingDrag && controller!.Snapshot.State != VisualState.Landing)
        {
            StartPassiveMotion();
        }
    }

    private void ChangeVisibility(bool visible)
    {
        if (controller!.IsVisible == visible)
        {
            return;
        }
        AdvanceModel();
        if (!visible)
        {
            CancelNativeInteraction();
            StopSessionLaunchJump();
            passiveTimer.Stop();
            sprite!.StopAnimation(nameof(sprite.Offset));
            sprite.StopAnimation(nameof(sprite.Scale));
        }
        controller.SetVisible(visible);
        sprite!.IsVisible = visible;
        if (sessionEffects is not null)
        {
            sessionEffects.IsVisible = visible;
        }
        modelTimestamp = Stopwatch.GetTimestamp();
        if (visible)
        {
            StartPassiveMotion();
        }
        UpdatePointerRouting();
    }

    private void TrackDragMovement(Point point)
    {
        dragMoved |= HasMovedForDrag(dragStart, point);
    }

    private bool HasMovedForDrag(Point start, Point current)
    {
        double horizontal = (current.X - start.X) / dpiScale;
        double vertical = (current.Y - start.Y) / dpiScale;
        return horizontal * horizontal + vertical * vertical >= 9;
    }

    private void CancelNativeInteraction()
    {
        attentionPressPending = false;
        StopAttentionBounce();
        dragging = false;
        releasingDrag = false;
        inputTimer.Stop();
        pointerPending = false;
        Capture = false;
        sprite!.StopAnimation(nameof(sprite.Offset));
        if (nativeDrag is not null)
        {
            nativeMovingRetargets += nativeDrag.MovingRetargets;
            nativeDrag.Dispose();
            nativeDrag = null;
        }
        if (controller!.Snapshot.State is VisualState.Airborne or VisualState.Landing)
        {
            controller.BeginDrag(controller.Snapshot.X, controller.Snapshot.HopOffset);
        }
        controller.CancelDrag();
        sprite.Offset = PositionOf(controller.Snapshot);
        RecordDragTrace("interaction-cancelled");
        SaveDragTrace();
    }

    private void StartAttention()
    {
        if (!buddyActive)
        {
            return;
        }
        Vector3 renderedPosition = CurrentRenderedBuddyOffset();
        controller!.PlaceAt(renderedPosition.X / dpiScale);
        sprite!.StopAnimation(nameof(sprite.Offset));
        sprite.Offset = PositionOf(controller.Snapshot);
        PresentationSnapshot current = controller.Snapshot;
        controller.ShowMessage(current.Message!);
        sprite!.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        ShowFrame(SpriteFrame.Wave1);
        StartAlternatingFrames(SpriteFrame.Wave1, SpriteFrame.Wave2,
            presentation.Attention.ReducedMotion ? presentation.Attention.ReducedMotionWaveFrameSeconds : presentation.Attention.WaveFrameSeconds);
        if (presentation.Attention.ReducedMotion || presentation.Attention.HopHeight == 0)
        {
            sprite.Offset = PositionOf(current);
            return;
        }
        StartAttentionBounce();
    }

    private void StartAttentionBounce()
    {
        if (!buddyActive)
        {
            return;
        }
        PresentationSnapshot current = controller!.Snapshot;
        PresentationController preview = new(presentation, new SystemRandomSource(0), current.X, current.X);
        preview.ShowMessage(current.Message!);
        using Vector3KeyFrameAnimation hop = compositor!.CreateVector3KeyFrameAnimation();
        using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
        hop.Duration = TimeSpan.FromSeconds(presentation.Attention.HopDurationSeconds);
        hop.IterationBehavior = AnimationIterationBehavior.Count;
        hop.IterationCount = 1;
        TimeSpan step = TimeSpan.FromSeconds(presentation.Attention.HopDurationSeconds / 60);
        for (int sample = 0; sample <= 60; sample++)
        {
            hop.InsertKeyFrame(sample / 60f, PositionOf(preview.Snapshot), easing);
            preview.Tick(step);
        }
        hop.InsertKeyFrame(1, new Vector3((float)current.X * dpiScale, (float)baseline * dpiScale, 0), easing);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        attentionBounce = batch;
        batch.Completed += (_, _) =>
        {
            if (!Disposing && !IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() => CompleteAttentionBounce(batch));
            }
        };
        sprite!.StartAnimation(nameof(sprite.Offset), hop);
        batch.End();
    }

    private void CompleteAttentionBounce(CompositionScopedBatch batch)
    {
        if (!ReferenceEquals(attentionBounce, batch))
        {
            return;
        }
        AdvanceModel();
        attentionBounce = null;
        batch.Dispose();
        if (controller!.Snapshot is { IsVisible: true, State: VisualState.Attention })
        {
            StartAttentionBounce();
        }
        else
        {
            StartPassiveMotion();
        }
        UpdateBubble();
    }

    private void StopAttentionBounce()
    {
        CompositionScopedBatch? batch = attentionBounce;
        attentionBounce = null;
        batch?.Dispose();
    }

    private void TrackAttentionPress(Point point)
    {
        if (!attentionPressPending)
        {
            return;
        }
        TrackDragMovement(point);
        if (dragMoved)
        {
            attentionPressPending = false;
            BeginPointerDrag(dragStart);
            dragMoved = true;
        }
    }

    private void UpdateBubble()
    {
        if (bubble is null)
        {
            return;
        }
        if (TaskbarMode && taskbarLayer?.IsVisible == false)
        {
            bubble.Hide();
            return;
        }
        PresentationSnapshot snapshot = controller!.Snapshot;
        bubble.IsInteractive = HasSessionAction || HasNeedMessageAction;
        string? message = snapshot.Message ?? ambientQuip;
        if (!snapshot.IsVisible || message is null)
        {
            bubble.Hide();
            return;
        }
        bool ambient = snapshot.Message is null;
        Vector3 position = (snapshot.State == VisualState.Attention || ambient) && bubblePosition is not null
            ? bubblePosition.Position
            : PositionOf(snapshot);
        Point anchor = PointToScreen(new Point((int)(position.X + spriteWidth / 2 * dpiScale),
            (int)(position.Y + spriteHeight * (1 - snapshot.ScaleY) * dpiScale)));
        bubble.Present(this, message, anchor, RectangleToScreen(ClientRectangle), dpiScale,
            ambient ? MessageBubbleStyle.Ambient : MessageBubbleStyle.Default);
    }

    private void OnBuddyLayerVisibilityChanged(object? sender, bool visible)
    {
        if (!visible)
        {
            bubble?.Hide();
            presentBubble?.Hide();
            return;
        }
        if (controller is not null)
        {
            UpdateBubble();
            UpdatePresentBubble();
        }
    }

    private sealed class BubblePositionObserver : IInteractionTrackerOwner, IDisposable
    {
        private readonly InteractionTracker tracker;
        private readonly object positionLock = new();
        private Vector3 position;

        public BubblePositionObserver(Visual visual)
        {
            position = visual.Offset;
            tracker = InteractionTracker.CreateWithOwner(visual.Compositor, this);
            tracker.MinPosition = new Vector3(-1_000_000);
            tracker.MaxPosition = new Vector3(1_000_000);
            tracker.TryUpdatePosition(position);
            using ExpressionAnimation following = visual.Compositor.CreateExpressionAnimation("sprite.Offset");
            following.SetReferenceParameter("sprite", visual);
            tracker.TryUpdatePositionWithAnimation(following);
        }

        public Vector3 Position
        {
            get
            {
                lock (positionLock)
                {
                    return position;
                }
            }
        }

        public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
        {
            lock (positionLock)
            {
                position = args.Position;
            }
        }

        public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }
        public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args) { }
        public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }
        public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args) { }
        public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }

        public void Dispose() => tracker.Dispose();
    }
}