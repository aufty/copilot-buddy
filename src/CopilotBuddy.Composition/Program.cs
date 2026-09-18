using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CopilotBuddy.Core;
using Microsoft.Win32;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace CopilotBuddy.Composition;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--assistant-broker", StringComparer.OrdinalIgnoreCase))
        {
            int cliPathIndex = Array.FindIndex(arguments,
                argument => string.Equals(argument, "--broker-cli-path", StringComparison.OrdinalIgnoreCase));
            string? cliPath = cliPathIndex >= 0 && cliPathIndex + 1 < arguments.Length
                ? arguments[cliPathIndex + 1]
                : null;
            CopilotBuddy.Copilot.CopilotSessionBrokerHost.RunAsync(cliPath).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using Mutex instance = new(true, "Local\\CopilotBuddy.Composition.v1", out bool firstInstance);
        if (!firstInstance)
        {
            return;
        }
        try
        {
            SessionSettings settings = SessionSettings.Load();
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not locate the Copilot Buddy executable.");
            using CancellationTokenSource startupTimeout = new(TimeSpan.FromSeconds(12));
            AssistantSessionBrokerClient sessions = AssistantSessionBrokerClient.ConnectAsync(
                executablePath, settings.CliPath, startupTimeout.Token).GetAwaiter().GetResult();
            Application.Run(new ReplayWindow(sessions, settings));
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), exception.ToString());
            Environment.ExitCode = 1;
        }
    }
}

internal sealed partial class ReplayWindow : Form
{
    private readonly System.Windows.Forms.Timer exitTimer = new() { Interval = 60000 };
    private DispatcherQueueController? queue;
    private Compositor? compositor;
    private DesktopWindowTarget? target;
    private ContainerVisual? root;
    private ContainerVisual? sprite;
    private readonly bool smokeTest = Environment.GetCommandLineArgs().Contains("--smoke");
    private readonly bool interactionSmoke = Environment.GetCommandLineArgs().Contains("--interaction-smoke");
    private bool ReplayOnly => !interactionSmoke && (smokeTest || Environment.GetCommandLineArgs().Contains("--replay"));
    private bool TaskbarMode => !ReplayOnly && !interactionSmoke && !Environment.GetCommandLineArgs().Contains("--windowed");
    private System.Windows.Forms.Timer? captureTimer;
    private int captures;
    private readonly System.Windows.Forms.Timer inputTimer = new() { Interval = 16 };
    private readonly PresentationOptions presentation = JsonSerializer.Deserialize<PresentationOptions>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "presentation.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    private PresentationController? controller;
    private float dpiScale;
    private double spriteWidth;
    private double spriteHeight;
    private double baseline;
    private bool[,]? opaquePixels;
    private bool dragging;
    private bool pointerPending;
    private Point pendingPointer;
    private long modelTimestamp;
    private static readonly TimeSpan MotionStep = TimeSpan.FromSeconds(1.0 / 120);
    private System.Windows.Forms.Timer? interactionTestTimer;
    private readonly List<PresentationSnapshot> interactionTestSamples = [];
    private int interactionTestPhase;
    private Point interactionTestStart;
    private bool smokeRegrabbed;
    private bool smokeCaptureReleased;
    private bool smokeSettled;
    private float smokeSettleDistance;
    private int smokeSettleSubmissions;
    private bool injectingInput;
    private readonly bool traceDrag = Environment.GetCommandLineArgs().Contains("--trace-drag");
    private List<DragTraceSample>? dragTrace;
    private long dragTraceStarted;
    private int dragAnimationSubmissions;
    private int dragTraceDropped;
    private readonly Dictionary<SpriteFrame, ContainerVisual> frameVisuals = [];
    private readonly Dictionary<SpriteFrame, bool[,]> frameMasks = [];
    private readonly System.Windows.Forms.Timer passiveTimer = new();
    private readonly System.Windows.Forms.Timer pointerTimer = new() { Interval = 20 };
    private readonly ContextMenuStrip exitMenu = new();
    private NotifyIcon? trayIcon;
    private bool hovered;
    private bool repositioning;
    private NativeDragMotion? nativeDrag;
    private bool releasingDrag;
    private bool placementPending;
    private Vector3 grabOffset;
    private Vector3 dragTarget;
    private bool directDrag = !Environment.GetCommandLineArgs().Contains("--spring-drag");
    private int completedNativeReleases;
    private bool flightReported;
    private int nativeMovingRetargets;
    private readonly Queue<Action> delayedDragReports = new();
    private readonly bool delayDragReports = Environment.GetCommandLineArgs().Contains("--delayed-drag-reports");
    private readonly uint taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private TaskbarLayerController? taskbarLayer;

