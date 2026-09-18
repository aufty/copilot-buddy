using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBuddy.Core;
using Windows.UI.Composition;

namespace CopilotBuddy.Composition;

internal sealed partial class ReplayWindow
{
    private const int OpenSessionHotkeyId = 0x4242;
    private const int SkillMenuHotkeyId = 0x4243;
    private const int HandoffHotkeyId = 0x4244;
    private const int StoreHotkeyId = 0x4245;
    private const int InquireHotkeyId = 0x4246;
    private const int GatherHotkeyId = 0x4247;
    private const int TestHotkeyId = 0x42FF;
    private readonly IStoredAssistantSessions assistantSessions;
    private readonly SessionSettings sessionSettings;
    private readonly AttentionQueue attentionQueue = new();
    private readonly CancellationTokenSource sessionCancellation = new();
    private readonly PixelSkillMenu skillMenu = new();
    private ContainerVisual? sessionEffects;
    private ContainerVisual? queueOrbs;
    private readonly List<CompositionObject> orbResources = [];
    private readonly List<CompositionObject> sparkleResources = [];
    private readonly System.Windows.Forms.Timer sparkleTimer = new() { Interval = 850 };
    private readonly System.Windows.Forms.Timer focusWaitTimer = new() { Interval = 350 };
    private static readonly TimeSpan MinimumInquireWindowDwell = TimeSpan.FromMilliseconds(500);
    private CompositionScopedBatch? sessionLaunchJump;
    private CompositionScopedBatch? handoffWindowJump;
    private TaskCompletionSource<long>? handoffWindowArrival;
    private TaskCompletionSource<long>? handoffTaskbarArrival;
    private AssistantHandoff? deferredHandoffPresent;
    private bool visitingHandoffWindow;
    private bool endingHandoffWindowVisit;
    private bool restoringTaskbarAfterHandoffJump;
    private Rectangle handoffTaskbarBounds;
    private double handoffTaskbarTargetX;
    private bool openingSession;
    private bool focusingSession;
    private string? focusWaitMessage;
    private int focusWaitFrame;
    private bool startingHandoff;
    private bool gatheringWindows;
    private bool storingSession;
    private bool injectingInquire;
    private bool hotkeyRegistered;
    private bool skillMenuHotkeyRegistered;
    private bool handoffHotkeyRegistered;
    private bool storeHotkeyRegistered;
    private bool inquireHotkeyRegistered;
    private bool gatherHotkeyRegistered;
    private SparkleBorderWindow? sparkleBorder;
    private bool sessionsStopping;
    private ToolStripMenuItem? shortcutMenu;
    private bool SkillsBlocked => gatheringWindows;

    private void AddSessionMenu()
    {
        skillMenu.SummonRequested += async (_, _) => await SummonAsync();
        skillMenu.InquireRequested += async (_, _) => await InjectInquireAsync();
        skillMenu.HandoffRequested += async (_, _) => await StartHandoffAsync();
        skillMenu.GatherRequested += async (_, _) => await GatherWindowsAsync();
        skillMenu.StoreRequested += async (_, _) => await StoreSessionAsync();
        skillMenu.SettingsRequested += async (_, _) => await EditSettingsAsync();
        skillMenu.SupplyRequested += (_, args) => SelectSupply(args.Kind);
        skillMenu.VisibleChanged += (_, _) => UpdatePointerRouting();

        exitMenu.Items.Add("Summon", null, async (_, _) => await SummonAsync());
        shortcutMenu = new ToolStripMenuItem($"Summon shortcut: {sessionSettings.Shortcut}", null, (_, _) => EditShortcut());
        exitMenu.Items.Add(shortcutMenu);
        exitMenu.Items.Add(new ToolStripSeparator());
    }

    private void StartSessionIntegration()
    {
        sessionEffects = compositor!.CreateContainerVisual();
        queueOrbs = compositor.CreateContainerVisual();
        sessionEffects.Children.InsertAtTop(queueOrbs);
        root!.Children.InsertAtTop(sessionEffects);
        using ExpressionAnimation follow = compositor.CreateExpressionAnimation("buddy.Offset");
        follow.SetReferenceParameter("buddy", sprite!);
        sessionEffects.StartAnimation(nameof(sessionEffects.Offset), follow);
        sparkleTimer.Tick += (_, _) => ClearSparkle();
        focusWaitTimer.Tick += (_, _) =>
        {
            focusWaitFrame = (focusWaitFrame + 1) % 3;
            focusWaitMessage = new string('.', focusWaitFrame + 1);
            RefreshAttention();
        };
        assistantSessions.AttentionRequested += OnSessionAttention;
        assistantSessions.ContextUsageChanged += OnContextUsageChanged;
        assistantSessions.HandoffCompleted += OnHandoffCompleted;
        assistantSessions.StoredSessionAdded += OnStoredSessionAdded;
        assistantSessions.StoredSessionRemoved += OnStoredSessionRemoved;
        assistantSessions.SessionFocused += OnSessionFocused;
        assistantSessions.SessionClosed += OnSessionClosed;
        assistantSessions.ConnectionFailed += OnConnectionFailed;
        try
        {
            RegisterShortcut(sessionSettings.Shortcut);
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception)
        {
            BeginInvoke(() => ShowSessionError($"Shortcut unavailable: {exception.Message}"));
        }
        try
        {
            RegisterSkillHotkeys();
        }
        catch (Win32Exception exception)
        {
            BeginInvoke(() => ShowSessionError($"Skill shortcut unavailable: {exception.Message}"));
        }
        if (Environment.GetCommandLineArgs().Contains("--open-copilot", StringComparer.OrdinalIgnoreCase))
        {
            BeginInvoke(async () => await OpenSessionAsync());
        }
    }

