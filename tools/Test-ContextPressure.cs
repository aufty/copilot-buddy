#:project ../src/CopilotBuddy.Composition/CopilotBuddy.Composition.csproj
#:property TargetFramework=net10.0-windows10.0.22000.0
#:property UseWindowsForms=true
#:property PublishAot=false
#:property CopilotSkipCliDownload=true

using System.Reflection;
using CopilotBuddy.Core;

internal static class ContextPressureSmoke
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!args.Contains("--windowed")) throw new ArgumentException("Run with --windowed.");
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Assembly host = Assembly.Load("CopilotBuddy.Composition");
        Type windowType = host.GetType("CopilotBuddy.Composition.ReplayWindow", true)!;
        object settings = Activator.CreateInstance(host.GetType("CopilotBuddy.Composition.SessionSettings", true)!)!;
        FakeSessions sessions = new();
        using Form window = (Form)Activator.CreateInstance(windowType, sessions, settings)!;
        using System.Windows.Forms.Timer timer = new() { Interval = 200 };
        int phase = 0;
        Exception? failure = null;
        object? Field(string name) => windowType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        void Capture(string name, Rectangle bounds)
        {
            using Bitmap bitmap = new(bounds.Width, bounds.Height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            Directory.CreateDirectory("artifacts/context-pressure");
            bitmap.Save(Path.Combine("artifacts/context-pressure", name));
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                PresentationController controller = (PresentationController)Field("controller")!;
                ContextPressure pressure = (ContextPressure)Field("contextPressure")!;
                AttentionQueue alerts = (AttentionQueue)Field("attentionQueue")!;
                switch (phase++)
                {
                    case 0:
                        sessions.Usage(new("heavy", "Refactoring", 90000, 200000));
                        break;
                    case 1:
                        Check(pressure.IsHeavy && controller.WalkingSpeedMultiplier == 0.5, "Usage did not slow the buddy.");
                        Check(Field("contextSweat") is not null, "Sweat visuals were not created.");
                        Check(controller.Message is null && sessions.Focused.Count == 0, "Usage should not show or focus a session.");
                        break;
                    case 2:
                        float dpiScale = (float)Field("dpiScale")!;
                        Point buddyTop = window.PointToScreen(new Point((int)(controller.Snapshot.X * dpiScale),
                            (int)(((double)Field("baseline")! - controller.Snapshot.HopOffset) * dpiScale)));
                        Capture("sweating.png", new Rectangle(buddyTop.X - (int)(18 * dpiScale),
                            buddyTop.Y - (int)(8 * dpiScale), (int)(70 * dpiScale), (int)(55 * dpiScale)));
                        controller.RequestAction();
                        break;
                    case 3:
                        Check(controller.Message == "Refactoring session is getting heavy on context. I recommend compacting or handing off to a new session", "First click did not explain context pressure.");
                        Check(sessions.Focused.Count == 0, "First click focused the session prematurely.");
                        Check(((Form)Field("bubble")!).Visible, "Context message bubble is not visible.");
                        sessions.Attention(new("other", "Approval needed"));
                        sessions.CanFocus = false;
                        controller.RequestAction();
                        break;
                    case 4:
                        Capture("warning.png", ((Form)Field("bubble")!).Bounds);
                        Check(pressure.Presented?.SessionId == "heavy", "Failed focus discarded the context warning.");
                        Check(alerts.Current?.SessionId == "other", "Context warning lost a queued alert.");
                        sessions.CanFocus = true;
                        controller.RequestAction();
                        break;
                    case 5:
                        Check(sessions.Focused.SequenceEqual(new[] { "heavy", "heavy" }), "Second click did not target the heavy session.");
                        Check(pressure.Presented is null && pressure.IsHeavy, "Acknowledgement should hide the warning, not clear heavy usage.");
                        Check(controller.Message == "Approval needed", "Queued alert was not restored.");
                        controller.RequestAction();
                        break;
                    case 6:
                        Check(sessions.Focused.Last() == "other" && alerts.Count == 0, "A heavy session starved the regular alert queue.");
                        sessions.Usage(new("heavy", "Refactoring", 10000, 200000));
                        break;
                    case 7:
                        Check(!pressure.IsHeavy && controller.WalkingSpeedMultiplier == 1, "Recovery did not restore speed.");
                        Check(Field("contextSweat") is null, "Recovery did not remove sweat.");
                        sessions.Usage(new("closing", "Closing", 140000, 1000000));
                        break;
                    case 8:
                        controller.RequestAction();
                        sessions.Close("closing");
                        break;
                    case 9:
                        Check(!pressure.IsHeavy && pressure.Presented is null && controller.Message is null, "Closing a session did not clear its context state.");
                        Check(Field("contextSweat") is null && controller.WalkingSpeedMultiplier == 1, "Closing did not restore the normal presentation.");
                        Console.WriteLine("PASS: usage thresholds, sweat creation/removal, two-click focus, failed focus, queued alerts, recovery, and closure.");
                        timer.Stop();
                        window.Close();
                        break;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                timer.Stop();
                window.Close();
            }
        };
        window.Shown += (_, _) => timer.Start();
        Application.Run(window);
        if (failure is not null) throw failure;
        if (phase < 10) throw new InvalidOperationException("Context smoke closed before completing.");
    }

    private sealed class FakeSessions : IAssistantSessions
    {
        public event EventHandler<SessionAttention>? AttentionRequested;
        public event EventHandler<SessionContextUsage>? ContextUsageChanged;
        public event EventHandler<AssistantHandoff>? HandoffCompleted { add { } remove { } }
        public event EventHandler<string>? SessionFocused { add { } remove { } }
        public event EventHandler<string>? SessionClosed;
        public event EventHandler<string>? ConnectionFailed { add { } remove { } }
        public bool CanFocus { get; set; } = true;
        public List<string> Focused { get; } = [];
        public void Usage(SessionContextUsage usage) => ContextUsageChanged?.Invoke(this, usage);
        public void Attention(SessionAttention attention) => AttentionRequested?.Invoke(this, attention);
        public void Close(string sessionId) => SessionClosed?.Invoke(this, sessionId);
        public Task OpenAsync(string workingDirectory, CancellationToken cancellationToken) => throw new InvalidOperationException("Smoke tests must not launch a CLI.");
        public Task<bool> FocusAsync(string sessionId, CancellationToken cancellationToken)
        {
            Focused.Add(sessionId);
            return Task.FromResult(CanFocus);
        }
        public Task<AssistantSessionTarget?> CaptureSessionTargetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<AssistantSessionTarget?>(null);
        public Task<AssistantPromptInjection?> InjectPromptAsync(
            string prompt,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssistantPromptInjection?>(null);
        public Task<AssistantHandoff?> StartHandoffAsync(
            AssistantSessionTarget target,
            HandoffRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssistantHandoff?>(null);
        public Task CloseHandoffSourceAsync(AssistantHandoff handoff, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task OpenHandoffAsync(AssistantHandoff handoff, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Smoke tests must not launch a CLI.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}