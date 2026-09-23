using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotBuddy.Core;

public static class AssistantBrokerProtocol
{
    public const int Version = 15;
    public const string PipeName = "CopilotBuddy.AssistantBroker.v15";
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
    public const string Shutdown = "shutdown";
    public const string ConfigureLaunch = "configure-launch";
    public const string Open = "open";
    public const string Focus = "focus";
    public const string Gather = "gather";
    public const string CaptureSessionTarget = "capture-session-target";
    public const string InjectPrompt = "inject-prompt";
    public const string StartHandoff = "start-handoff";
    public const string CloseHandoffSource = "close-handoff-source";
    public const string OpenHandoff = "open-handoff";
    public const string StoreSession = "store-session";
    public const string CloseStoredSessionSource = "close-stored-session-source";
    public const string OpenStoredSession = "open-stored-session";
    public const string AttentionRequested = "attention-requested";
    public const string ContextUsageChanged = "context-usage-changed";
    public const string HandoffCompleted = "handoff-completed";
    public const string StoredSessionAdded = "stored-session-added";
    public const string StoredSessionRemoved = "stored-session-removed";
    public const string SessionFocused = "session-focused";
    public const string SessionClosed = "session-closed";
    public const string ConnectionFailed = "connection-failed";
}

public sealed record AssistantBrokerMessage(
    int Version,
    string Kind,
    Guid? Id = null,
    string? Name = null,
    bool Success = true,
    JsonElement? Payload = null,
    string? Error = null);

public sealed record BrokerOpenRequest(string WorkingDirectory);
public sealed record BrokerFocusRequest(string SessionId);
public sealed record BrokerInjectPromptRequest(AssistantSessionTarget Target, string Prompt);
public sealed record BrokerStartHandoffRequest(AssistantSessionTarget Target, HandoffRequest Request);

public sealed record AssistantLaunchCommand(string Executable, string[] Arguments)
{
    public static AssistantLaunchCommand Default { get; } = new("copilot", []);

    public static AssistantLaunchCommand Parse(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        List<string> arguments = [];
        StringBuilder current = new();
        bool quoted = false;
        bool started = false;

        for (int index = 0; index < commandLine.Length;)
        {
            char character = commandLine[index];
            if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
                index++;
                continue;
            }

            if (character == '\\')
            {
                int slashStart = index;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    index++;
                }
                int slashCount = index - slashStart;
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                    {
                        quoted = !quoted;
                    }
                    else
                    {
                        current.Append('"');
                    }
                    index++;
                }
                else
                {
                    current.Append('\\', slashCount);
                }
                started = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                started = true;
                index++;
                continue;
            }

            current.Append(character);
            started = true;
            index++;
        }

        if (quoted)
        {
            throw new FormatException("The launch command contains an unmatched quote.");
        }
        if (started)
        {
            arguments.Add(current.ToString());
        }
        if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]))
        {
            throw new FormatException("Enter the executable used to launch Copilot.");
        }
        return new(arguments[0], arguments.Skip(1).ToArray());
    }

    public override string ToString() =>
        string.Join(" ", new[] { Executable }.Concat(Arguments).Select(Quote));

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        StringBuilder quoted = new("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', slashes * 2 + 1);
                quoted.Append('"');
            }
            else
            {
                quoted.Append('\\', slashes);
                quoted.Append(character);
            }
            slashes = 0;
        }
        quoted.Append('\\', slashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}

public interface IConfigurableAssistantSessions
{
    Task ConfigureLaunchAsync(AssistantLaunchCommand command, CancellationToken cancellationToken);
}

public interface IAssistantBrokerControl
{
    Task ShutdownAsync(CancellationToken cancellationToken);
}