    public ReplayWindow(IStoredAssistantSessions sessions, SessionSettings settings)
    {
        assistantSessions = sessions;
        sessionSettings = settings;
        presentation.Validate();
        Text = ReplayOnly ? "Copilot Buddy - Composition replay" : "Copilot Buddy - Composition drag trial";
        FormBorderStyle = TaskbarMode ? FormBorderStyle.None : FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Rectangle workArea = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(Math.Min(960, (int)(workArea.Width * 0.8)), Math.Min(540, (int)(workArea.Height * 0.8)));
        ShowInTaskbar = !TaskbarMode;
        KeyPreview = true;
        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape)
            {
                Close();
            }
        };
        exitTimer.Tick += (_, _) => Close();
        inputTimer.Tick += (_, _) => FlushPointer();
        passiveTimer.Tick += (_, _) =>
        {
            passiveTimer.Stop();
            AdvanceModel();
            if (controller!.Snapshot.State is VisualState.Airborne or VisualState.Landing)
            {
                AnimateInteraction();
            }
            else
            {
                StartPassiveMotion();
            }
        };
        pointerTimer.Tick += (_, _) => UpdatePointerRouting();
        DpiChanged += (_, _) => QueuePlacementRefresh();
        AddPresentationMenu();
        exitMenu.Items.Add("Exit", null, (_, _) => Close());
    }

    protected override bool ShowWithoutActivation => TaskbarMode;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x00200000;
            if (TaskbarMode)
            {
                parameters.ExStyle |= 0x08000080 | 0x00080000 | 0x20;
            }
            return parameters;
        }
    }

    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args);
        PlaceWindow();
        if (TaskbarMode)
        {
            taskbarLayer = new TaskbarLayerController(Handle);
            taskbarLayer.Start();
        }
        if (TaskbarMode && !SetLayeredWindowAttributes(Handle, 0, 255, 0x2))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        DispatcherQueueOptions options = new() { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out nint queuePointer));
        try
        {
            queue = MarshalInterface<DispatcherQueueController>.FromAbi(queuePointer);
        }
        finally
        {
            Marshal.Release(queuePointer);
        }
        compositor = new Compositor();
        ICompositorDesktopInterop interop = compositor.As<ICompositorDesktopInterop>();
        interop.CreateDesktopWindowTarget(Handle, false, out nint targetPointer);
        try
        {
            target = MarshalInterface<DesktopWindowTarget>.FromAbi(targetPointer);
        }
        finally
        {
            Marshal.Release(targetPointer);
        }
        root = compositor.CreateContainerVisual();
        root.BorderMode = CompositionBorderMode.Hard;
        target.Root = root;
        sprite = compositor.CreateContainerVisual();
        root.Children.InsertAtTop(sprite);
        LoadSpriteFrames();
        controller = new PresentationController(presentation, new SystemRandomSource(), 0,
            Math.Max(0, ClientSize.Width / dpiScale - spriteWidth));
        controller.SetMaximumLift(Math.Max(0, baseline));
        controller.SetHovered(!TaskbarMode);
        sprite.Offset = PositionOf(controller.Snapshot);
        modelTimestamp = Stopwatch.GetTimestamp();
        if (!ReplayOnly && !interactionSmoke)
        {
            StartPresentationHost();
        }
        if (ReplayOnly)
        {
            StartReplay();
        }
        if (TaskbarMode)
        {
            trayIcon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Copilot Buddy", ContextMenuStrip = exitMenu, Visible = true };
            StartPassiveMotion();
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        if (smokeTest || interactionSmoke)
        {
            StartSmokeTest();
        }
        if (!TaskbarMode)
        {
            exitTimer.Interval = smokeTest || interactionSmoke ? 5500 : ReplayOnly ? 60000 : 180000;
            exitTimer.Start();
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "replay-started.txt"),
            $"{DateTimeOffset.Now:O} Windows Composition; mode={(TaskbarMode ? "taskbar" : ReplayOnly ? "replay" : "interactive")}; {ClientSize.Width}x{ClientSize.Height}; DPI={DeviceDpi}; drag={(directDrag ? "direct" : "native-spring")}");
    }

    private void PlaceWindow()
    {
        if (TaskbarMode)
        {
            Bounds = TaskbarPlacement.GetPrimaryOverlayBounds();
        }
        else
        {
            Rectangle workArea = Screen.FromHandle(Handle).WorkingArea;
            float scale = DeviceDpi / 96f;
            ClientSize = new Size(Math.Min((int)(960 * scale), (int)(workArea.Width * 0.8)),
                Math.Min((int)(540 * scale), (int)(workArea.Height * 0.8)));
            Location = new Point(workArea.Left + (workArea.Width - Width) / 2, workArea.Top + (workArea.Height - Height) / 2);
        }
        dpiScale = DeviceDpi / 96f;
    }

    private void LoadSpriteFrames()
    {
        sprite!.Children.RemoveAll();
        foreach (ContainerVisual oldFrame in frameVisuals.Values)
        {
            oldFrame.Dispose();
        }
        frameVisuals.Clear();
        frameMasks.Clear();
        using Bitmap bitmap = new(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, presentation.Sprite.Path)));
        FrameRectangle standing = presentation.Sprite.Frames[SpriteFrame.Standing];
        float scale = presentation.Sprite.Scale * dpiScale;
        spriteWidth = standing.Width * presentation.Sprite.Scale;
        spriteHeight = standing.Height * presentation.Sprite.Scale;
        baseline = ClientSize.Height / dpiScale - spriteHeight;
        sprite.Size = new Vector2((float)spriteWidth * dpiScale, (float)spriteHeight * dpiScale);
        sprite.CenterPoint = new Vector3(sprite.Size.X / 2, sprite.Size.Y, 0);
        foreach ((SpriteFrame name, FrameRectangle frame) in presentation.Sprite.Frames)
        {
            if (frame.X + frame.Width > bitmap.Width || frame.Y + frame.Height > bitmap.Height ||
                frame.Width != standing.Width || frame.Height != standing.Height)
            {
                throw new InvalidOperationException("Composition frames must fit the sheet and share the standing frame dimensions.");
            }
            ContainerVisual layer = compositor!.CreateContainerVisual();
            layer.Opacity = name == SpriteFrame.Standing ? 1 : 0;
            bool[,] mask = new bool[frame.Width, frame.Height];
            for (int vertical = 0; vertical < frame.Height; vertical++)
            {
                for (int horizontal = 0; horizontal < frame.Width; horizontal++)
                {
                    Color pixel = bitmap.GetPixel(frame.X + horizontal, frame.Y + vertical);
                    if (pixel.A == 0 || (pixel.R == 255 && pixel.G == 255 && pixel.B == 255))
                    {
                        continue;
                    }
                    mask[horizontal, vertical] = true;
                    SpriteVisual visual = compositor.CreateSpriteVisual();
                    visual.Size = new Vector2(scale, scale);
                    visual.Offset = new Vector3(horizontal * scale, vertical * scale, 0);
                    visual.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B));
                    layer.Children.InsertAtTop(visual);
                }
            }
            frameVisuals.Add(name, layer);
            frameMasks.Add(name, mask);
            sprite.Children.InsertAtTop(layer);
        }
        opaquePixels = frameMasks[SpriteFrame.Standing];
    }

    private void ShowFrame(SpriteFrame frame)
    {
        foreach ((SpriteFrame name, ContainerVisual layer) in frameVisuals)
        {
            layer.StopAnimation(nameof(layer.Opacity));
            layer.Opacity = name == frame ? 1 : 0;
        }
        opaquePixels = frameMasks[frame];
    }

    private void StartPassiveMotion()
    {
        if (attentionBounce is not null || sessionLaunchJump is not null)
        {
            return;
        }
        if (!TaskbarMode && ReplayOnly)
        {
            return;
        }
        passiveTimer.Stop();
        PassiveMotionPlan? plan = controller!.PlanPassiveMotion();
        PresentationSnapshot current = controller.Snapshot;
        if (!current.IsVisible)
        {
            return;
        }
        if (current.State == VisualState.Attention)
        {
            StartAttention();
            return;
        }
        if (plan is null)
        {
            if (current.State == VisualState.Idle)
            {
                sprite!.StopAnimation(nameof(sprite.Offset));
                sprite.Offset = PositionOf(current);
                ShowFrame(SpriteFrame.Standing);
                StartBreathing();
            }
            return;
        }
        TimeSpan duration = plan.Value.Duration < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : plan.Value.Duration;
        using Vector3KeyFrameAnimation movement = compositor!.CreateVector3KeyFrameAnimation();
        using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
        movement.Duration = duration;
        movement.InsertExpressionKeyFrame(0, "this.StartingValue");
        movement.InsertKeyFrame(1, PositionOf(plan.Value.End), easing);
        sprite!.StartAnimation(nameof(sprite.Offset), movement);
        ShowFrame(current.Frame);
        if (current.State == VisualState.Walking)
        {
            sprite.StopAnimation(nameof(sprite.Scale));
            sprite.Scale = Vector3.One;
            StartWalkingFrames(current.Frame is SpriteFrame.WalkLeft1 or SpriteFrame.WalkLeft2);
        }
        else
        {
            StartBreathing();
        }
        passiveTimer.Interval = (int)Math.Clamp(Math.Ceiling(duration.TotalMilliseconds) + 1, 1, int.MaxValue);
        passiveTimer.Start();
    }

    private void StartWalkingFrames(bool left)
    {
        SpriteFrame first = left ? SpriteFrame.WalkLeft1 : SpriteFrame.WalkRight1;
        SpriteFrame second = left ? SpriteFrame.WalkLeft2 : SpriteFrame.WalkRight2;
        StartAlternatingFrames(first, second, presentation.Wander.WalkFrameSeconds / controller!.WalkingSpeedMultiplier);
    }

    private void StartAlternatingFrames(SpriteFrame first, SpriteFrame second, double frameSeconds)
    {
        using StepEasingFunction easing = compositor!.CreateStepEasingFunction(1);
        foreach (SpriteFrame frame in new[] { first, second })
        {
            using ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromSeconds(frameSeconds * 2);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.InsertKeyFrame(0, frame == first ? 1 : 0);
            animation.InsertKeyFrame(0.5f, frame == first ? 0 : 1, easing);
            animation.InsertKeyFrame(1, frame == first ? 1 : 0, easing);
            frameVisuals[frame].StartAnimation(nameof(ContainerVisual.Opacity), animation);
        }
    }

    private void StartBreathing()
    {
        sprite!.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        if (presentation.Attention.ReducedMotion || presentation.Breathing.Amount == 0)
        {
            return;
        }
        using Vector3KeyFrameAnimation animation = compositor!.CreateVector3KeyFrameAnimation();
        using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
        animation.Duration = TimeSpan.FromSeconds(presentation.Breathing.DurationSeconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        for (int sample = 0; sample <= 60; sample++)
        {
            float breath = (float)(Math.Sin(sample * Math.Tau / 60) * presentation.Breathing.Amount);
            animation.InsertKeyFrame(sample / 60f, new Vector3(1 - breath * 0.5f, 1 + breath, 1), easing);
        }
        sprite.StartAnimation(nameof(sprite.Scale), animation);
    }

    private void StartReplay()
    {
        float horizontalRange = ClientSize.Width - (float)spriteWidth * dpiScale;
        float verticalRange = (float)baseline * dpiScale;
        using Vector3KeyFrameAnimation animation = compositor!.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromSeconds(2.5);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
        for (int sample = 0; sample <= 600; sample++)
        {
            float progress = sample / 600f;
            double phase = progress * Math.Tau;
            animation.InsertKeyFrame(progress, new Vector3(
                horizontalRange / 2 + Math.Min(400 * DeviceDpi / 96f, horizontalRange * 0.35f) * (float)Math.Sin(phase),
                verticalRange / 2 - Math.Min(100 * DeviceDpi / 96f, verticalRange * 0.2f) * (float)Math.Sin(phase * 2), 0), easing);
        }
        sprite!.StartAnimation(nameof(sprite.Offset), animation);
    }

    private void StartSmokeTest()
    {
        if (interactionSmoke)
        {
            interactionTestTimer = new System.Windows.Forms.Timer { Interval = 150 };
            interactionTestTimer.Tick += (_, _) => RunInteractionTestStep();
            interactionTestTimer.Start();
        }
        captureTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        captureTimer.Tick += (_, _) =>
        {
            using Bitmap capture = new(ClientSize.Width, ClientSize.Height);
            using Graphics graphics = Graphics.FromImage(capture);
            graphics.CopyFromScreen(PointToScreen(Point.Empty), Point.Empty, ClientSize);
            capture.Save(Path.Combine(AppContext.BaseDirectory, $"smoke-{++captures}.png"));
            if (captures == 2)
            {
                captureTimer.Stop();
            }
        };
        captureTimer.Start();
    }

    private Vector3 PositionOf(PresentationSnapshot snapshot) =>
        new((float)snapshot.X * dpiScale, (float)(baseline - snapshot.HopOffset) * dpiScale, 0);

    private void AdvanceModel()
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan remaining = Stopwatch.GetElapsedTime(modelTimestamp, now);
        modelTimestamp = now;
        if (dragging || releasingDrag || sessionLaunchJump is not null ||
            (attentionBounce is not null && controller!.Message is null))
        {
            return;
        }
        while (remaining > TimeSpan.Zero && (!ReplayOnly || controller!.Snapshot.State is VisualState.Dragging or VisualState.Airborne or VisualState.Landing))
        {
            TimeSpan step = remaining < MotionStep ? remaining : MotionStep;
            controller!.Tick(step);
            remaining -= step;
        }
    }

    private void AnimateInteraction()
    {
        IReadOnlyList<PresentationSnapshot> samples = controller!.PreviewInteraction(MotionStep, 600);
        if (samples.Count < 2)
        {
            return;
        }
        using Vector3KeyFrameAnimation position = compositor!.CreateVector3KeyFrameAnimation();
        using Vector3KeyFrameAnimation deformation = compositor.CreateVector3KeyFrameAnimation();
        using LinearEasingFunction easing = compositor.CreateLinearEasingFunction();
        position.Duration = deformation.Duration = TimeSpan.FromTicks(MotionStep.Ticks * (samples.Count - 1));
        position.InsertExpressionKeyFrame(0, "this.StartingValue");
        deformation.InsertExpressionKeyFrame(0, "this.StartingValue");
        for (int index = 1; index < samples.Count; index++)
        {
            float progress = (float)index / (samples.Count - 1);
            PresentationSnapshot sample = samples[index];
            position.InsertKeyFrame(progress, PositionOf(sample), easing);
            if (!dragging)
            {
                deformation.InsertKeyFrame(progress, new Vector3((float)sample.ScaleX, (float)sample.ScaleY, 1), easing);
            }
        }
        sprite!.StartAnimation(nameof(sprite.Offset), position);
        if (!dragging)
        {
            sprite.StartAnimation(nameof(sprite.Scale), deformation);
            if (!ReplayOnly && !interactionSmoke)
            {
                passiveTimer.Interval = (int)Math.Ceiling(position.Duration.TotalMilliseconds) + 1;
                passiveTimer.Start();
            }
        }
    }

    private bool HitSprite(Point point)
    {
        PresentationSnapshot snapshot = controller!.Snapshot;
        if (!snapshot.IsVisible)
        {
            return false;
        }
        if (attentionBounce is not null && bubblePosition is not null)
        {
            Vector3 position = bubblePosition.Position;
            snapshot = snapshot with
            {
                X = position.X / dpiScale,
                HopOffset = baseline - position.Y / dpiScale,
                ScaleX = 1,
                ScaleY = 1,
                Frame = SpriteFrame.Wave1
            };
        }
        double left = snapshot.X + spriteWidth * (1 - snapshot.ScaleX) / 2;
        double top = baseline - snapshot.HopOffset + spriteHeight * (1 - snapshot.ScaleY);
        int horizontal = (int)Math.Floor((point.X / dpiScale - left) / (presentation.Sprite.Scale * snapshot.ScaleX));
        int vertical = (int)Math.Floor((point.Y / dpiScale - top) / (presentation.Sprite.Scale * snapshot.ScaleY));
        bool[,] mask = frameMasks.GetValueOrDefault(snapshot.Frame, opaquePixels!);
        return horizontal >= 0 && vertical >= 0 && horizontal < mask.GetLength(0) &&
            vertical < mask.GetLength(1) && mask[horizontal, vertical];
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (ReplayOnly || controller is null || (interactionSmoke && !injectingInput))
        {
            return;
        }
        AdvanceModel();
        if (args.Button == MouseButtons.Left && HitPresent(args.Location))
        {
            BeginPresentPress(args.Location);
            return;
        }
        if (!HitSprite(args.Location))
        {
            return;
        }
        if (args.Button == MouseButtons.Right)
        {
            ShowSkillMenu();
            return;
        }
        if (args.Button != MouseButtons.Left)
        {
            return;
        }
        if (attentionBounce is not null && !interactionSmoke)
        {
            dragStart = args.Location;
            dragMoved = false;
            attentionPressPending = true;
            Capture = true;
            return;
        }
        BeginPointerDrag(args.Location);
    }

    private void BeginPointerDrag(Point point)
    {
        Vector3? bouncePosition = attentionBounce is not null ? bubblePosition?.Position : null;
        StopAttentionBounce();
        passiveTimer.Stop();
        if (releasingDrag)
        {
            sprite!.StopAnimation(nameof(sprite.Offset));
            sprite.Offset = PositionOf(controller!.Snapshot);
            nativeMovingRetargets += nativeDrag!.MovingRetargets;
            nativeDrag.Dispose();
            nativeDrag = null;
            releasingDrag = false;
            RecordDragTrace("flight-regrabbed");
            SaveDragTrace();
        }
        if (traceDrag)
        {
            dragTrace = new List<DragTraceSample>(4096);
            dragTraceStarted = Stopwatch.GetTimestamp();
            dragAnimationSubmissions = 0;
            dragTraceDropped = 0;
        }
        controller!.BeginDrag(point.X / dpiScale, baseline - point.Y / dpiScale);
        if (bouncePosition is { } position)
        {
            controller.SetExternalDragPose(position.X / dpiScale, baseline - position.Y / dpiScale, 0, 0);
        }
        dragStart = point;
        dragMoved = false;
        dragging = true;
        pendingPointer = point;
        pointerPending = false;
        Capture = true;
        RecordDragTrace("begin");
        if (!Capture)
        {
            dragging = false;
            controller.CancelDrag();
            RecordDragTrace("capture-failed");
            SaveDragTrace();
            StartPassiveMotion();
            return;
        }
        sprite!.StopAnimation(nameof(sprite.Scale));
        sprite.Scale = Vector3.One;
        ShowFrame(SpriteFrame.Standing);
        dragTarget = PositionOf(controller.Snapshot);
        grabOffset = dragTarget - new Vector3(point.X, point.Y, 0);
        nativeDrag = new NativeDragMotion(compositor!, sprite, dragTarget,
            new Vector3(Math.Max(0, ClientSize.Width - (float)spriteWidth * dpiScale), (float)baseline * dpiScale, 0),
            presentation.Physics.DragResponseSeconds, (float)presentation.Physics.MaximumReleaseSpeed * dpiScale,
            (float)presentation.Physics.Gravity * dpiScale, (float)presentation.Physics.WallRestitution,
            directDrag, DispatchNativeDrag, OnNativeDragPose);
        RecordDragTrace(directDrag ? "direct-drag-started" : "native-spring-started");
        inputTimer.Start();
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (TrackPresentPress(args.Location))
        {
            return;
        }
        TrackAttentionPress(args.Location);
        if (dragging && (!interactionSmoke || injectingInput))
        {
            TrackDragMovement(args.Location);
            pendingPointer = args.Location;
            pointerPending = true;
        }
    }

    private void FlushPointer()
    {
        if (!pointerPending)
        {
            return;
        }
        pointerPending = false;
        if (presentDragging)
        {
            pressedPresent?.DragTo(pendingPointer);
            return;
        }
        if (!dragging)
        {
            return;
        }
        AdvanceModel();
        Vector3 updatedTarget = Vector3.Clamp(new Vector3(pendingPointer.X, pendingPointer.Y, 0) + grabOffset,
            Vector3.Zero, new Vector3(Math.Max(0, ClientSize.Width - (float)spriteWidth * dpiScale), (float)baseline * dpiScale, 0));
        if (updatedTarget != dragTarget)
        {
            dragTarget = updatedTarget;
            RecordDragTrace("target-changed");
            nativeDrag!.SetTarget(dragTarget);
            dragAnimationSubmissions++;
            RecordDragTrace("native-target-submitted");
        }
        else
        {
            RecordDragTrace("target-unchanged");
        }
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button == MouseButtons.Left)
        {
            if (EndPresentPress(args.Location))
            {
                return;
            }
            TrackAttentionPress(args.Location);
            if (attentionPressPending)
            {
                attentionPressPending = false;
                Capture = false;
                controller!.RequestAction();
                return;
            }
        }
        if (dragging && args.Button == MouseButtons.Left && (!interactionSmoke || injectingInput))
        {
            pendingPointer = args.Location;
            pointerPending = true;
            TrackDragMovement(args.Location);
            if (!dragMoved && !interactionSmoke)
            {
                CancelNativeInteraction();
                controller!.RequestAction();
            }
            else
            {
                ReleaseDrag();
            }
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs args)
    {
        base.OnMouseCaptureChanged(args);
        if (!Capture)
        {
            attentionPressPending = false;
            CancelOrReleasePresentPress();
        }
        if (dragging && !Capture)
        {
            RecordDragTrace("capture-lost");
            ReleaseDrag();
        }
    }

    private void ReleaseDrag()
    {
        if (!dragging || releasingDrag)
        {
            return;
        }
        FlushPointer();
        AdvanceModel();
        RecordDragTrace("release-requested");
        dragging = false;
        releasingDrag = true;
        inputTimer.Stop();
        pointerPending = false;
        Capture = false;
        controller!.EndDragFromPose(controller.Snapshot.X, controller.Snapshot.HopOffset, 0, 0);
        flightReported = false;
        nativeDrag?.Release();
    }

    private void DispatchNativeDrag(Action action)
    {
        if (!Disposing && !IsDisposed && IsHandleCreated)
        {
            BeginInvoke(() =>
            {
                if (interactionSmoke && delayDragReports && dragging)
                {
                    delayedDragReports.Enqueue(action);
                }
                else
                {
                    action();
                }
            });
        }
    }

    private void OnNativeDragPose(Vector3 position, Vector3 velocity, bool released)
    {
        if (releasingDrag && !flightReported)
        {
            flightReported = true;
            completedNativeReleases++;
            RecordDragTrace("flight-started");
        }
        if (released)
        {
            controller!.SetExternalFlightPose(position.X / dpiScale, 0, -velocity.Y / dpiScale);
            controller.Tick(MotionStep);
            releasingDrag = false;
            modelTimestamp = Stopwatch.GetTimestamp();
            sprite!.StopAnimation(nameof(sprite.Offset));
            sprite.Offset = position;
            nativeMovingRetargets += nativeDrag!.MovingRetargets;
            nativeDrag!.Dispose();
            nativeDrag = null;
            RecordDragTrace("release-completed");
            SaveDragTrace();
            if (placementPending)
            {
                placementPending = false;
                RefreshPlacement();
            }
            else
            {
                AnimateInteraction();
            }
        }
        else if (releasingDrag)
        {
            controller!.SetExternalFlightPose(position.X / dpiScale, baseline - position.Y / dpiScale);
            RecordDragTrace("flight-pose");
        }
        else if (dragging)
        {
            controller!.SetExternalDragPose(position.X / dpiScale, baseline - position.Y / dpiScale,
                velocity.X / dpiScale, -velocity.Y / dpiScale);
            RecordDragTrace("native-pose");
        }
    }

    private void UpdatePointerRouting()
    {
        if (controller is null || Disposing || IsDisposed)
        {
            return;
        }
        AdvanceModel();
        UpdateBubble();
        UpdatePresentBubble();
        Point pointer = PointToClient(Cursor.Position);
        PollPresentPress(pointer);
        bool buddyHit = HitSprite(pointer);
        bool presentHit = HitPresent(pointer);
        bool interactionHold = buddyHit || skillMenu.Visible;
        bool interactive = dragging || presentPressPending || presentDragging || interactionHold || presentHit || exitMenu.Visible;
        int style = GetWindowLong(Handle, -20);
        int updatedStyle = interactive ? style & ~0x20 : style | 0x20;
        if (TaskbarMode && style != updatedStyle)
        {
            SetWindowLong(Handle, -20, updatedStyle);
        }
        if (hovered != interactionHold)
        {
            RecordDragTrace(interactionHold ? "hover-enter" : "hover-leave");
            hovered = interactionHold;
            controller.SetHovered(interactionHold);
            if (controller.Snapshot.State is VisualState.Idle or VisualState.Walking)
            {
                StartPassiveMotion();
            }
        }
        RecordDragTrace("pointer-check");
    }

    private void RecordDragTrace(string eventName)
    {
        if (dragTrace is null || controller is null)
        {
            return;
        }
        if (dragTrace.Count >= 4096)
        {
            dragTraceDropped++;
            return;
        }
        Point pointer = PointToClient(Cursor.Position);
        dragTrace.Add(new DragTraceSample(Stopwatch.GetElapsedTime(dragTraceStarted).TotalMilliseconds,
            eventName, pointer.X, pointer.Y, pendingPointer.X, pendingPointer.Y,
            Capture, hovered, dragAnimationSubmissions, controller.Snapshot));
    }

    private void SaveDragTrace()
    {
        if (dragTrace is null)
        {
            return;
        }
        DragTraceSample[] samples = dragTrace.ToArray();
        int dropped = dragTraceDropped;
        dragTrace = null;
        string directory = Path.Combine(Path.GetTempPath(), "CopilotBuddy", "drag-traces");
        string path = Path.Combine(directory, $"drag-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Environment.ProcessId}.json");
        var report = new { ProcessId = Environment.ProcessId, DpiScale = dpiScale, BaselineDip = baseline,
            BorderMode = root?.BorderMode.ToString(), DroppedSamples = dropped, Samples = samples };
        BeginInvoke(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(report));
            }
            catch (IOException exception)
            {
                Debug.WriteLine(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.WriteLine(exception);
            }
        });
    }

    private sealed record DragTraceSample(double TimeMs, string Event, int CursorX, int CursorY,
        int PendingX, int PendingY, bool Captured, bool Hovered, int AnimationSubmissions, PresentationSnapshot Model);

    protected override void WndProc(ref Message message)
    {
        if (TaskbarMode && (uint)message.Msg == taskbarCreatedMessage)
        {
            taskbarLayer?.Reattach();
            QueuePlacementRefresh();
            return;
        }
        if (message.Msg == 0x0312 && message.WParam == OpenSessionHotkeyId)
        {
            _ = OpenSessionAsync(celebrate: true);
            return;
        }
        if (message.Msg == 0x0312 && message.WParam == SkillMenuHotkeyId)
        {
            if (skillMenu.Visible) skillMenu.Hide();
            else ShowSkillMenu();
            return;
        }
        if (message.Msg == 0x0312 && message.WParam == HandoffHotkeyId)
        {
            _ = StartHandoffAsync();
            return;
        }
        if (message.Msg == 0x0312 && message.WParam == StoreHotkeyId)
        {
            _ = StoreSessionAsync();
            return;
        }
        if (message.Msg == 0x0312 && message.WParam == InquireHotkeyId)
        {
            _ = InjectInquireAsync();
            return;
        }
        if (TaskbarMode && message.Msg == 0x0021)
        {
            message.Result = HasSessionAction || HasInteractivePresent ? 1 : 3;
            return;
        }
        if (TaskbarMode && controller is not null && message.Msg == 0x0084)
        {
            AdvanceModel();
            long packed = message.LParam.ToInt64();
            Point screenPoint = new(unchecked((short)packed), unchecked((short)(packed >> 16)));
            Point clientPoint = PointToClient(screenPoint);
            message.Result = dragging || presentPressPending || presentDragging ||
                HitSprite(clientPoint) || HitPresent(clientPoint) ? 1 : -1;
            return;
        }
        base.WndProc(ref message);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args) => QueuePlacementRefresh();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => QueuePlacementRefresh();

    private void QueuePlacementRefresh()
    {
        if (IsHandleCreated && !Disposing && !IsDisposed)
        {
            BeginInvoke(RefreshPlacement);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        if (TaskbarMode && controller is not null && !repositioning)
        {
            QueuePlacementRefresh();
        }
    }

    private void RefreshPlacement()
    {
        if (repositioning || controller is null || Disposing || IsDisposed)
        {
            return;
        }
        if (dragging || releasingDrag || handoffPresent?.IsMoving == true)
        {
            placementPending = true;
            ReleaseDrag();
            return;
        }
        repositioning = true;
        try
        {
            AdvanceModel();
            if (dragging)
            {
                ReleaseDrag();
            }
            passiveTimer.Stop();
            PlaceWindow();
            if (TaskbarMode)
            {
                taskbarLayer?.Reattach();
            }
            LoadSpriteFrames();
            UpdateContextSweat();
            RelayoutPresent();
            controller.SetHorizontalBounds(0, Math.Max(0, ClientSize.Width / dpiScale - spriteWidth));
            controller.SetMaximumLift(Math.Max(0, baseline));
            sprite!.StopAnimation(nameof(sprite.Offset));
            sprite.Offset = PositionOf(controller.Snapshot);
            if (controller.Snapshot.State is VisualState.Airborne or VisualState.Landing)
            {
                AnimateInteraction();
            }
            else
            {
                StartPassiveMotion();
            }
        }
        finally
        {
            repositioning = false;
        }
    }

    private Point CurrentSpriteCenter()
    {
        PresentationSnapshot snapshot = controller!.Snapshot;
        return new Point((int)((snapshot.X + spriteWidth / 2) * dpiScale),
            (int)((baseline - snapshot.HopOffset + spriteHeight * (1 - snapshot.ScaleY / 2)) * dpiScale));
    }

    private void RunInteractionTestStep()
    {
        while (delayedDragReports.TryDequeue(out Action? delayedReport))
        {
            delayedReport();
        }
        injectingInput = true;
        AdvanceModel();
        if (interactionTestPhase == 0 || interactionTestPhase == 13)
        {
            bool airborne = controller!.Snapshot.State == VisualState.Airborne;
            interactionTestStart = CurrentSpriteCenter();
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, interactionTestStart.X, interactionTestStart.Y, 0));
            if (interactionTestPhase == 13)
            {
                smokeRegrabbed = airborne && dragging;
            }
        }
        else if (interactionTestPhase is 1 or 2 or 3 or 8 or 14 or 17)
        {
            int horizontal = interactionTestStart.X + (int)((interactionTestPhase is 3 or 8 or 17 ? -180 : interactionTestPhase == 2 ? 240 : 180) * dpiScale);
            int vertical = interactionTestStart.Y - (int)(180 * dpiScale);
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, horizontal, vertical, 0));
        }
        else if (interactionTestPhase == 7)
        {
            smokeSettleDistance = Vector3.Distance(PositionOf(controller!.Snapshot), dragTarget);
            smokeSettleSubmissions = dragAnimationSubmissions;
            smokeSettled = smokeSettleDistance <= 1 && smokeSettleSubmissions == 3;
        }
        else if (interactionTestPhase == 12)
        {
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, pendingPointer.X, pendingPointer.Y, 0));
        }
        else if (interactionTestPhase == 18)
        {
            Capture = false;
            smokeCaptureReleased = !dragging && releasingDrag;
            interactionTestTimer!.Stop();
        }
        interactionTestSamples.Add(controller!.Snapshot);
        interactionTestPhase++;
        injectingInput = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs args)
    {
        StopPresentationHost();
        dragging = false;
        releasingDrag = false;
        nativeDrag?.Dispose();
        inputTimer.Dispose();
        passiveTimer.Dispose();
        pointerTimer.Dispose();
        taskbarLayer?.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        trayIcon?.Dispose();
        exitMenu.Dispose();
        interactionTestTimer?.Dispose();
        if (interactionSmoke && controller is not null)
        {
            AdvanceModel();
            interactionTestSamples.Add(controller.Snapshot);
            bool passed = smokeSettled && smokeRegrabbed && smokeCaptureReleased && completedNativeReleases == 2 &&
                (directDrag || nativeMovingRetargets > 0) &&
                interactionTestSamples.Any(sample => sample.State == VisualState.Dragging && sample.HopOffset > 10) &&
                controller.Snapshot.State == VisualState.Idle && controller.Snapshot.HopOffset == 0;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "interaction-smoke.json"),
                JsonSerializer.Serialize(new { Passed = passed, SettledWithoutRetarget = smokeSettled, SettleDistance = smokeSettleDistance, SettleSubmissions = smokeSettleSubmissions, DragAnimationSubmissions = dragAnimationSubmissions, MovingRetargets = nativeMovingRetargets, Regrabbed = smokeRegrabbed, CaptureReleased = smokeCaptureReleased, CompletedReleases = completedNativeReleases, Samples = interactionTestSamples }));
            if (!passed)
            {
                Environment.ExitCode = 1;
            }
        }
        exitTimer.Stop();
        exitTimer.Dispose();
        captureTimer?.Dispose();
        sprite?.StopAnimation(nameof(sprite.Offset));
        target?.Dispose();
        compositor?.Dispose();
        base.OnFormClosed(args);
        GC.KeepAlive(queue);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(nint window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
        void EnsureOnThread(uint threadId);
    }
}