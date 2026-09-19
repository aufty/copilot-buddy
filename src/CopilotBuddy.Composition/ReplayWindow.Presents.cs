using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private readonly System.Windows.Forms.Timer presentTimer = new() { Interval = 250 };
    private readonly Dictionary<string, StoredPresent> storedPresents = [];
    private ArtifactPresent? handoffPresent;
    private MessageBubble? presentBubble;
    private AssistantHandoff? pendingHandoff;
    private ArtifactPresent? pressedPresent;
    private ArtifactPresent? presentBubbleTarget;
    private bool completingHandoffSource;
    private bool openingHandoff;
    private bool handoffUnavailable;
    private bool presentPressPending;
    private bool presentDragging;
    private Point presentDragStart;
    private MouseButtons previousPresentButtons;

    private bool HasInteractivePresent =>
        handoffPresent is { IsOpened: false } ||
        storedPresents.Values.Any(entry => !entry.Present.IsOpened);

    private void StartPresentHost()
    {
        presentTimer.Tick += (_, _) => CheckPresentReadiness();
        presentTimer.Start();
    }

    private MessageBubble CreatePresentBubble()
    {
        MessageBubble bubble = new(presentation.Bubble) { IsInteractive = true };
        bubble.MouseClick += OnPresentBubbleClick;
        return bubble;
    }

    private void DropHandoffPresent(AssistantHandoff handoff)
    {
        ClearHandoffPresent();
        pendingHandoff = handoff;
        PresentationSnapshot snapshot = controller!.Snapshot;
        handoffPresent = new ArtifactPresent(
            compositor!,
            root!,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Sprites", "present.png"),
            handoff.ArtifactPath,
            handoff.SourceLabel,
            dpiScale,
            ClientSize,
            snapshot.X,
            spriteWidth,
            presentation.Attention.ReducedMotion);
        handoffPresent.Opened += OnPresentOpened;
        handoffPresent.MotionCompleted += OnPresentMotionCompleted;
        handoffPresent.SetPaused(!buddyActive);
        presentBubble ??= CreatePresentBubble();
        StartSessionLaunchJump();
        UpdatePointerRouting();
    }

    private void OnStoredSessionAdded(object? sender, AssistantStoredSession storedSession) =>
        PostSessionUpdate(() => PresentStoredSession(storedSession));

    private void OnStoredSessionRemoved(object? sender, string sessionId) =>
        PostSessionUpdate(() =>
        {
            deferredStoredPresents.Remove(sessionId);
            ClearStoredPresent(sessionId);
        });

    private void PresentStoredSession(AssistantStoredSession storedSession)
    {
        if (visitingHandoffWindow)
        {
            deferredStoredPresents[storedSession.SessionId] = storedSession;
            return;
        }
        DropStoredPresent(storedSession);
    }

    private void DropStoredPresent(AssistantStoredSession storedSession)
    {
        if (storedPresents.ContainsKey(storedSession.SessionId))
        {
            return;
        }
        PresentationSnapshot snapshot = controller!.Snapshot;
        ArtifactPresent present = new(
            compositor!,
            root!,
            Path.Combine(AppContext.BaseDirectory, "Assets", "Sprites", "present.png"),
            "",
            storedSession.Label,
            dpiScale,
            ClientSize,
            snapshot.X,
            spriteWidth,
            presentation.Attention.ReducedMotion,
            readyInitially: true);
        present.SetPaused(!buddyActive);
        present.Opened += OnPresentOpened;
        present.MotionCompleted += OnPresentMotionCompleted;
        storedPresents[storedSession.SessionId] = new(storedSession, present);
        presentBubble ??= CreatePresentBubble();
        StartSessionLaunchJump();
        UpdatePointerRouting();
    }

    private void CheckPresentReadiness()
    {
        if (!buddyActive || handoffPresent is null || !handoffPresent.CheckReady())
        {
            return;
        }
        UpdatePresentBubble();
        if (!completingHandoffSource)
        {
            completingHandoffSource = true;
            _ = CloseHandoffSourceWindowAsync();
        }
    }

    private void OnHandoffCompleted(object? sender, AssistantHandoff handoff) => PostSessionUpdate(() =>
    {
        if (pendingHandoff?.SourceSessionId != handoff.SourceSessionId || handoffPresent is null)
        {
            return;
        }
        handoffPresent.MarkProducerCompleted();
        CheckPresentReadiness();
    });

    private void OnHandoffSourceClosed(string sessionId)
    {
        if (pendingHandoff?.SourceSessionId != sessionId ||
            handoffPresent is not { IsReady: false, IsOpened: false })
        {
            return;
        }
        handoffUnavailable = true;
        presentPressPending = false;
        if (presentDragging)
        {
            presentDragging = false;
            inputTimer.Stop();
            pointerPending = false;
        }
        pressedPresent = null;
        Capture = false;
        presentBubble?.Hide();
        handoffPresent.PopOpenUnavailable(handoffPresent.BubbleAnchor);
        UpdatePointerRouting();
    }

    private async Task CloseHandoffSourceWindowAsync()
    {
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await assistantSessions.CloseHandoffSourceAsync(pendingHandoff!, timeout.Token);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError(
                $"The handoff is ready, but the previous session could not close: {exception.Message}");
        }
    }

    private void UpdatePresentBubble()
    {
        if (presentBubble is null)
        {
            return;
        }
        ArtifactPresent? previousTarget = presentBubbleTarget;
        Point pointer = PointToClient(Cursor.Position);
        StoredPresent? stored = storedPresents.Values.LastOrDefault(entry =>
            !entry.Present.IsOpened &&
            !entry.Present.IsMoving &&
            entry.Present.HitTest(pointer));
        if (stored is not null)
        {
            Point anchor = PointToScreen(stored.Present.BubbleAnchor);
            string label = string.IsNullOrWhiteSpace(stored.Session.Label)
                ? "Copilot session"
                : stored.Session.Label;
            presentBubbleTarget = stored.Present;
            presentBubble.Present(this, $"Stored session: {label}", anchor,
                Screen.FromPoint(anchor).WorkingArea, dpiScale);
            return;
        }
        if (previousTarget is not null &&
            presentBubble.Visible &&
            presentBubble.Bounds.Contains(Cursor.Position))
        {
            presentBubbleTarget = previousTarget;
            return;
        }
        if (handoffPresent is { IsReady: true, IsOpened: false, IsMoving: false })
        {
            Point anchor = PointToScreen(handoffPresent.BubbleAnchor);
            string label = string.IsNullOrWhiteSpace(handoffPresent.Label)
                ? "Previous session"
                : handoffPresent.Label;
            presentBubbleTarget = handoffPresent;
            presentBubble.Present(this, $"{label} handoff ready to open", anchor,
                Screen.FromPoint(anchor).WorkingArea, dpiScale);
            return;
        }
        presentBubbleTarget = null;
        presentBubble.Hide();
    }

    private void PollPresentPress(Point clientPoint)
    {
        MouseButtons buttons = Control.MouseButtons;
        bool leftDown = (buttons & MouseButtons.Left) != 0;
        bool leftWasDown = (previousPresentButtons & MouseButtons.Left) != 0;
        previousPresentButtons = buttons;
        if (leftDown && !leftWasDown && !presentPressPending && !presentDragging)
        {
            BeginPresentPress(clientPoint);
        }
        else if (leftDown && (presentPressPending || presentDragging))
        {
            TrackPresentPress(clientPoint);
        }
        else if (!leftDown && leftWasDown && (presentPressPending || presentDragging))
        {
            TrackPresentPress(clientPoint);
            EndPresentPress(clientPoint);
        }
    }

    private ArtifactPresent? FindPresent(Point clientPoint)
    {
        foreach (StoredPresent entry in storedPresents.Values.Reverse())
        {
            if (!entry.Present.IsOpened && entry.Present.HitTest(clientPoint))
            {
                return entry.Present;
            }
        }
        return handoffPresent is { IsOpened: false } && handoffPresent.HitTest(clientPoint)
            ? handoffPresent
            : null;
    }

    private bool HitPresent(Point clientPoint) => FindPresent(clientPoint) is not null;

    private void BeginPresentPress(Point clientPoint)
    {
        pressedPresent = FindPresent(clientPoint);
        if (pressedPresent is null)
        {
            return;
        }
        presentDragStart = clientPoint;
        presentPressPending = true;
        presentDragging = false;
        Capture = true;
    }

    private bool TrackPresentPress(Point clientPoint)
    {
        if (!presentPressPending && !presentDragging)
        {
            return false;
        }
        if (presentPressPending && HasMovedForDrag(presentDragStart, clientPoint))
        {
            presentPressPending = false;
            presentDragging = true;
            presentBubble?.Hide();
            pressedPresent?.BeginDrag(
                presentDragStart,
                presentation.Physics,
                directDrag,
                DispatchNativeDrag);
            inputTimer.Start();
        }
        if (presentDragging)
        {
            pendingPointer = clientPoint;
            pointerPending = true;
        }
        return true;
    }

    private bool EndPresentPress(Point clientPoint)
    {
        if (!presentPressPending && !presentDragging)
        {
            return false;
        }
        bool open = presentPressPending;
        ArtifactPresent? releasedPresent = pressedPresent;
        presentPressPending = false;
        if (presentDragging)
        {
            pendingPointer = clientPoint;
            pointerPending = true;
            FlushPointer();
            presentDragging = false;
            inputTimer.Stop();
            pressedPresent?.ReleaseDrag();
        }
        Capture = false;
        if (open && releasedPresent is { IsReady: true, IsOpened: false })
        {
            presentBubble?.Hide();
            releasedPresent.PopOpen(clientPoint);
        }
        pressedPresent = null;
        UpdatePointerRouting();
        return true;
    }

    private void CancelOrReleasePresentPress()
    {
        presentPressPending = false;
        if (presentDragging)
        {
            FlushPointer();
            presentDragging = false;
            inputTimer.Stop();
            pressedPresent?.ReleaseDrag();
        }
        pressedPresent = null;
    }

    private void OnPresentMotionCompleted(object? sender, EventArgs args)
    {
        UpdatePresentBubble();
        UpdatePointerRouting();
        if (placementPending)
        {
            placementPending = false;
            RefreshPlacement();
        }
    }

    private void OnPresentBubbleClick(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
        {
            return;
        }
        Point click = PointToClient(Cursor.Position);
        ArtifactPresent? present = presentBubbleTarget;
        if (present is not { IsReady: true, IsOpened: false })
        {
            return;
        }
        presentBubble?.Hide();
        presentBubbleTarget = null;
        present.PopOpen(click);
    }

    private async void OnPresentOpened(object? sender, EventArgs args)
    {
        if (sender is ArtifactPresent openedPresent)
        {
            StoredPresent? stored = storedPresents.Values.FirstOrDefault(
                entry => ReferenceEquals(entry.Present, openedPresent));
            if (stored is not null)
            {
                await OpenStoredPresentAsync(stored);
                return;
            }
        }
        if (openingHandoff || sessionsStopping)
        {
            return;
        }
        if (handoffUnavailable)
        {
            ClearHandoffPresent();
            if (placementPending)
            {
                placementPending = false;
                RefreshPlacement();
            }
            return;
        }
        if (pendingHandoff is null)
        {
            return;
        }
        openingHandoff = true;
        try
        {
            await assistantSessions.OpenHandoffAsync(pendingHandoff, sessionCancellation.Token);
            if (!sessionsStopping)
            {
                Sparkle();
                ClearHandoffPresent();
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not open the handoff: {exception.Message}");
            handoffPresent?.ResetAfterFailedOpen();
            UpdatePresentBubble();
        }
        finally
        {
            openingHandoff = false;
        }
    }

    private async Task OpenStoredPresentAsync(StoredPresent stored)
    {
        if (sessionsStopping)
        {
            return;
        }
        try
        {
            await assistantSessions.OpenStoredSessionAsync(stored.Session, sessionCancellation.Token);
            if (!sessionsStopping)
            {
                Sparkle();
                ClearStoredPresent(stored.Session.SessionId);
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not resume the stored session: {exception.Message}");
            stored.Present.ResetAfterFailedOpen();
            UpdatePresentBubble();
        }
    }

    private void RelayoutPresent()
    {
        handoffPresent?.Relayout(dpiScale, ClientSize);
        foreach (StoredPresent entry in storedPresents.Values)
        {
            entry.Present.Relayout(dpiScale, ClientSize);
        }
        UpdatePresentBubble();
    }

    private void ClearHandoffPresent()
    {
        ArtifactPresent? clearing = handoffPresent;
        if (clearing is not null)
        {
            clearing.Opened -= OnPresentOpened;
            clearing.MotionCompleted -= OnPresentMotionCompleted;
            clearing.Dispose();
            handoffPresent = null;
        }
        pendingHandoff = null;
        if (ReferenceEquals(presentBubbleTarget, clearing))
        {
            presentBubbleTarget = null;
        }
        completingHandoffSource = false;
        handoffUnavailable = false;
        if (ReferenceEquals(pressedPresent, clearing))
        {
            pressedPresent = null;
            presentPressPending = false;
            presentDragging = false;
            Capture = false;
            inputTimer.Stop();
        }
        DisposePresentBubbleIfUnused();
    }

    private void ClearStoredPresent(string sessionId)
    {
        if (!storedPresents.Remove(sessionId, out StoredPresent? entry))
        {
            return;
        }
        if (ReferenceEquals(pressedPresent, entry.Present))
        {
            pressedPresent = null;
            presentPressPending = false;
            presentDragging = false;
            Capture = false;
            inputTimer.Stop();
        }
        if (ReferenceEquals(presentBubbleTarget, entry.Present))
        {
            presentBubbleTarget = null;
            presentBubble?.Hide();
        }
        entry.Present.Opened -= OnPresentOpened;
        entry.Present.MotionCompleted -= OnPresentMotionCompleted;
        entry.Present.Dispose();
        DisposePresentBubbleIfUnused();
        UpdatePointerRouting();
    }

    private void DisposePresentBubbleIfUnused()
    {
        if (handoffPresent is not null || storedPresents.Count > 0 || presentBubble is null)
        {
            return;
        }
        presentBubble.MouseClick -= OnPresentBubbleClick;
        presentBubble.Dispose();
        presentBubble = null;
        presentBubbleTarget = null;
    }

    private void StopPresentHost()
    {
        presentTimer.Stop();
        presentTimer.Dispose();
        ClearHandoffPresent();
        foreach (string sessionId in storedPresents.Keys.ToArray())
        {
            ClearStoredPresent(sessionId);
        }
    }

    private sealed record StoredPresent(
        AssistantStoredSession Session,
        ArtifactPresent Present);
}