    private void RegisterSkillHotkeys()
    {
        if (!RegisterHotKey(Handle, SkillMenuHotkeyId, 0x4001, (uint)Keys.Space))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Alt+Space is unavailable for the skills menu.");
        }
        skillMenuHotkeyRegistered = true;
        if (!RegisterHotKey(Handle, HandoffHotkeyId, 0x4005, (uint)Keys.H))
        {
            UnregisterHotKey(Handle, SkillMenuHotkeyId);
            skillMenuHotkeyRegistered = false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Alt+Shift+H is unavailable for Handoff.");
        }
        handoffHotkeyRegistered = true;
        if (!RegisterHotKey(Handle, StoreHotkeyId, 0x4005, (uint)Keys.S))
        {
            UnregisterHotKey(Handle, HandoffHotkeyId);
            UnregisterHotKey(Handle, SkillMenuHotkeyId);
            handoffHotkeyRegistered = false;
            skillMenuHotkeyRegistered = false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Alt+Shift+S is unavailable for Store.");
        }
        storeHotkeyRegistered = true;
        if (!RegisterHotKey(Handle, InquireHotkeyId, 0x4005, (uint)Keys.G))
        {
            UnregisterHotKey(Handle, StoreHotkeyId);
            UnregisterHotKey(Handle, HandoffHotkeyId);
            UnregisterHotKey(Handle, SkillMenuHotkeyId);
            storeHotkeyRegistered = false;
            handoffHotkeyRegistered = false;
            skillMenuHotkeyRegistered = false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Alt+Shift+G is unavailable for Inquire.");
        }
        inquireHotkeyRegistered = true;
        if (!RegisterHotKey(Handle, GatherHotkeyId, 0x4005, (uint)Keys.T))
        {
            UnregisterHotKey(Handle, InquireHotkeyId);
            UnregisterHotKey(Handle, StoreHotkeyId);
            UnregisterHotKey(Handle, HandoffHotkeyId);
            UnregisterHotKey(Handle, SkillMenuHotkeyId);
            inquireHotkeyRegistered = false;
            storeHotkeyRegistered = false;
            handoffHotkeyRegistered = false;
            skillMenuHotkeyRegistered = false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Alt+Shift+T is unavailable for Gather.");
        }
        gatherHotkeyRegistered = true;
    }

    private void ShowSkillMenu()
    {
        if (SkillsBlocked || controller is null || !controller.IsVisible)
        {
            return;
        }
        AdvanceModel();
        PresentationSnapshot snapshot = controller.Snapshot;
        Point buddyTopRight = PointToScreen(new Point(
            (int)Math.Round((snapshot.X + spriteWidth) * dpiScale),
            (int)Math.Round((baseline - snapshot.HopOffset) * dpiScale)));
        Size menuSize = new((int)Math.Round(560 * dpiScale), (int)Math.Round(454 * dpiScale));
        Rectangle area = Screen.FromPoint(buddyTopRight).WorkingArea;
        int x = buddyTopRight.X + (int)(8 * dpiScale);
        if (x + menuSize.Width > area.Right)
        {
            x = buddyTopRight.X - (int)Math.Round(spriteWidth * dpiScale) - menuSize.Width - (int)(8 * dpiScale);
        }
        int y = Math.Clamp(buddyTopRight.Y - menuSize.Height, area.Top, Math.Max(area.Top, area.Bottom - menuSize.Height));
        skillMenu.Present(this,
            new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - menuSize.Width)), y),
            dpiScale,
            sessionSettings.Shortcut);
    }

    private async Task InjectInquireAsync()
    {
        if (SkillsBlocked || injectingInquire || sessionsStopping)
        {
            return;
        }
        injectingInquire = true;
        skillMenu.Hide();
        Task<long>? windowArrival = null;
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            AssistantSessionTarget? target =
                await assistantSessions.CaptureSessionTargetAsync(timeout.Token);
            if (target is null)
            {
                ShowSessionError("Open and focus a Copilot CLI session managed by the buddy before using Inquire.");
                return;
            }
            if (target.WindowBounds is { Width: > 0, Height: > 0 } windowBounds)
            {
                windowArrival = BeginHandoffWindowVisit(windowBounds);
            }
            AssistantPromptInjection? injection =
                await assistantSessions.InjectPromptAsync(
                    target,
                    AssistantSkillPrompts.Grilling,
                    timeout.Token);
            if (injection is null)
            {
                ShowSessionError("Open and focus a Copilot CLI session managed by the buddy before using Inquire.");
                return;
            }
            Sparkle();
            if (injection.WindowBounds is { Width: > 0, Height: > 0 } bounds)
            {
                sparkleBorder?.Close();
                sparkleBorder?.Dispose();
                SparkleBorderWindow border = new(
                    new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
                sparkleBorder = border;
                border.FormClosed += (_, _) =>
                {
                    border.Dispose();
                    if (ReferenceEquals(sparkleBorder, border))
                    {
                        sparkleBorder = null;
                    }
                };
                border.Show();
            }
            if (windowArrival is not null)
            {
                long arrivedAt = await windowArrival.WaitAsync(sessionCancellation.Token);
                TimeSpan remainingDwell =
                    MinimumInquireWindowDwell -
                    System.Diagnostics.Stopwatch.GetElapsedTime(arrivedAt);
                if (remainingDwell > TimeSpan.Zero)
                {
                    await Task.Delay(remainingDwell, sessionCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not start Inquire: {exception.Message}");
        }
        finally
        {
            _ = EndHandoffWindowVisit();
            injectingInquire = false;
        }
    }

    private async Task StartHandoffAsync()
    {
        if (SkillsBlocked || startingHandoff || sessionsStopping)
        {
            return;
        }
        if (handoffPresent is not null)
        {
            skillMenu.Hide();
            ShowSessionError("Open the current handoff present before starting another handoff.");
            return;
        }
        AssistantSessionTarget? target;
        try
        {
            using CancellationTokenSource captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            captureTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            target = await assistantSessions.CaptureSessionTargetAsync(captureTimeout.Token);
        }

        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            skillMenu.Hide();
            ShowSessionError($"Could not identify the handoff session: {exception.Message}");
            return;
        }
        skillMenu.Hide();
        if (target is null)
        {
            ShowSessionError("Focus a Copilot CLI session opened by the buddy before starting Handoff.");
            return;
        }
        startingHandoff = true;
        try
        {
            if (target.WindowBounds is { Width: > 0, Height: > 0 } windowBounds)
            {
                _ = BeginHandoffWindowVisit(windowBounds);
            }
            HandoffRequest? request;
            try
            {
                request = HandoffQuestionDialog.Ask(this, dpiScale, target.WindowBounds);
            }
            finally
            {
                _ = EndHandoffWindowVisit();
            }
            if (request is null)
            {
                return;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            AssistantHandoff? handoff = await assistantSessions.StartHandoffAsync(target, request, timeout.Token);
            if (handoff is not null)
            {
                if (!sessionsStopping)
                {
                    if (visitingHandoffWindow)
                    {
                        deferredHandoffPresent = handoff;
                    }
                    else
                    {
                        DropHandoffPresent(handoff);
                    }
                }
            }

            else
            {
                ShowSessionError("Focus a Copilot CLI session opened by the buddy before starting Handoff.");
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not start Handoff: {exception.Message}");
        }
        finally
        {
            startingHandoff = false;
        }
    }

    private async Task StoreSessionAsync()
    {
        if (SkillsBlocked || storingSession || sessionsStopping)
        {
            return;
        }
        storingSession = true;
        skillMenu.Hide();
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            AssistantSessionTarget? target =
                await assistantSessions.CaptureSessionTargetAsync(timeout.Token);
            if (target is null)
            {
                ShowSessionError("Focus a Copilot CLI session opened by the buddy before using Store.");
                return;
            }
            AssistantStoredSession? storedSession =
                await assistantSessions.StoreSessionAsync(target, timeout.Token);
            if (storedSession is null)
            {
                ShowSessionError("The highlighted Copilot session could not be stored.");
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not store the session: {exception.Message}");
        }
        finally
        {
            storingSession = false;
        }
    }

    private async Task GatherWindowsAsync()
    {
        if (gatheringWindows || startingHandoff || storingSession || injectingInquire ||
            openingSession || visitingHandoffWindow || sessionsStopping)
        {
            return;
        }
        gatheringWindows = true;
        skillMenu.Hide();
        try
        {
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            AssistantWindowBounds workArea = new(area.Left, area.Top, area.Width, area.Height);
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            AssistantGatherResult gathered = await assistantSessions.GatherAsync(workArea, timeout.Token);
            if (gathered.Windows.Length == 0)
            {
                ShowSessionError("Open a Copilot CLI window with the buddy before using Gather.");
                return;
            }
            Sparkle();
            Task<long>? arrival = BeginHandoffWindowVisit(gathered.Windows[0]);
            if (arrival is not null)
            {
                await arrival.WaitAsync(sessionCancellation.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(250), sessionCancellation.Token);
                foreach (AssistantWindowBounds window in gathered.Windows.Skip(1))
                {
                    arrival = ContinueHandoffWindowVisit(window);
                    if (arrival is null)
                    {
                        break;
                    }
                    await arrival.WaitAsync(sessionCancellation.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), sessionCancellation.Token);
                }
                Task<long>? taskbarArrival = EndHandoffWindowVisit();
                if (taskbarArrival is not null)
                {
                    await taskbarArrival.WaitAsync(sessionCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not gather Copilot windows: {exception.Message}");
        }
        finally
        {
            if (visitingHandoffWindow)
            {
                Task<long>? taskbarArrival = EndHandoffWindowVisit();
                if (taskbarArrival is not null && !sessionCancellation.IsCancellationRequested)
                {
                    try
                    {
                        await taskbarArrival.WaitAsync(sessionCancellation.Token);
                    }
                    catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
                }
            }
            gatheringWindows = false;
        }
    }

    private void OnConnectionFailed(object? sender, string message) => PostSessionUpdate(() => ShowSessionError(message));

    private void OnSessionAttention(object? sender, SessionAttention attention) => PostSessionUpdate(() =>
    {
        attentionQueue.Enqueue(attention);
        RefreshAttention();
    });

    private void OnSessionFocused(object? sender, string sessionId) => PostSessionUpdate(() =>
    {
        if (attentionQueue.Remove(sessionId))
        {
            RefreshAttention();
        }
    });

    private void OnSessionClosed(object? sender, string sessionId) => PostSessionUpdate(() =>
    {
        ChangeContextPressure(() => contextPressure.Remove(sessionId));
        attentionQueue.Remove(sessionId);
        RefreshAttention();
        OnHandoffSourceClosed(sessionId);
    });

    private void PostSessionUpdate(Action action)
    {
        if (sessionsStopping || IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                if (!sessionsStopping)
                {
                    action();
                }
            });
        }
        catch (InvalidOperationException) when (sessionsStopping || IsDisposed) { }
    }

    private void RefreshAttention()
    {
        bool pauseSupply = HasSessionAction;
        if (pauseSupply)
        {
            UpdateSupplyAlertPause(true);
        }
        string? message = SessionMessage;
        if (controller!.Message != message)
        {
            ChangePresentation(() =>
            {
                if (message is null) controller.DismissMessage();
                else controller.ShowMessage(message);
            });
        }
        UpdateQueueOrbs();
        if (!pauseSupply)
        {
            UpdateSupplyAlertPause(false);
        }
        if (!buddyActive)
        {
            SetBuddyAnimationsPaused(true);
        }
    }

    private async Task OpenSessionAsync(bool celebrate = false)
    {
        if (SkillsBlocked || openingSession || sessionsStopping)
        {
            return;
        }
        openingSession = true;
        if (celebrate)
        {
            StartSessionLaunchJump();
        }
        try
        {
            await assistantSessions.OpenAsync(sessionSettings.WorkingDirectory ?? Environment.CurrentDirectory, sessionCancellation.Token);
            if (!sessionsStopping)
            {
                Sparkle();
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not open Copilot: {exception.Message}");
        }
        finally
        {
            openingSession = false;
        }
    }

    private async Task SummonAsync()
    {
        if (SkillsBlocked)
        {
            return;
        }
        skillMenu.Hide();
        if (HasSessionAction)
        {
            await HandleActionRequestedAsync();
            return;
        }
        await OpenSessionAsync(celebrate: true);
    }

    private void StartSessionLaunchJump()
    {
        if (seated)
        {
            BeginChairHop(entering: false, StartSessionLaunchJump);
            return;
        }
        if (!buddyActive || controller is null || sprite is null || !controller.IsVisible || dragging || releasingDrag ||
            controller.Snapshot.State is VisualState.Airborne or VisualState.Landing)
        {
            return;
        }

        InterruptSupplyMotionForSkill();
        AdvanceModel();
        controller.ResetWandering();
        StopAttentionBounce();
        StopSessionLaunchJump();
        passiveTimer.Stop();
        sprite.StopAnimation(nameof(sprite.Offset));
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        ShowFrame(SpriteFrame.Wave1);

        PresentationSnapshot current = controller.Snapshot;
        Vector3 baselinePosition = PositionOf(current with { HopOffset = 0 });
        float jumpHeight = (float)(presentation.Attention.ReducedMotion ? 0 : 28 * dpiScale);
        using Vector3KeyFrameAnimation jump = compositor!.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction launch = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1));
        using CubicBezierEasingFunction land = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.6f, 0), new Vector2(0.8f, 0.2f));
        jump.Duration = TimeSpan.FromMilliseconds(presentation.Attention.ReducedMotion ? 450 : 700);
        jump.InsertKeyFrame(0, baselinePosition);
        jump.InsertKeyFrame(0.42f, baselinePosition - new Vector3(0, jumpHeight, 0), launch);
        jump.InsertKeyFrame(0.58f, baselinePosition - new Vector3(0, jumpHeight, 0));
        jump.InsertKeyFrame(1, baselinePosition, land);

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        sessionLaunchJump = batch;
        batch.Completed += (_, _) =>
        {
            if (!Disposing && !IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() => CompleteSessionLaunchJump(batch));
            }
        };
        sprite.StartAnimation(nameof(sprite.Offset), jump);
        batch.End();
    }

    private void CompleteSessionLaunchJump(CompositionScopedBatch batch)
    {
        if (!ReferenceEquals(sessionLaunchJump, batch))
        {
            return;
        }

        sessionLaunchJump = null;
        batch.Dispose();
        modelTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        sprite!.Offset = PositionOf(controller!.Snapshot);
        StartPassiveMotion();
        UpdateBubble();
    }

    private void StopSessionLaunchJump()
    {
        CompositionScopedBatch? batch = sessionLaunchJump;
        sessionLaunchJump = null;
        batch?.Dispose();
    }

    private Task<long>? BeginHandoffWindowVisit(AssistantWindowBounds windowBounds)
    {
        if (seated)
        {
            return BeginHandoffWindowVisitAfterChairHop(windowBounds);
        }
        if (!TaskbarMode || controller is null || sprite is null || compositor is null ||
            dragging || releasingDrag || visitingHandoffWindow)
        {
            return null;
        }

        AdvanceModel();
        InterruptSupplyMotionForSkill();
        StopAttentionBounce();
        StopSessionLaunchJump();
        StopHandoffWindowJump();
        passiveTimer.Stop();
        sprite.StopAnimation(nameof(sprite.Offset));
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        Vector3 start = PositionOf(controller.Snapshot);
        Point startScreen = PointToScreen(new Point((int)Math.Round(start.X), (int)Math.Round(start.Y)));

        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Rectangle target = Rectangle.Intersect(
            new Rectangle(windowBounds.Left, windowBounds.Top, windowBounds.Width, windowBounds.Height),
            virtualScreen);
        if (target.Width <= 0 || target.Height <= 0)
        {
            StartPassiveMotion();
            return null;
        }

        visitingHandoffWindow = true;
        endingHandoffWindowVisit = false;
        restoringTaskbarAfterHandoffJump = false;
        handoffTaskbarArrival = null;
        handoffWindowArrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repositioning = true;
        try
        {
            Bounds = virtualScreen;
        }
        finally
        {
            repositioning = false;
        }

        double spriteWidthPixels = spriteWidth * dpiScale;
        double spriteHeightPixels = spriteHeight * dpiScale;
        double minimumX = Math.Clamp((target.Left - virtualScreen.Left) / dpiScale,
            0, Math.Max(0, ClientSize.Width / dpiScale - spriteWidth));
        double maximumX = Math.Clamp((target.Right - virtualScreen.Left - spriteWidthPixels) / dpiScale,
            minimumX, Math.Max(minimumX, ClientSize.Width / dpiScale - spriteWidth));
        double targetX = minimumX + (maximumX - minimumX) / 2;
        baseline = Math.Clamp((target.Bottom - virtualScreen.Top - spriteHeightPixels) / dpiScale,
            0, Math.Max(0, ClientSize.Height / dpiScale - spriteHeight));
        controller.SetMaximumLift(Math.Max(0, baseline));
        controller.SetHorizontalBounds(targetX, targetX);
        controller.ResetWandering();
        controller.SetHorizontalBounds(minimumX, maximumX);

        Vector3 localStart = new(startScreen.X - virtualScreen.Left, startScreen.Y - virtualScreen.Top, 0);
        Vector3 destination = PositionOf(controller.Snapshot with { HopOffset = 0 });
        StartHandoffWindowJump(localStart, destination);
        return handoffWindowArrival.Task;
    }

    private Task<long>? ContinueHandoffWindowVisit(AssistantWindowBounds windowBounds)
    {
        if (!visitingHandoffWindow || restoringTaskbarAfterHandoffJump || handoffWindowJump is not null ||
            controller is null || sprite is null || compositor is null)
        {
            return null;
        }

        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Rectangle target = Rectangle.Intersect(
            new Rectangle(windowBounds.Left, windowBounds.Top, windowBounds.Width, windowBounds.Height),
            virtualScreen);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return null;
        }

        Vector3 start = CurrentSpriteOffset();
        double spriteWidthPixels = spriteWidth * dpiScale;
        double spriteHeightPixels = spriteHeight * dpiScale;
        double minimumX = Math.Clamp((target.Left - virtualScreen.Left) / dpiScale,
            0, Math.Max(0, ClientSize.Width / dpiScale - spriteWidth));
        double maximumX = Math.Clamp((target.Right - virtualScreen.Left - spriteWidthPixels) / dpiScale,
            minimumX, Math.Max(minimumX, ClientSize.Width / dpiScale - spriteWidth));
        double targetX = minimumX + (maximumX - minimumX) / 2;
        baseline = Math.Clamp((target.Bottom - virtualScreen.Top - spriteHeightPixels) / dpiScale,
            0, Math.Max(0, ClientSize.Height / dpiScale - spriteHeight));
        controller.SetMaximumLift(Math.Max(0, baseline));
        controller.SetHorizontalBounds(targetX, targetX);
        controller.ResetWandering();
        controller.SetHorizontalBounds(minimumX, maximumX);

        handoffWindowArrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartHandoffWindowJump(start, PositionOf(controller.Snapshot with { HopOffset = 0 }));
        return handoffWindowArrival.Task;
    }

    private Task<long> BeginHandoffWindowVisitAfterChairHop(AssistantWindowBounds windowBounds)
    {
        TaskCompletionSource<long> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginChairHop(entering: false, async () =>
        {
            try
            {
                Task<long>? visit = BeginHandoffWindowVisit(windowBounds);
                completion.SetResult(visit is null
                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                    : await visit);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    private Task<long>? EndHandoffWindowVisit()
    {
        if (!visitingHandoffWindow || controller is null || sprite is null || compositor is null)
        {
            return null;
        }

        handoffTaskbarArrival ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (handoffWindowJump is not null && !restoringTaskbarAfterHandoffJump)
        {
            endingHandoffWindowVisit = true;
            return handoffTaskbarArrival.Task;
        }

        StopHandoffWindowJump();
        Vector3 current = CurrentSpriteOffset();
        sprite.StopAnimation(nameof(sprite.Offset));
        sprite.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        Point currentScreen = PointToScreen(new Point((int)Math.Round(current.X), (int)Math.Round(current.Y)));
        handoffTaskbarBounds = TaskbarPlacement.GetPrimaryOverlayBounds();
        double maximumX = Math.Max(0, handoffTaskbarBounds.Width / dpiScale - spriteWidth);
        handoffTaskbarTargetX = Math.Clamp(
            (currentScreen.X - handoffTaskbarBounds.Left) / dpiScale, 0, maximumX);
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Vector3 localStart = new(currentScreen.X - virtualScreen.Left, currentScreen.Y - virtualScreen.Top, 0);
        Vector3 destination = new(
            (float)(handoffTaskbarBounds.Left - virtualScreen.Left + handoffTaskbarTargetX * dpiScale),
            (float)(handoffTaskbarBounds.Bottom - virtualScreen.Top - spriteHeight * dpiScale),
            0);
        restoringTaskbarAfterHandoffJump = true;
        StartHandoffWindowJump(localStart, destination);
        return handoffTaskbarArrival.Task;
    }

    private void StartHandoffWindowJump(Vector3 start, Vector3 destination)
    {
        sprite!.Offset = start;
        sprite.Scale = Vector3.One;
        ShowFrame(SpriteFrame.Standing);
        float distance = Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(destination.X, destination.Y));
        float arc = presentation.Attention.ReducedMotion
            ? 0
            : Math.Clamp(distance * 0.22f, 56 * dpiScale, 160 * dpiScale);
        using Vector3KeyFrameAnimation jump = compositor!.CreateVector3KeyFrameAnimation();
        using Vector3KeyFrameAnimation deformation = compositor.CreateVector3KeyFrameAnimation();
        using CubicBezierEasingFunction takeoff = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1));
        using CubicBezierEasingFunction landing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.6f, 0), new Vector2(0.8f, 0.2f));
        using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
        jump.Duration = deformation.Duration =
            TimeSpan.FromMilliseconds(presentation.Attention.ReducedMotion ? 250 : 950);
        jump.InsertKeyFrame(0, start);
        if (presentation.Attention.ReducedMotion)
        {
            jump.InsertKeyFrame(1, destination);
            deformation.InsertKeyFrame(0, Vector3.One);
            deformation.InsertKeyFrame(1, Vector3.One);
        }
        else
        {
            const float takeoffProgress = 0.16f;
            const float landingProgress = 0.86f;
            const int flightSamples = 60;
            jump.InsertKeyFrame(takeoffProgress, start);
            float peakY = Math.Min(start.Y, destination.Y) - arc;
            Vector3 firstControl = new(destination.X, peakY, 0);
            Vector3 secondControl = new(destination.X, peakY, 0);
            for (int sample = 1; sample <= flightSamples; sample++)
            {
                float flight = (float)sample / flightSamples;
                float progress = takeoffProgress + (landingProgress - takeoffProgress) * flight;
                float inverse = 1 - flight;
                Vector3 position =
                    inverse * inverse * inverse * start +
                    3 * inverse * inverse * flight * firstControl +
                    3 * inverse * flight * flight * secondControl +
                    flight * flight * flight * destination;
                jump.InsertKeyFrame(progress, position, linear);
            }
            jump.InsertKeyFrame(1, destination);

            deformation.InsertKeyFrame(0, Vector3.One);
            deformation.InsertKeyFrame(0.1f, new Vector3(1.18f, 0.72f, 1), takeoff);
            deformation.InsertKeyFrame(takeoffProgress, new Vector3(0.9f, 1.12f, 1), takeoff);
            deformation.InsertKeyFrame(0.72f, new Vector3(0.96f, 1.05f, 1));
            deformation.InsertKeyFrame(landingProgress, new Vector3(1.2f, 0.7f, 1), landing);
            deformation.InsertKeyFrame(1, Vector3.One, takeoff);
        }

        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        handoffWindowJump = batch;
        batch.Completed += (_, _) =>
        {
            if (!Disposing && !IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() => CompleteHandoffWindowJump(batch));
            }
        };
        sprite.StartAnimation(nameof(sprite.Offset), jump);
        sprite.StartAnimation(nameof(sprite.Scale), deformation);
        batch.End();
    }

    private void CompleteHandoffWindowJump(CompositionScopedBatch batch)
    {
        if (!ReferenceEquals(handoffWindowJump, batch))
        {
            return;
        }

        handoffWindowJump = null;
        batch.Dispose();
        modelTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!restoringTaskbarAfterHandoffJump)
        {
            handoffWindowArrival?.TrySetResult(modelTimestamp);
        }
        if (endingHandoffWindowVisit && !restoringTaskbarAfterHandoffJump)
        {
            endingHandoffWindowVisit = false;
            EndHandoffWindowVisit();
            return;
        }
        if (restoringTaskbarAfterHandoffJump)
        {
            restoringTaskbarAfterHandoffJump = false;
            repositioning = true;
            try
            {
                Bounds = handoffTaskbarBounds;
                baseline = ClientSize.Height / dpiScale - spriteHeight;
                taskbarLayer?.Reattach();
                RelayoutSupply();
            }
            finally
            {
                repositioning = false;
            }
            double maximumX = Math.Max(0, ClientSize.Width / dpiScale - spriteWidth);
            controller!.SetMaximumLift(Math.Max(0, baseline));
            controller.SetHorizontalBounds(handoffTaskbarTargetX, handoffTaskbarTargetX);
            controller.ResetWandering();
            controller.SetHorizontalBounds(0, maximumX);
            visitingHandoffWindow = false;
            endingHandoffWindowVisit = false;
            handoffWindowArrival = null;
            handoffTaskbarArrival?.TrySetResult(modelTimestamp);
            handoffTaskbarArrival = null;
        }
        sprite!.Offset = PositionOf(controller!.Snapshot);
        sprite.Scale = Vector3.One;
        StartPassiveMotion();
        UpdateBubble();
        if (placementPending && !visitingHandoffWindow)
        {
            placementPending = false;
            QueuePlacementRefresh();
        }
        if (deferredHandoffPresent is { } handoff)
        {
            deferredHandoffPresent = null;
            if (!sessionsStopping)
            {
                DropHandoffPresent(handoff);
            }
        }
    }

    private void StopHandoffWindowJump()
    {
        CompositionScopedBatch? batch = handoffWindowJump;
        handoffWindowJump = null;
        batch?.Dispose();
    }

    private Vector3 CurrentSpriteOffset() =>
        sprite!.Properties.TryGetVector3(nameof(sprite.Offset), out Vector3 offset) ==
            CompositionGetValueStatus.Succeeded
            ? offset
            : sprite.Offset;

    private Vector3 CurrentRenderedBuddyOffset() =>
        bubblePosition?.Position ?? CurrentSpriteOffset();

    private async Task FocusNextSessionAsync()
    {
        if (attentionQueue.Current is not { } attention)
        {
            return;
        }
        await FocusSessionAsync(attention.SessionId, () =>
        {
            attentionQueue.Acknowledge(attention);
            RefreshAttention();
        });
    }

    private async Task FocusSessionAsync(string sessionId, Action acknowledge)
    {
        if (focusingSession)
        {
            return;
        }
        focusingSession = true;
        focusWaitFrame = 0;
        focusWaitMessage = ".";
        focusWaitTimer.Start();
        RefreshAttention();
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!timeout.IsCancellationRequested)
            {
                if (await assistantSessions.FocusAsync(sessionId, timeout.Token))
                {
                    if (sessionsStopping) return;
                    acknowledge();
                    return;
                }
                await Task.Delay(250, timeout.Token);
            }
            ShowSessionError("Could not focus that CLI window. The alert remains queued.");
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            ShowSessionError("Could not focus that CLI window. The alert remains queued.");
        }
        catch (Exception exception)
        {
            ShowSessionError($"Could not focus the session: {exception.Message}");
        }
        finally
        {
            focusWaitTimer.Stop();
            focusWaitMessage = null;
            focusingSession = false;
            if (!sessionsStopping)
            {
                RefreshAttention();
            }
        }
    }

    private void ShowSessionError(string message)
    {
        if (sessionsStopping)
        {
            return;
        }
        if (HasSessionAction)
        {
            trayIcon?.ShowBalloonTip(5000, "Copilot Buddy", message, ToolTipIcon.Warning);
        }
        else
        {
            ChangePresentation(() => controller!.ShowMessage(message));
        }
    }

    private void RegisterShortcut(string shortcut)
    {
        Keys keys = (Keys)(new KeysConverter().ConvertFromInvariantString(shortcut)
            ?? throw new ArgumentException("Choose a modifier and a key."));
        Keys key = keys & Keys.KeyCode;
        uint modifiers = 0x4000;
        if ((keys & Keys.Alt) != 0) modifiers |= 0x1;
        if ((keys & Keys.Control) != 0) modifiers |= 0x2;
        if ((keys & Keys.Shift) != 0) modifiers |= 0x4;
        if (modifiers == 0x4000 || key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
        {
            throw new ArgumentException("Choose a key with Alt, Ctrl, or Shift.");
        }
        if (!RegisterHotKey(Handle, TestHotkeyId, modifiers, (uint)key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        UnregisterHotKey(Handle, TestHotkeyId);
        if (hotkeyRegistered) UnregisterHotKey(Handle, OpenSessionHotkeyId);
        if (!RegisterHotKey(Handle, OpenSessionHotkeyId, modifiers, (uint)key))
        {
            hotkeyRegistered = false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        hotkeyRegistered = true;
    }

    private void EditShortcut()
    {
        using Form dialog = new()
        {
            Text = "Summon shortcut", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(330, 115),
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        TextBox input = new() { ReadOnly = true, Text = sessionSettings.Shortcut, Bounds = new Rectangle(16, 16, 298, 28) };
        Button save = new() { Text = "Save", Bounds = new Rectangle(150, 65, 78, 30) };
        Button cancel = new() { Text = "Cancel", Bounds = new Rectangle(236, 65, 78, 30), DialogResult = DialogResult.Cancel };
        input.KeyDown += (_, args) =>
        {
            args.SuppressKeyPress = true;
            if (args.KeyCode is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu))
            {
                input.Text = new KeysConverter().ConvertToInvariantString(args.KeyData);
            }
        };
        save.Click += (_, _) =>
        {
            try
            {
                RegisterShortcut(input.Text);
                sessionSettings.Shortcut = input.Text;
                sessionSettings.Save();
                shortcutMenu!.Text = $"Summon shortcut: {input.Text}";
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or Win32Exception or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(dialog, exception.Message, "Shortcut unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        dialog.Controls.AddRange([input, save, cancel]);
        dialog.CancelButton = cancel;
        if (hotkeyRegistered)
        {
            UnregisterHotKey(Handle, OpenSessionHotkeyId);
            hotkeyRegistered = false;
        }
        dialog.ShowDialog(this);
        if (!hotkeyRegistered)
        {
            try { RegisterShortcut(sessionSettings.Shortcut); }
            catch (Win32Exception exception) { ShowSessionError(exception.Message); }
        }
    }

    private async Task EditSettingsAsync()
    {
        if (SkillsBlocked)
        {
            return;
        }
        skillMenu.Hide();
        AssistantLaunchCommand previousLaunch = sessionSettings.EffectiveCopilotLaunch;
        string previousBuddy = BuddySpriteCatalog.Resolve(sessionSettings.Buddy).Name;
        SettingsDialogResult? updated;
        try
        {
            updated = CopilotSettingsDialog.Edit(this, dpiScale, previousLaunch, previousBuddy);
        }
        catch (InvalidOperationException exception)
        {
            ShowSessionError(exception.Message);
            return;
        }
        if (updated is null)
        {
            return;
        }
        bool launchChanged =
            !string.Equals(updated.CopilotLaunch.Executable, previousLaunch.Executable, StringComparison.Ordinal) ||
            !updated.CopilotLaunch.Arguments.SequenceEqual(previousLaunch.Arguments, StringComparer.Ordinal);
        if (launchChanged && assistantSessions is not IConfigurableAssistantSessions)
        {
            ShowSessionError("This assistant provider does not support launch settings.");
            return;
        }

        try
        {
            if (launchChanged)
            {
                using CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ((IConfigurableAssistantSessions)assistantSessions)
                    .ConfigureLaunchAsync(updated.CopilotLaunch, timeout.Token);
            }
            sessionSettings.CopilotLaunch = updated.CopilotLaunch;
            sessionSettings.CliPath = null;
            sessionSettings.Buddy = updated.Buddy;
            sessionSettings.Save();
            if (!string.Equals(updated.Buddy, previousBuddy, StringComparison.OrdinalIgnoreCase))
            {
                LoadSpriteFrames();
                ShowFrame(controller!.Snapshot.Frame);
                UpdateContextSweat();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            if (launchChanged && assistantSessions is IConfigurableAssistantSessions configurable)
            {
                try
                {
                    using CancellationTokenSource rollback = new(TimeSpan.FromSeconds(2));
                    await configurable.ConfigureLaunchAsync(previousLaunch, rollback.Token);
                }
                catch (Exception rollbackException)
                {
                    System.Diagnostics.Debug.WriteLine(rollbackException);
                }
            }
            sessionSettings.CopilotLaunch = previousLaunch;
            sessionSettings.Buddy = previousBuddy;
            ShowSessionError($"Could not save settings: {exception.Message}");
        }
    }

    private void UpdateQueueOrbs()
    {
        if (queueOrbs is null)
        {
            return;
        }
        queueOrbs.Children.RemoveAll();
        foreach (CompositionObject resource in orbResources) resource.Dispose();
        orbResources.Clear();
        int extra = Math.Max(0, attentionQueue.Count - 1);
        for (int index = 0; index < extra; index++)
        {
            float diameter = 6 * dpiScale;
            ShapeVisual orb = compositor!.CreateShapeVisual();
            orb.Size = new Vector2(diameter);
            CompositionEllipseGeometry circle = compositor.CreateEllipseGeometry();
            circle.Center = new Vector2(diameter / 2);
            circle.Radius = new Vector2(diameter / 2);
            CompositionSpriteShape shape = compositor.CreateSpriteShape(circle);
            CompositionColorBrush brush = compositor.CreateColorBrush(index % 2 == 0
                ? Windows.UI.Color.FromArgb(255, 66, 196, 206) : Windows.UI.Color.FromArgb(255, 255, 203, 77));
            shape.FillBrush = brush;
            orb.Shapes.Add(shape);
            queueOrbs.Children.InsertAtTop(orb);
            orbResources.AddRange([orb, shape, circle, brush]);
            float column = index % 5;
            float row = index / 5;
            CompositionPropertySet motion = compositor.CreatePropertySet();
            motion.InsertScalar("Bob", 0);
            orbResources.Add(motion);
            using ExpressionAnimation offset = compositor.CreateExpressionAnimation(
                "Vector3(buddy.Offset.X + buddy.Size.X + distance + size <= width ? buddy.Size.X + distance : -distance - size, Max(-buddy.Offset.Y, baseY + motion.Bob), 0)");
            offset.SetReferenceParameter("buddy", sprite!);
            offset.SetReferenceParameter("motion", motion);
            offset.SetScalarParameter("distance", (8 + column * 10) * dpiScale);
            offset.SetScalarParameter("baseY", ((float)spriteHeight / 2 - row * 10) * dpiScale);
            offset.SetScalarParameter("width", ClientSize.Width);
            offset.SetScalarParameter("size", diameter);
            orb.StartAnimation(nameof(orb.Offset), offset);
            using ScalarKeyFrameAnimation bob = compositor.CreateScalarKeyFrameAnimation();
            bob.Duration = TimeSpan.FromSeconds(1.4 + index * 0.07);
            bob.IterationBehavior = AnimationIterationBehavior.Forever;
            using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
            for (int sample = 0; sample <= 30; sample++)
            {
                float lift = presentation.Attention.ReducedMotion ? 0 : (float)Math.Sin(sample * Math.Tau / 30 + index) * dpiScale;
                bob.InsertKeyFrame(sample / 30f, lift, easing);
            }
            motion.StartAnimation("Bob", bob);
        }
    }

    private void Sparkle()
    {
        if (!buddyActive || sessionEffects is null || !controller!.IsVisible)
        {
            return;
        }
        ClearSparkle();
        for (int index = 0; index < 8; index++)
        {
            ContainerVisual spark = compositor!.CreateContainerVisual();
            float size = 7 * dpiScale;
            spark.Size = new Vector2(size);
            spark.CenterPoint = new Vector3(size / 2, size / 2, 0);
            CompositionColorBrush brush = compositor.CreateColorBrush(index % 2 == 0
                ? Windows.UI.Color.FromArgb(255, 255, 221, 104) : Windows.UI.Color.FromArgb(255, 247, 255, 255));
            SpriteVisual horizontal = compositor.CreateSpriteVisual();
            horizontal.Size = new Vector2(size, dpiScale);
            horizontal.Offset = new Vector3(0, 3 * dpiScale, 0);
            horizontal.Brush = brush;
            SpriteVisual vertical = compositor.CreateSpriteVisual();
            vertical.Size = new Vector2(dpiScale, size);
            vertical.Offset = new Vector3(3 * dpiScale, 0, 0);
            vertical.Brush = brush;
            spark.Children.InsertAtTop(horizontal);
            spark.Children.InsertAtTop(vertical);
            sessionEffects.Children.InsertAtTop(spark);
            sparkleResources.AddRange([spark, horizontal, vertical, brush]);
            double angle = index * Math.Tau / 8;
            Vector3 center = new((float)spriteWidth * dpiScale / 2, (float)spriteHeight * dpiScale / 2, 0);
            Vector3 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
            using Vector3KeyFrameAnimation movement = compositor.CreateVector3KeyFrameAnimation();
            movement.Duration = TimeSpan.FromMilliseconds(800);
            movement.InsertKeyFrame(0, center + direction * 18 * dpiScale);
            movement.InsertKeyFrame(1, center + direction * (presentation.Attention.ReducedMotion ? 18 : 30) * dpiScale);
            spark.StartAnimation(nameof(spark.Offset), movement);
            using ScalarKeyFrameAnimation opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Duration = movement.Duration;
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(0.15f, 1);
            opacity.InsertKeyFrame(1, 0);
            spark.StartAnimation(nameof(spark.Opacity), opacity);
        }
        sparkleTimer.Start();
    }

    private void ClearSparkle()
    {
        sparkleTimer.Stop();
        foreach (CompositionObject resource in sparkleResources)
        {
            if (resource is ContainerVisual visual && visual.Parent == sessionEffects)
            {
                sessionEffects!.Children.Remove(visual);
            }
        }
        foreach (CompositionObject resource in sparkleResources)
        {
            resource.Dispose();
        }
        sparkleResources.Clear();
    }

    private void StopSessionIntegration()
    {
        sessionsStopping = true;
        deferredHandoffPresent = null;
        StopSessionLaunchJump();
        if (hotkeyRegistered) UnregisterHotKey(Handle, OpenSessionHotkeyId);
        if (skillMenuHotkeyRegistered) UnregisterHotKey(Handle, SkillMenuHotkeyId);
        if (handoffHotkeyRegistered) UnregisterHotKey(Handle, HandoffHotkeyId);
        if (storeHotkeyRegistered) UnregisterHotKey(Handle, StoreHotkeyId);
        if (inquireHotkeyRegistered) UnregisterHotKey(Handle, InquireHotkeyId);
        if (gatherHotkeyRegistered) UnregisterHotKey(Handle, GatherHotkeyId);
        sessionCancellation.Cancel();
        assistantSessions.AttentionRequested -= OnSessionAttention;
        assistantSessions.ContextUsageChanged -= OnContextUsageChanged;
        assistantSessions.HandoffCompleted -= OnHandoffCompleted;
        assistantSessions.StoredSessionAdded -= OnStoredSessionAdded;
        assistantSessions.StoredSessionRemoved -= OnStoredSessionRemoved;
        assistantSessions.SessionFocused -= OnSessionFocused;
        assistantSessions.SessionClosed -= OnSessionClosed;
        assistantSessions.ConnectionFailed -= OnConnectionFailed;
        _ = DisposeSessionsAsync();
        ClearSparkle();
        sparkleTimer.Dispose();
        focusWaitTimer.Dispose();
        ClearContextSweat();
        foreach (CompositionObject resource in orbResources) resource.Dispose();
        skillMenu.Dispose();
        sparkleBorder?.Close();
        sparkleBorder?.Dispose();
        queueOrbs?.Dispose();
        sessionEffects?.Dispose();
    }

    private async Task DisposeSessionsAsync()
    {
        try { await assistantSessions.DisposeAsync(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        sessionCancellation.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int identifier, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int identifier);
}

internal sealed class SessionSettings
{
    public string Shortcut { get; set; } = "Alt+Enter";
    public string Buddy { get; set; } = BuddySpriteCatalog.DefaultName;
    public string? WorkingDirectory { get; set; }
    public AssistantLaunchCommand? CopilotLaunch { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CliPath { get; set; }
    [JsonIgnore]
    public AssistantLaunchCommand EffectiveCopilotLaunch =>
        CopilotLaunch ??
        (!string.IsNullOrWhiteSpace(CliPath)
            ? new AssistantLaunchCommand(CliPath, [])
            : AssistantLaunchCommand.Default);
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopilotBuddy", "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static SessionSettings Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new()
        : new();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}