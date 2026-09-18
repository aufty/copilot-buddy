using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private CompositionScopedBatch? sessionLaunchJump;
    private bool openingSession;
    private bool focusingSession;
    private bool startingHandoff;
    private bool storingSession;
    private bool injectingInquire;
    private bool hotkeyRegistered;
    private bool skillMenuHotkeyRegistered;
    private bool handoffHotkeyRegistered;
    private bool storeHotkeyRegistered;
    private bool inquireHotkeyRegistered;
    private SparkleBorderWindow? sparkleBorder;
    private bool sessionsStopping;
    private ToolStripMenuItem? shortcutMenu;

    private void AddSessionMenu()
    {
        skillMenu.InquireRequested += async (_, _) => await InjectInquireAsync();
        skillMenu.HandoffRequested += async (_, _) => await StartHandoffAsync();
        skillMenu.StoreRequested += async (_, _) => await StoreSessionAsync();
        skillMenu.VisibleChanged += (_, _) => UpdatePointerRouting();

        exitMenu.Items.Add("Open Copilot CLI", null, async (_, _) => await OpenSessionAsync());
        shortcutMenu = new ToolStripMenuItem($"Shortcut: {sessionSettings.Shortcut}", null, (_, _) => EditShortcut());
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
    }

    private void ShowSkillMenu()
    {
        if (controller is null || !controller.IsVisible)
        {
            return;
        }
        AdvanceModel();
        PresentationSnapshot snapshot = controller.Snapshot;
        Point buddyTopRight = PointToScreen(new Point(
            (int)Math.Round((snapshot.X + spriteWidth) * dpiScale),
            (int)Math.Round((baseline - snapshot.HopOffset) * dpiScale)));
        Size menuSize = new((int)Math.Round(360 * dpiScale), (int)Math.Round(302 * dpiScale));
        Rectangle area = Screen.FromPoint(buddyTopRight).WorkingArea;
        int x = buddyTopRight.X + (int)(8 * dpiScale);
        if (x + menuSize.Width > area.Right)
        {
            x = buddyTopRight.X - (int)Math.Round(spriteWidth * dpiScale) - menuSize.Width - (int)(8 * dpiScale);
        }
        int y = Math.Clamp(buddyTopRight.Y - menuSize.Height, area.Top, Math.Max(area.Top, area.Bottom - menuSize.Height));
        skillMenu.Present(this,
            new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - menuSize.Width)), y),
            dpiScale);
    }

    private async Task InjectInquireAsync()
    {
        if (injectingInquire || sessionsStopping)
        {
            return;
        }
        injectingInquire = true;
        skillMenu.Hide();
        StartSessionLaunchJump();
        try
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            AssistantPromptInjection? injection =
                await assistantSessions.InjectPromptAsync(AssistantSkillPrompts.Grilling, timeout.Token);
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
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not start Inquire: {exception.Message}");
        }
        finally
        {
            injectingInquire = false;
        }
    }

    private async Task StartHandoffAsync()
    {
        if (startingHandoff || sessionsStopping)
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
        HandoffRequest? request = HandoffQuestionDialog.Ask(this, dpiScale);
        if (request is null)
        {
            return;
        }
        startingHandoff = true;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            AssistantHandoff? handoff = await assistantSessions.StartHandoffAsync(target, request, timeout.Token);
            if (handoff is not null)
            {
                if (!sessionsStopping)
                {
                    DropHandoffPresent(handoff);
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
        if (storingSession || sessionsStopping)
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
    }

    private async Task OpenSessionAsync(bool celebrate = false)
    {
        if (openingSession || sessionsStopping)
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

    private void StartSessionLaunchJump()
    {
        if (controller is null || sprite is null || !controller.IsVisible || dragging || releasingDrag ||
            controller.Snapshot.State is VisualState.Airborne or VisualState.Landing)
        {
            return;
        }

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
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (await assistantSessions.FocusAsync(sessionId, timeout.Token))
            {
                if (sessionsStopping) return;
                acknowledge();
            }
            else
            {
                ShowSessionError("Could not focus that CLI window. The alert remains queued.");
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowSessionError($"Could not focus the session: {exception.Message}");
        }
        finally
        {
            focusingSession = false;
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
            Text = "Open CLI shortcut", FormBorderStyle = FormBorderStyle.FixedDialog,
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
                shortcutMenu!.Text = $"Shortcut: {input.Text}";
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
        if (sessionEffects is null || !controller!.IsVisible)
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
        StopSessionLaunchJump();
        if (hotkeyRegistered) UnregisterHotKey(Handle, OpenSessionHotkeyId);
        if (skillMenuHotkeyRegistered) UnregisterHotKey(Handle, SkillMenuHotkeyId);
        if (handoffHotkeyRegistered) UnregisterHotKey(Handle, HandoffHotkeyId);
        if (storeHotkeyRegistered) UnregisterHotKey(Handle, StoreHotkeyId);
        if (inquireHotkeyRegistered) UnregisterHotKey(Handle, InquireHotkeyId);
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
    public string? WorkingDirectory { get; set; }
    public string? CliPath { get; set; }
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