public sealed class AssistantSessionBrokerClient :
    IStoredAssistantSessions,
    IConfigurableAssistantSessions,
    IAssistantBrokerControl
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AssistantBrokerMessage>> pending = new();
    private readonly object eventLock = new();
    private readonly Dictionary<string, SessionAttention> cachedAttention = [];
    private readonly Dictionary<string, SessionContextUsage> cachedUsage = [];
    private readonly Dictionary<string, AssistantStoredSession> cachedStoredSessions = [];
    private EventHandler<SessionAttention>? attentionRequested;
    private EventHandler<SessionContextUsage>? contextUsageChanged;
    private EventHandler<AssistantHandoff>? handoffCompleted;
    private EventHandler<AssistantStoredSession>? storedSessionAdded;
    private EventHandler<string>? storedSessionRemoved;
    private EventHandler<string>? sessionFocused;
    private EventHandler<string>? sessionClosed;
    private EventHandler<string>? connectionFailed;
    private readonly Task readerTask;

    private AssistantSessionBrokerClient(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        reader = new StreamReader(pipe, leaveOpen: true);
        writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        readerTask = Task.Run(() => ReadMessagesAsync(shutdown.Token));
    }

    public event EventHandler<SessionAttention>? AttentionRequested
    {
        add
        {
            SessionAttention[] snapshot;
            lock (eventLock)
            {
                attentionRequested += value;
                snapshot = cachedAttention.Values.ToArray();
            }
            foreach (SessionAttention attention in snapshot)
            {
                value?.Invoke(this, attention);
            }
        }
        remove
        {
            lock (eventLock) attentionRequested -= value;
        }
    }

    public event EventHandler<SessionContextUsage>? ContextUsageChanged
    {
        add
        {
            SessionContextUsage[] snapshot;
            lock (eventLock)
            {
                contextUsageChanged += value;
                snapshot = cachedUsage.Values.ToArray();
            }
            foreach (SessionContextUsage usage in snapshot)
            {
                value?.Invoke(this, usage);
            }
        }
        remove
        {
            lock (eventLock) contextUsageChanged -= value;
        }
    }

    public event EventHandler<AssistantHandoff>? HandoffCompleted
    {
        add { lock (eventLock) handoffCompleted += value; }
        remove { lock (eventLock) handoffCompleted -= value; }
    }

    public event EventHandler<AssistantStoredSession>? StoredSessionAdded
    {
        add
        {
            AssistantStoredSession[] snapshot;
            lock (eventLock)
            {
                storedSessionAdded += value;
                snapshot = cachedStoredSessions.Values.ToArray();
            }
            foreach (AssistantStoredSession storedSession in snapshot)
            {
                value?.Invoke(this, storedSession);
            }
        }
        remove { lock (eventLock) storedSessionAdded -= value; }
    }

    public event EventHandler<string>? StoredSessionRemoved
    {
        add { lock (eventLock) storedSessionRemoved += value; }
        remove { lock (eventLock) storedSessionRemoved -= value; }
    }

    public event EventHandler<string>? SessionFocused
    {
        add { lock (eventLock) sessionFocused += value; }
        remove { lock (eventLock) sessionFocused -= value; }
    }

    public event EventHandler<string>? SessionClosed
    {
        add { lock (eventLock) sessionClosed += value; }
        remove { lock (eventLock) sessionClosed -= value; }
    }

    public event EventHandler<string>? ConnectionFailed
    {
        add { lock (eventLock) connectionFailed += value; }
        remove { lock (eventLock) connectionFailed -= value; }
    }

    public static async Task<AssistantSessionBrokerClient> ConnectAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        NamedPipeClientStream? pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        if (pipe is null)
        {
            string brokerExecutable = PrepareBrokerExecutable(executablePath);
            ProcessStartInfo start = new(brokerExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--assistant-broker");
            Process.Start(start)?.Dispose();
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (pipe is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(150, cancellationToken);
                pipe = await TryConnectAsync(TimeSpan.FromMilliseconds(300), cancellationToken);
            }
        }
        return pipe is null
            ? throw new IOException("Could not connect to the Copilot Buddy assistant broker.")
            : new AssistantSessionBrokerClient(pipe);
    }

    private static string PrepareBrokerExecutable(string executablePath)
    {
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new InvalidOperationException("Could not locate the broker source directory.");
        string[] identityFiles =
        [
            executablePath,
            Path.Combine(sourceDirectory, "CopilotBuddy.Composition.dll"),
            Path.Combine(sourceDirectory, "CopilotBuddy.Core.dll"),
            Path.Combine(sourceDirectory, "CopilotBuddy.Copilot.dll")
        ];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in identityFiles.Where(File.Exists))
        {
            using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        string version = Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
        string targetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotBuddy",
            "broker",
            version);
        Directory.CreateDirectory(targetDirectory);
        foreach (string source in Directory.EnumerateFiles(sourceDirectory))
        {
            string extension = Path.GetExtension(source);
            if (extension is not (".exe" or ".dll" or ".json"))
            {
                continue;
            }
            string target = Path.Combine(targetDirectory, Path.GetFileName(source));
            if (!File.Exists(target))
            {
                File.Copy(source, target);
            }
        }
        return Path.Combine(targetDirectory, Path.GetFileName(executablePath));
    }

    public async Task ConfigureLaunchAsync(
        AssistantLaunchCommand command,
        CancellationToken cancellationToken) =>
        await RequestAsync<AssistantLaunchCommand, bool>(
            AssistantBrokerProtocol.ConfigureLaunch, command, cancellationToken);

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await RequestAsync<object, bool>(
            AssistantBrokerProtocol.Shutdown, new(), cancellationToken);
        await shutdown.CancelAsync();
    }

    public async Task OpenAsync(string workingDirectory, CancellationToken cancellationToken) =>
        await RequestAsync<BrokerOpenRequest, bool>(
            AssistantBrokerProtocol.Open, new(workingDirectory), cancellationToken);

    public Task<bool> FocusAsync(string sessionId, CancellationToken cancellationToken) =>
        RequestAsync<BrokerFocusRequest, bool>(
            AssistantBrokerProtocol.Focus, new(sessionId), cancellationToken);

    public Task<AssistantGatherResult> GatherAsync(
        AssistantWindowBounds workArea,
        CancellationToken cancellationToken) =>
        RequestAsync<AssistantWindowBounds, AssistantGatherResult>(
            AssistantBrokerProtocol.Gather, workArea, cancellationToken);

    public Task<AssistantSessionTarget?> CaptureSessionTargetAsync(CancellationToken cancellationToken) =>
        RequestAsync<object, AssistantSessionTarget?>(
            AssistantBrokerProtocol.CaptureSessionTarget, new(), cancellationToken);

    public Task<AssistantPromptInjection?> InjectPromptAsync(
        AssistantSessionTarget target,
        string prompt,
        CancellationToken cancellationToken) =>
        RequestAsync<BrokerInjectPromptRequest, AssistantPromptInjection?>(
            AssistantBrokerProtocol.InjectPrompt, new(target, prompt), cancellationToken);

    public Task<AssistantHandoff?> StartHandoffAsync(
        AssistantSessionTarget target,
        HandoffRequest request,
        CancellationToken cancellationToken) =>
        RequestAsync<BrokerStartHandoffRequest, AssistantHandoff?>(
            AssistantBrokerProtocol.StartHandoff, new(target, request), cancellationToken);

    public async Task CloseHandoffSourceAsync(AssistantHandoff handoff, CancellationToken cancellationToken) =>
        await RequestAsync<AssistantHandoff, bool>(
            AssistantBrokerProtocol.CloseHandoffSource, handoff, cancellationToken);

    public async Task OpenHandoffAsync(AssistantHandoff handoff, CancellationToken cancellationToken) =>
        await RequestAsync<AssistantHandoff, bool>(
            AssistantBrokerProtocol.OpenHandoff, handoff, cancellationToken);

    public Task<AssistantStoredSession?> StoreSessionAsync(
        AssistantStoreRequest request,
        CancellationToken cancellationToken) =>
        RequestAsync<AssistantStoreRequest, AssistantStoredSession?>(
            AssistantBrokerProtocol.StoreSession, request, cancellationToken);

    public async Task CloseStoredSessionSourceAsync(
        AssistantSessionTarget target,
        CancellationToken cancellationToken) =>
        await RequestAsync<AssistantSessionTarget, bool>(
            AssistantBrokerProtocol.CloseStoredSessionSource, target, cancellationToken);

    public async Task OpenStoredSessionAsync(
        AssistantStoredSession storedSession,
        CancellationToken cancellationToken) =>
        await RequestAsync<AssistantStoredSession, bool>(
            AssistantBrokerProtocol.OpenStoredSession, storedSession, cancellationToken);

    private static async Task<NamedPipeClientStream?> TryConnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = new(
            ".",
            AssistantBrokerProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);
            await pipe.ConnectAsync(attempt.Token);
            return pipe;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            return null;
        }
        catch (IOException)
        {
            pipe.Dispose();
            return null;
        }
    }

    private async Task<TResponse> RequestAsync<TRequest, TResponse>(
        string name,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        TaskCompletionSource<AssistantBrokerMessage> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not register the broker request.");
        }
        try
        {
            AssistantBrokerMessage request = new(
                AssistantBrokerProtocol.Version,
                AssistantBrokerProtocol.Request,
                id,
                name,
                Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));
            await WriteAsync(request, cancellationToken);
            AssistantBrokerMessage response = await completion.Task.WaitAsync(cancellationToken);
            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? "The assistant broker request failed.");
            }
            return response.Payload is { } responsePayload
                ? responsePayload.Deserialize<TResponse>(PipeProtocol.JsonOptions)!
                : default!;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                AssistantBrokerMessage? message =
                    JsonSerializer.Deserialize<AssistantBrokerMessage>(line, PipeProtocol.JsonOptions);
                if (message is null || message.Version != AssistantBrokerProtocol.Version)
                {
                    continue;
                }
                if (message.Kind == AssistantBrokerProtocol.Response && message.Id is { } id &&
                    pending.TryGetValue(id, out TaskCompletionSource<AssistantBrokerMessage>? completion))
                {
                    completion.TrySetResult(message);
                }
                else if (message.Kind == AssistantBrokerProtocol.Event)
                {
                    DispatchEvent(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or JsonException or ObjectDisposedException)
        {
            failure = exception;
        }
        finally
        {
            IOException disconnected = new(
                failure is null ? "The assistant broker disconnected." : $"The assistant broker disconnected: {failure.Message}",
                failure);
            foreach (TaskCompletionSource<AssistantBrokerMessage> completion in pending.Values)
            {
                completion.TrySetException(disconnected);
            }
            if (!shutdown.IsCancellationRequested)
            {
                Invoke(connectionFailed, disconnected.Message);
            }
        }
    }

    private void DispatchEvent(AssistantBrokerMessage message)
    {
        if (message.Payload is not { } payload)
        {
            return;
        }
        switch (message.Name)
        {
            case AssistantBrokerProtocol.AttentionRequested:
                SessionAttention attention = payload.Deserialize<SessionAttention>(PipeProtocol.JsonOptions)!;
                EventHandler<SessionAttention>? attentionHandler;
                lock (eventLock)
                {
                    cachedAttention[attention.SessionId] = attention;
                    attentionHandler = attentionRequested;
                }
                attentionHandler?.Invoke(this, attention);
                break;
            case AssistantBrokerProtocol.ContextUsageChanged:
                SessionContextUsage usage = payload.Deserialize<SessionContextUsage>(PipeProtocol.JsonOptions)!;
                EventHandler<SessionContextUsage>? usageHandler;
                lock (eventLock)
                {
                    cachedUsage[usage.SessionId] = usage;
                    usageHandler = contextUsageChanged;
                }
                usageHandler?.Invoke(this, usage);
                break;
            case AssistantBrokerProtocol.HandoffCompleted:
                Invoke(handoffCompleted, payload.Deserialize<AssistantHandoff>(PipeProtocol.JsonOptions)!);
                break;
            case AssistantBrokerProtocol.StoredSessionAdded:
                AssistantStoredSession storedSession =
                    payload.Deserialize<AssistantStoredSession>(PipeProtocol.JsonOptions)!;
                lock (eventLock) cachedStoredSessions[storedSession.SessionId] = storedSession;
                Invoke(storedSessionAdded, storedSession);
                break;
            case AssistantBrokerProtocol.StoredSessionRemoved:
                string removed = payload.Deserialize<string>(PipeProtocol.JsonOptions)!;
                lock (eventLock) cachedStoredSessions.Remove(removed);
                Invoke(storedSessionRemoved, removed);
                break;
            case AssistantBrokerProtocol.SessionFocused:
                string focused = payload.Deserialize<string>(PipeProtocol.JsonOptions)!;
                lock (eventLock) cachedAttention.Remove(focused);
                Invoke(sessionFocused, focused);
                break;
            case AssistantBrokerProtocol.SessionClosed:
                string closed = payload.Deserialize<string>(PipeProtocol.JsonOptions)!;
                lock (eventLock)
                {
                    cachedAttention.Remove(closed);
                    cachedUsage.Remove(closed);
                }
                Invoke(sessionClosed, closed);
                break;
            case AssistantBrokerProtocol.ConnectionFailed:
                Invoke(connectionFailed, payload.Deserialize<string>(PipeProtocol.JsonOptions)!);
                break;
        }
    }

    private void Invoke<T>(EventHandler<T>? handler, T value)
    {
        EventHandler<T>? snapshot;
        lock (eventLock) snapshot = handler;
        snapshot?.Invoke(this, value);
    }

    private async Task WriteAsync(AssistantBrokerMessage message, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, PipeProtocol.JsonOptions));
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        pipe.Dispose();
        try { await readerTask; }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        reader.Dispose();
        writer.Dispose();
        writeLock.Dispose();
        shutdown.Dispose();
    }
}
