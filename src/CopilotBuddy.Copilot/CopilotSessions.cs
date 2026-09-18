using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using CopilotBuddy.Core;

namespace CopilotBuddy.Copilot;

public sealed class CopilotSessions : IAssistantSessions
{
    private readonly ConcurrentDictionary<string, TerminalSession> terminals = new();
    private readonly ConcurrentDictionary<string, AssistantHandoff> pendingHandoffs = new();
    private readonly CancellationTokenSource shutdown = new();
    private AssistantLaunchCommand launchCommand = AssistantLaunchCommand.Default;
    private string? lastOpenedTerminalIdentifier;
    private string? lastFocusedSessionKey;
    public event EventHandler<SessionAttention>? AttentionRequested;
    public event EventHandler<SessionContextUsage>? ContextUsageChanged;
    public event EventHandler<AssistantHandoff>? HandoffCompleted;
    public event EventHandler<string>? SessionFocused;
    public event EventHandler<string>? SessionClosed;
    public event EventHandler<string>? ConnectionFailed;

    public Task OpenAsync(string workingDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => Open(workingDirectory, null, null, false, cancellationToken), cancellationToken);

    public void ConfigureLaunch(AssistantLaunchCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Executable);
        Volatile.Write(ref launchCommand, new(command.Executable, [.. command.Arguments ?? []]));
    }

    private TerminalSession Open(
        string workingDirectory,
        string? initialPrompt,
        string? resumeSessionId,
        bool waitForAttachment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(shutdown.IsCancellationRequested, this);
        string resolvedWorkingDirectory = Path.GetFullPath(workingDirectory);
        AssistantLaunchCommand configuredLaunch = Volatile.Read(ref launchCommand);
        string executable = FindExecutable(configuredLaunch.Executable);
        string runtimeDirectory = CopilotRuntimeCompatibility.Prepare(cancellationToken);
        string identifier = Guid.NewGuid().ToString();
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = resolvedWorkingDirectory
        };
        foreach (string argument in configuredLaunch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--ui-server");
        start.ArgumentList.Add("--no-auto-update");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--session-id");
        start.ArgumentList.Add(identifier);
        foreach (string variable in start.Environment.Keys
            .Where(name => name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            start.Environment.Remove(variable);
        }
        start.Environment["COPILOT_CONNECTION_TOKEN"] = token;
        start.Environment["COPILOT_CLI_DIST_DIR"] = runtimeDirectory;
        reservation.Stop();
        Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not open the Copilot CLI window.");
        TerminalSession terminal = new(
            identifier,
            process,
            port,
            token,
            resolvedWorkingDirectory,
            initialPrompt,
            waitForAttachment ? resumeSessionId : null);
        terminals[identifier] = terminal;
        Volatile.Write(ref lastOpenedTerminalIdentifier, identifier);
        Volatile.Write(ref lastFocusedSessionKey, null);
        terminal.Monitor = Task.Run(() => ObserveAsync(terminal, shutdown.Token));
        return terminal;
    }

    private static string FindExecutable(string configured)
    {
        if (!string.Equals(configured, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            string? resolved = ResolveExecutable(configured);
            if (resolved is null)
            {
                throw new FileNotFoundException($"Could not find launch executable '{configured}'.");
            }
            return resolved;
        }
        string? environmentPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            string? resolved = ResolveExecutable(environmentPath);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory.Trim('"'), "copilot.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (string directory in Directory.EnumerateDirectories(packages, "GitHub.Copilot_*"))
            {
                string candidate = Path.Combine(directory, "copilot.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        throw new FileNotFoundException("Install GitHub Copilot CLI, sign in with copilot login, or configure the launch command.");
    }

    private static string? ResolveExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            string fullPath = Path.GetFullPath(executable);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
            if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullPath + ".exe"))
            {
                return fullPath + ".exe";
            }
            return null;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }
        return null;
    }

    private async Task ObserveAsync(TerminalSession terminal, CancellationToken cancellationToken)
    {
        string stage = "connecting";
        try
        {
            RecordAttachment(terminal, stage);
            Stopwatch connecting = Stopwatch.StartNew();
            while (!terminal.Process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopilotClient candidate = new(new CopilotClientOptions
                {
                    Connection = RuntimeConnection.ForUri($"127.0.0.1:{terminal.Port}", terminal.Token)
                });
                terminal.Client = candidate;
                try
                {
                    using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(3));
                    await candidate.StartAsync(attempt.Token).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                    terminal.Client = candidate;
                    stage = "connected";
                    RecordAttachment(terminal, stage);
                    break;
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    RecordAttachment(terminal, stage, exception);
                    await DisconnectAsync(candidate);
                    terminal.Client = null;
                    if (connecting.Elapsed > TimeSpan.FromSeconds(10))
                    {
                        throw new IOException($"Could not attach to the CLI: {exception.GetBaseException().Message}", exception);
                    }
                }
                await Task.Delay(500, cancellationToken);
            }
            if (terminal.Client is not { } client)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ConnectionFailed?.Invoke(this, "Copilot CLI closed before attachment. Check that your CLI supports --ui-server.");
                }
                return;
            }
            using IDisposable lifecycle = client.OnLifecycle<SessionLifecycleEvent>(change =>
            {
                if (change is SessionForegroundEvent foreground)
                {
                    terminal.ForegroundSessionId = foreground.SessionId;
                    terminal.Discovered.Enqueue(foreground.SessionId);
                    if (IsTerminalFocused(terminal))
                    {
                        Volatile.Write(ref lastFocusedSessionKey, Key(terminal, foreground.SessionId));
                    }
                }
                else if (change is SessionCreatedEvent created)
                {
                    terminal.Discovered.Enqueue(created.SessionId);
                }
                else if (change is SessionDeletedEvent)
                {
                    string sessionKey = Key(terminal, change.SessionId);
                    pendingHandoffs.TryRemove(sessionKey, out _);
                    Interlocked.CompareExchange(ref lastFocusedSessionKey, null, sessionKey);
                    SessionClosed?.Invoke(this, sessionKey);
                }
            });
            if (terminal.ExpectedSessionId is { } expectedSessionId)
            {
                terminal.Discovered.Enqueue(expectedSessionId);
            }
            while (!terminal.Process.HasExited)
            {
                using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                stage = "foreground";
                string? foreground = await client.GetForegroundSessionIdAsync(request.Token).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (foreground is not null)
                {
                    terminal.ForegroundSessionId = foreground;
                    terminal.Discovered.Enqueue(foreground);
                    if (string.Equals(Volatile.Read(ref lastOpenedTerminalIdentifier), terminal.Identifier,
                        StringComparison.Ordinal) && Volatile.Read(ref lastFocusedSessionKey) is null)
                    {
                        Interlocked.CompareExchange(ref lastFocusedSessionKey, Key(terminal, foreground), null);
                    }
                }
                while (terminal.Discovered.TryDequeue(out string? sessionId))
                {
                    if (terminal.ExpectedSessionId is not null &&
                        string.Equals(sessionId, terminal.Identifier, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (terminal.Sessions.ContainsKey(sessionId))
                    {
                        continue;
                    }
                    SessionActivity activity = new() { Title = sessionId[..Math.Min(8, sessionId.Length)] };
                    stage = "subscribing";
                    using CancellationTokenSource subscription =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    subscription.CancelAfter(TimeSpan.FromSeconds(30));
                    activity.Session = await client.ResumeSessionAsync(sessionId, new ResumeSessionConfig
                    {
                        SuppressResumeEvent = true,
                        OnEvent = change => ObserveEvent(terminal, sessionId, activity, change)
                    }, subscription.Token).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    terminal.Sessions[sessionId] = activity;
                    if (string.Equals(sessionId, terminal.ExpectedSessionId, StringComparison.Ordinal))
                    {
                        await client.SetForegroundSessionIdAsync(sessionId, subscription.Token);
                        terminal.ForegroundSessionId = sessionId;
                        terminal.Attached?.TrySetResult(true);
                    }
                    if (terminal.PendingPrompt is { } pendingPrompt &&
                        string.Equals(sessionId, terminal.ForegroundSessionId, StringComparison.Ordinal))
                    {
                        terminal.PendingPrompt = null;
                        await activity.Session.SendAsync(pendingPrompt, request.Token);
                    }
                    stage = "attached";
                    RecordAttachment(terminal, stage);
                }
                PublishManualFocus(terminal);
                await Task.Delay(750, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            terminal.Attached?.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            terminal.Attached?.TrySetException(exception);
            RecordAttachment(terminal, stage, exception);
            if (!cancellationToken.IsCancellationRequested && !terminal.Process.HasExited)
            {
                ConnectionFailed?.Invoke(this, $"Copilot attachment failed while {stage}: {exception.Message.Replace(terminal.Token, "[redacted]")}");
            }
        }
        finally
        {
            if (terminal.Attached is { Task.IsCompleted: false } attached)
            {
                attached.TrySetException(new IOException(
                    "Copilot CLI closed before the stored session could be resumed."));
            }
            if (cancellationToken.IsCancellationRequested || terminal.Process.HasExited)
            {
                RecordAttachment(terminal, "closed");
            }
            if (terminal.Client is not null)
            {
                await DisconnectAsync(terminal.Client);
            }
            terminals.TryRemove(terminal.Identifier, out _);
            foreach (string sessionId in terminal.Sessions.Keys)
            {
                SessionClosed?.Invoke(this, Key(terminal, sessionId));
            }
            SessionClosed?.Invoke(this, terminal.Identifier);
            terminal.Process.Dispose();
        }
    }

    private static async Task DisconnectAsync(CopilotClient client)
    {
        try { await client.ForceStopAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception exception) { Debug.WriteLine(exception); }
    }

    private void ObserveEvent(TerminalSession terminal, string sessionId, SessionActivity activity, SessionEvent change)
    {
        string? message = null;
        switch (change)
        {
            case SessionUsageInfoEvent usage:
                PublishContextUsage(terminal, sessionId, activity, usage.Data.CurrentTokens, usage.Data.TokenLimit);
                break;
            case SessionTitleChangedEvent title:
                if (!string.IsNullOrWhiteSpace(title.Data.Title))
                {
                    activity.Title = title.Data.Title;
                    if (activity.Usage is { } previous)
                    {
                        PublishContextUsage(terminal, sessionId, activity, previous.CurrentTokens, previous.TokenLimit);
                    }
                }
                break;
            case SessionCompactionCompleteEvent compaction when compaction.Data.Success:
                if (compaction.Data.PostCompactionTokens is { } tokens)
                {
                    PublishContextUsage(terminal, sessionId, activity, tokens,
                        compaction.Data.TokenLimit ?? activity.Usage?.TokenLimit ?? 0);
                }
                break;
            case UserMessageEvent:
            case AssistantTurnStartEvent:
                activity.Busy = true;
                break;
            case PermissionRequestedEvent:
                message = AssistantMessages.PickPermission();
                break;
            case UserInputRequestedEvent:
            case ElicitationRequestedEvent:
                message = AssistantMessages.PickQuestion();
                break;
            case ExitPlanModeRequestedEvent:
                message = AssistantMessages.PickPlanReview();
                break;
            case SessionErrorEvent:
                activity.Busy = false;
                message = AssistantMessages.PickError();
                break;
            case SessionIdleEvent when activity.Busy:
                activity.Busy = false;
                message = AssistantMessages.PickCompletion();
                if (pendingHandoffs.TryRemove(Key(terminal, sessionId), out AssistantHandoff? handoff))
                {
                    HandoffCompleted?.Invoke(this, handoff);
                }
                break;
        }
        if (message is not null && !IsFocusedSession(terminal, sessionId))
        {
            AttentionRequested?.Invoke(this, new(Key(terminal, sessionId), message));
        }
    }

    private static void RecordAttachment(TerminalSession terminal, string stage, Exception? error = null)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopilotBuddy", "connections");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, terminal.Identifier + ".json"), JsonSerializer.Serialize(new
            {
                updatedUtc = DateTimeOffset.UtcNow,
                stage,
                port = terminal.Port,
                processId = terminal.Process.Id,
                error = error?.ToString().Replace(terminal.Token, "[redacted]"),
                errorType = error?.GetType().FullName
            }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
        }
    }

    public async Task<bool> FocusAsync(string sessionId, CancellationToken cancellationToken)
    {
        string[] parts = sessionId.Split('/', 2);
        if (!terminals.TryGetValue(parts[0], out TerminalSession? terminal) || terminal.Process.HasExited)
        {
            return false;
        }
        nint window = FindTerminalWindow(terminal);
        if (window == 0)
        {
            return false;
        }
        if (IsIconic(window))
        {
            ShowWindow(window, 9);
        }
        bool focused = SetForegroundWindow(window) || GetForegroundWindow() == window;
        if (focused && parts.Length == 2 && terminal.Client is not null)
        {
            await terminal.Client.SetForegroundSessionIdAsync(parts[1], cancellationToken);
            Volatile.Write(ref lastFocusedSessionKey, sessionId);
        }
        return focused;
    }

    public Task<AssistantGatherResult> GatherAsync(
        AssistantWindowBounds workArea,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), "Gather requires a non-empty screen work area.");
        }

        List<nint> windows = [];
        HashSet<nint> seen = [];
        foreach (TerminalSession terminal in terminals.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (terminal.Process.HasExited)
            {
                continue;
            }
            nint window = FindTerminalWindow(terminal);
            nint root = window == 0 ? 0 : GetAncestor(window, 3);
            window = root == 0 ? window : root;
            if (window != 0 && IsWindowVisible(window) && seen.Add(window))
            {
                windows.Add(window);
            }
        }
        if (windows.Count == 0)
        {
            return Task.FromResult(new AssistantGatherResult([]));
        }

        int columns = (int)Math.Ceiling(Math.Sqrt(windows.Count));
        int rows = (int)Math.Ceiling((double)windows.Count / columns);
        const uint noActivateOrZOrder = 0x0010 | 0x0004 | 0x0200;
        AssistantWindowBounds[] tiledBounds = new AssistantWindowBounds[windows.Count];
        for (int index = 0; index < windows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint window = windows[index];
            if (IsIconic(window) || IsZoomed(window))
            {
                ShowWindow(window, 9);
            }
            int column = index % columns;
            int row = index / columns;
            int left = workArea.Left + column * workArea.Width / columns;
            int right = workArea.Left + (column + 1) * workArea.Width / columns;
            int top = workArea.Top + row * workArea.Height / rows;
            int bottom = workArea.Top + (row + 1) * workArea.Height / rows;
            if (!SetWindowPos(window, 0, left, top, right - left, bottom - top, noActivateOrZOrder))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not tile a managed terminal window.");
            }
            tiledBounds[index] = TryGetVisibleWindowBounds(window, out NativeRect actual)
                ? new(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top)
                : new(left, top, right - left, bottom - top);
        }
        return Task.FromResult(new AssistantGatherResult(tiledBounds));
    }

    public Task<AssistantSessionTarget?> CaptureSessionTargetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? targetKey = Volatile.Read(ref lastFocusedSessionKey);
        if (TryResolveHandoffTarget(targetKey, out AssistantSessionTarget? target))
        {
            return Task.FromResult<AssistantSessionTarget?>(target);
        }
        string? terminalIdentifier = Volatile.Read(ref lastOpenedTerminalIdentifier);
        if (terminalIdentifier is not null &&
            terminals.TryGetValue(terminalIdentifier, out TerminalSession? terminal) &&
            terminal.ForegroundSessionId is { } sessionId)
        {
            string fallbackKey = Key(terminal, sessionId);
            if (TryResolveHandoffTarget(fallbackKey, out target))
            {
                Volatile.Write(ref lastFocusedSessionKey, fallbackKey);
                return Task.FromResult<AssistantSessionTarget?>(target);
            }
        }
        return Task.FromResult<AssistantSessionTarget?>(null);
    }

    public async Task<AssistantPromptInjection?> InjectPromptAsync(
        AssistantSessionTarget target,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!TryResolvePromptTarget(target.SessionId, out TerminalSession terminal, out SessionActivity activity))
        {
            return null;
        }
        CopilotSession session = activity.Session
            ?? throw new InvalidOperationException("The selected Copilot session is no longer attached.");
        await session.SendAsync(prompt, cancellationToken);
        nint window = FindTerminalWindow(terminal);
        AssistantWindowBounds? bounds = window != 0 && TryGetVisibleWindowBounds(window, out NativeRect rectangle)
            ? new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top)
            : null;
        return new(target.SessionId, bounds);
    }

    private bool TryResolvePromptTarget(
        string? targetKey,
        out TerminalSession terminal,
        out SessionActivity activity)
    {
        terminal = null!;
        activity = null!;
        if (targetKey is null)
        {
            return false;
        }
        string[] parts = targetKey.Split('/', 2);
        if (parts.Length != 2 ||
            !terminals.TryGetValue(parts[0], out TerminalSession? foundTerminal) ||
            foundTerminal.Process.HasExited ||
            !foundTerminal.Sessions.TryGetValue(parts[1], out SessionActivity? foundActivity) ||
            foundActivity.Session is null)
        {
            return false;
        }
        terminal = foundTerminal;
        activity = foundActivity;
        return true;
    }

    private bool TryResolveHandoffTarget(string? targetKey, out AssistantSessionTarget? target)
    {
        target = null;
        if (targetKey is null)
        {
            return false;
        }
        string[] parts = targetKey.Split('/', 2);
        if (parts.Length != 2 ||
            !terminals.TryGetValue(parts[0], out TerminalSession? terminal) ||
            terminal.Process.HasExited ||
            !IsTerminalFocused(terminal) ||
            !terminal.Sessions.TryGetValue(parts[1], out SessionActivity? activity) ||
            activity.Session is null)
        {
            return false;
        }
        nint window = FindTerminalWindow(terminal);
        AssistantWindowBounds? bounds = window != 0 && TryGetVisibleWindowBounds(window, out NativeRect rectangle)
            ? new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top)
            : null;
        target = new(targetKey, bounds);
        return true;
    }

    private static bool TryGetVisibleWindowBounds(nint window, out NativeRect rectangle)
    {
        const uint extendedFrameBounds = 9;
        return DwmGetWindowAttribute(
                window,
                extendedFrameBounds,
                out rectangle,
                (uint)Marshal.SizeOf<NativeRect>()) == 0 ||
            GetWindowRect(window, out rectangle);
    }

    public async Task<AssistantHandoff?> StartHandoffAsync(
        AssistantSessionTarget target,
        HandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        string[] parts = target.SessionId.Split('/', 2);
        if (parts.Length != 2 || !terminals.TryGetValue(parts[0], out TerminalSession? terminal) ||
            terminal.Process.HasExited || !terminal.Sessions.TryGetValue(parts[1], out SessionActivity? activity) ||
            activity.Session is null)
        {
            return null;
        }
        string sourceKey = target.SessionId;
        string uniqueSource = $"{sourceKey}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uniqueSource)))[..12]
            .ToLowerInvariant();
        string handoffDirectory = Path.Combine(Path.GetTempPath(), "CopilotBuddy", "handoffs");
        Directory.CreateDirectory(handoffDirectory);
        string handoffPath = Path.Combine(handoffDirectory, $"handoff-{suffix}.md");
        string sourcePrompt = $"""
            Write a handoff document for a fresh agent to continue the current work.

            {request.OutputInstructions}

            Include a "suggested skills" section naming which skills the next agent should call with the Skill tool.

            Do not duplicate content already captured in other artifacts such as specs, plans, ADRs, issues, commits, or diffs. Reference them by path or URL instead.

            Redact sensitive information such as API keys, passwords, credentials, or personally identifiable information.

            Save the document to this exact path: `{handoffPath}`
            """;
        AssistantHandoff handoff = new(sourceKey, activity.Title, handoffPath, terminal.WorkingDirectory);
        pendingHandoffs[sourceKey] = handoff;
        try
        {
            await activity.Session.SendAsync(sourcePrompt, cancellationToken);
        }
        catch
        {
            pendingHandoffs.TryRemove(sourceKey, out _);
            throw;
        }
        return handoff;
    }

    public Task CloseHandoffSourceAsync(AssistantHandoff handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        cancellationToken.ThrowIfCancellationRequested();
        string[] parts = handoff.SourceSessionId.Split('/', 2);
        if (parts.Length != 2 || !terminals.TryGetValue(parts[0], out TerminalSession? terminal) ||
            terminal.Process.HasExited)
        {
            return Task.CompletedTask;
        }
        nint window = FindTerminalWindow(terminal);
        if (window == 0 || !PostMessage(window, 0x0010, 0, 0))
        {
            throw new InvalidOperationException("Could not request that terminal window to close.");
        }
        return Task.CompletedTask;
    }

    public Task OpenHandoffAsync(AssistantHandoff handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        string continuationPrompt =
            $"Read the handoff document at `{handoff.ArtifactPath}` and continue the work from it. " +
            "Follow its instructions and references. Give this session a concise, task-specific summary name " +
            "based on the handoff contents instead of a generic name such as \"continue handoff document\". " +
            "Do not ask the user to paste the handoff.";
        return Task.Run(
            () => Open(handoff.WorkingDirectory, continuationPrompt, null, false, cancellationToken),
            cancellationToken);
    }

    public Task<AssistantStoredSession?> StoreSessionAsync(
        AssistantSessionTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        string[] parts = target.SessionId.Split('/', 2);
        if (parts.Length != 2 ||
            !terminals.TryGetValue(parts[0], out TerminalSession? terminal) ||
            terminal.Process.HasExited ||
            !terminal.Sessions.TryGetValue(parts[1], out SessionActivity? activity) ||
            activity.Session is null)
        {
            return Task.FromResult<AssistantStoredSession?>(null);
        }
        return Task.FromResult<AssistantStoredSession?>(new(
            parts[1],
            activity.Title,
            terminal.WorkingDirectory));
    }

    public async Task OpenStoredSessionAsync(
        AssistantStoredSession storedSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storedSession);
        TerminalSession terminal = await Task.Run(
            () => Open(
                storedSession.WorkingDirectory,
                null,
                storedSession.SessionId,
                true,
                cancellationToken),
            cancellationToken);
        try
        {
            await terminal.Attached!.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch
        {
            if (!terminal.Process.HasExited)
            {
                nint window = FindTerminalWindow(terminal);
                if (window != 0)
                {
                    PostMessage(window, 0x0010, 0, 0);
                }
            }
            throw;
        }
    }

    public Task CloseSessionSourceAsync(
        AssistantSessionTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        string[] parts = target.SessionId.Split('/', 2);
        if (parts.Length != 2 ||
            !terminals.TryGetValue(parts[0], out TerminalSession? terminal) ||
            terminal.Process.HasExited)
        {
            return Task.CompletedTask;
        }
        nint window = FindTerminalWindow(terminal);
        if (window == 0 || !PostMessage(window, 0x0010, 0, 0))
        {
            throw new InvalidOperationException("Could not request that terminal window to close.");
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        await Task.WhenAll(terminals.Values.Select(terminal => terminal.Monitor));
        shutdown.Dispose();
    }

    private static nint FindConsoleWindow(int processId)
    {
        if (GetConsoleWindow() != 0 || !AttachConsole((uint)processId))
        {
            return 0;
        }
        try
        {
            nint console = GetConsoleWindow();
            nint owner = GetAncestor(console, 3);
            if (owner != 0 && IsWindowVisible(owner))
            {
                return owner;
            }
            return IsWindowVisible(console) ? console : 0;
        }
        finally
        {
            FreeConsole();
        }
    }

    private static nint FindTerminalWindow(TerminalSession terminal)
    {
        terminal.Process.Refresh();
        nint window = terminal.Process.MainWindowHandle;
        if (window == 0)
        {
            window = FindConsoleWindow(terminal.Process.Id);
        }
        if (window != 0)
        {
            return window;
        }
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out uint processId);
            if (processId == terminal.Process.Id && IsWindowVisible(candidate))
            {
                window = candidate;
                return false;
            }
            return true;
        }, 0);
        if (window == 0)
        {
            window = FindWindowsTerminalWindow();
        }
        return window;
    }

    private static nint FindWindowsTerminalWindow()
    {
        nint foreground = GetForegroundWindow();
        if (IsWindowsTerminalWindow(foreground))
        {
            return foreground;
        }
        nint window = 0;
        EnumWindows((candidate, _) =>
        {
            if (!IsWindowsTerminalWindow(candidate)) return true;
            window = candidate;
            return false;
        }, 0);
        return window;
    }

    private static bool IsWindowsTerminalWindow(nint window)
    {
        if (window == 0 || !IsWindowVisible(window))
        {
            return false;
        }
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsFocusedSession(TerminalSession terminal, string sessionId) =>
        string.Equals(terminal.ForegroundSessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(Volatile.Read(ref lastFocusedSessionKey), Key(terminal, sessionId), StringComparison.Ordinal) &&
        IsTerminalFocused(terminal);

    private static bool IsTerminalFocused(TerminalSession terminal)
    {
        nint foreground = GetForegroundWindow();
        nint terminalWindow = FindTerminalWindow(terminal);
        if (foreground == 0 || terminalWindow == 0)
        {
            return false;
        }
        nint foregroundRoot = GetAncestor(foreground, 3);
        nint terminalRoot = GetAncestor(terminalWindow, 3);
        return (foregroundRoot == 0 ? foreground : foregroundRoot) ==
            (terminalRoot == 0 ? terminalWindow : terminalRoot);
    }

    private static string Key(TerminalSession terminal, string sessionId) => $"{terminal.Identifier}/{sessionId}";

    private void PublishManualFocus(TerminalSession terminal)
    {
        string? sessionId = terminal.ForegroundSessionId;
        string sessionKey = sessionId is null ? "" : Key(terminal, sessionId);
        if (sessionId is null ||
            !string.Equals(Volatile.Read(ref lastFocusedSessionKey), sessionKey, StringComparison.Ordinal) ||
            !IsTerminalFocused(terminal))
        {
            return;
        }
        SessionFocused?.Invoke(this, sessionKey);
    }

    private void PublishContextUsage(TerminalSession terminal, string sessionId, SessionActivity activity,
        double tokens, double limit)
    {
        activity.Usage = new SessionContextUsage(Key(terminal, sessionId), activity.Title, tokens, limit);
        ContextUsageChanged?.Invoke(this, activity.Usage);
    }

    private sealed class SessionActivity
    {
        public bool Busy { get; set; }
        public string Title { get; set; } = "Copilot";
        public SessionContextUsage? Usage { get; set; }
        public CopilotSession? Session { get; set; }
    }
    private sealed class TerminalSession(
        string identifier,
        Process process,
        int port,
        string token,
        string workingDirectory,
        string? pendingPrompt,
        string? expectedSessionId)
    {
        public string Identifier { get; } = identifier;
        public Process Process { get; } = process;
        public int Port { get; } = port;
        public string Token { get; } = token;
        public string WorkingDirectory { get; } = workingDirectory;
        public string? ExpectedSessionId { get; } = expectedSessionId;
        public TaskCompletionSource<bool>? Attached { get; } = expectedSessionId is null
            ? null
            : new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? PendingPrompt { get; set; } = pendingPrompt;
        public CopilotClient? Client { get; set; }
        public string? ForegroundSessionId { get; set; }
        public ConcurrentQueue<string> Discovered { get; } = new();
        public ConcurrentDictionary<string, SessionActivity> Sessions { get; } = new();
        public Task Monitor { get; set; } = Task.CompletedTask;
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
        nint window, uint attribute, out NativeRect value, uint valueSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}