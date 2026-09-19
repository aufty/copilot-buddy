using System.IO.Pipes;
using System.Text.Json;
using CopilotBuddy.Core;

namespace CopilotBuddy.Copilot;

public static class CopilotSessionBrokerHost
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using Mutex instance = new(true, "Local\\CopilotBuddy.AssistantBroker.v11", out bool firstInstance);
        if (!firstInstance)
        {
            return;
        }
        await using CopilotSessions sessions = new();
        BrokerState state = new(sessions);
        await state.RunAsync(cancellationToken);
    }

    private sealed class BrokerState
    {
        private readonly CopilotSessions sessions;
        private readonly object stateLock = new();
        private readonly List<BrokerConnection> clients = [];
        private readonly Dictionary<string, SessionAttention> attention = [];
        private readonly Dictionary<string, SessionContextUsage> usage = [];
        private readonly Dictionary<string, AssistantStoredSession> storedSessions;
        private static readonly JsonSerializerOptions StoredSessionJsonOptions =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public BrokerState(CopilotSessions sessions)
        {
            this.sessions = sessions;
            storedSessions = LoadStoredSessions();
            sessions.AttentionRequested += (_, value) =>
            {
                lock (stateLock) attention[value.SessionId] = value;
                Broadcast(AssistantBrokerProtocol.AttentionRequested, value);
            };
            sessions.ContextUsageChanged += (_, value) =>
            {
                lock (stateLock) usage[value.SessionId] = value;
                Broadcast(AssistantBrokerProtocol.ContextUsageChanged, value);
            };
            sessions.HandoffCompleted += (_, value) =>
                Broadcast(AssistantBrokerProtocol.HandoffCompleted, value);
            sessions.SessionFocused += (_, sessionId) =>
            {
                lock (stateLock) attention.Remove(sessionId);
                Broadcast(AssistantBrokerProtocol.SessionFocused, sessionId);
            };
            sessions.SessionClosed += (_, sessionId) =>
            {
                lock (stateLock)
                {
                    attention.Remove(sessionId);
                    usage.Remove(sessionId);
                }
                Broadcast(AssistantBrokerProtocol.SessionClosed, sessionId);
            };
            sessions.ConnectionFailed += (_, message) =>
                Broadcast(AssistantBrokerProtocol.ConnectionFailed, message);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = new(
                    AssistantBrokerProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    BrokerConnection connection = new(pipe);
                    lock (stateLock) clients.Add(connection);
                    _ = HandleConnectionAsync(connection, cancellationToken);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            }
        }

        private async Task HandleConnectionAsync(BrokerConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                await SendSnapshotAsync(connection, cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await connection.Reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        return;
                    }
                    AssistantBrokerMessage? request =
                        JsonSerializer.Deserialize<AssistantBrokerMessage>(line, PipeProtocol.JsonOptions);
                    if (request is null || request.Kind != AssistantBrokerProtocol.Request || request.Id is null)
                    {
                        continue;
                    }
                    AssistantBrokerMessage response = await HandleRequestAsync(request, cancellationToken);
                    await connection.SendAsync(response, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or JsonException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
            finally
            {
                lock (stateLock) clients.Remove(connection);
                connection.Dispose();
            }
        }

        private async Task<AssistantBrokerMessage> HandleRequestAsync(
            AssistantBrokerMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Version != AssistantBrokerProtocol.Version)
            {
                return Failure(request, $"Unsupported broker protocol version {request.Version}.");
            }
            try
            {
                object? result = request.Name switch
                {
                    AssistantBrokerProtocol.ConfigureLaunch => ConfigureLaunch(
                        Deserialize<AssistantLaunchCommand>(request)),
                    AssistantBrokerProtocol.Open => await OpenAsync(
                        Deserialize<BrokerOpenRequest>(request).WorkingDirectory, cancellationToken),
                    AssistantBrokerProtocol.Focus => await sessions.FocusAsync(
                        Deserialize<BrokerFocusRequest>(request).SessionId, cancellationToken),
                    AssistantBrokerProtocol.Gather => await sessions.GatherAsync(
                        Deserialize<AssistantWindowBounds>(request), cancellationToken),
                    AssistantBrokerProtocol.CaptureSessionTarget =>
                        await sessions.CaptureSessionTargetAsync(cancellationToken),
                    AssistantBrokerProtocol.InjectPrompt => await InjectPromptAsync(
                        Deserialize<BrokerInjectPromptRequest>(request), cancellationToken),
                    AssistantBrokerProtocol.StartHandoff => await StartHandoffAsync(
                        Deserialize<BrokerStartHandoffRequest>(request), cancellationToken),
                    AssistantBrokerProtocol.CloseHandoffSource => await CloseHandoffSourceAsync(
                        Deserialize<AssistantHandoff>(request), cancellationToken),
                    AssistantBrokerProtocol.OpenHandoff => await OpenHandoffAsync(
                        Deserialize<AssistantHandoff>(request), cancellationToken),
                    AssistantBrokerProtocol.StoreSession => await StoreSessionAsync(
                        Deserialize<AssistantStoreRequest>(request), cancellationToken),
                    AssistantBrokerProtocol.CloseStoredSessionSource => await CloseStoredSessionSourceAsync(
                        Deserialize<AssistantSessionTarget>(request), cancellationToken),
                    AssistantBrokerProtocol.OpenStoredSession => await OpenStoredSessionAsync(
                        Deserialize<AssistantStoredSession>(request), cancellationToken),
                    _ => throw new InvalidOperationException($"Unknown assistant broker method '{request.Name}'.")
                };
                return Success(request, result);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(request, exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Failure(request, exception.Message);
            }
        }

        private bool ConfigureLaunch(AssistantLaunchCommand command)
        {
            sessions.ConfigureLaunch(command);
            return true;
        }

        private async Task<bool> OpenAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            await sessions.OpenAsync(workingDirectory, cancellationToken);
            return true;
        }

        private Task<AssistantPromptInjection?> InjectPromptAsync(
            BrokerInjectPromptRequest request,
            CancellationToken cancellationToken) =>
            sessions.InjectPromptAsync(request.Target, request.Prompt, cancellationToken);

        private Task<AssistantHandoff?> StartHandoffAsync(
            BrokerStartHandoffRequest request,
            CancellationToken cancellationToken) =>
            sessions.StartHandoffAsync(request.Target, request.Request, cancellationToken);

        private async Task<bool> CloseHandoffSourceAsync(
            AssistantHandoff handoff,
            CancellationToken cancellationToken)
        {
            await sessions.CloseHandoffSourceAsync(handoff, cancellationToken);
            return true;
        }

        private async Task<bool> OpenHandoffAsync(
            AssistantHandoff handoff,
            CancellationToken cancellationToken)
        {
            await sessions.OpenHandoffAsync(handoff, cancellationToken);
            return true;
        }

        private async Task<AssistantStoredSession?> StoreSessionAsync(
            AssistantStoreRequest request,
            CancellationToken cancellationToken)
        {
            AssistantStoredSession? storedSession =
                await sessions.StoreSessionAsync(request, cancellationToken);
            if (storedSession is null)
            {
                return null;
            }
            bool added;
            lock (stateLock)
            {
                added = !storedSessions.ContainsKey(storedSession.SessionId);
                if (added)
                {
                    Dictionary<string, AssistantStoredSession> next = new(storedSessions)
                    {
                        [storedSession.SessionId] = storedSession
                    };
                    SaveStoredSessions(next.Values);
                    storedSessions[storedSession.SessionId] = storedSession;
                }
            }
            if (added)
            {
                Broadcast(AssistantBrokerProtocol.StoredSessionAdded, storedSession);
            }
            return storedSession;
        }

        private async Task<bool> CloseStoredSessionSourceAsync(
            AssistantSessionTarget target,
            CancellationToken cancellationToken)
        {
            await sessions.CloseSessionSourceAsync(target, cancellationToken);
            return true;
        }

        private async Task<bool> OpenStoredSessionAsync(
            AssistantStoredSession storedSession,
            CancellationToken cancellationToken)
        {
            lock (stateLock)
            {
                if (!storedSessions.ContainsKey(storedSession.SessionId))
                {
                    throw new InvalidOperationException("That stored session is no longer available.");
                }
            }
            await sessions.OpenStoredSessionAsync(storedSession, cancellationToken);
            lock (stateLock)
            {
                Dictionary<string, AssistantStoredSession> next = new(storedSessions);
                next.Remove(storedSession.SessionId);
                SaveStoredSessions(next.Values);
                storedSessions.Remove(storedSession.SessionId);
            }
            Broadcast(AssistantBrokerProtocol.StoredSessionRemoved, storedSession.SessionId);
            return true;
        }

        private async Task SendSnapshotAsync(BrokerConnection connection, CancellationToken cancellationToken)
        {
            AssistantBrokerMessage[] snapshot;
            lock (stateLock)
            {
                snapshot =
                [
                    .. attention.Values.Select(value => Event(AssistantBrokerProtocol.AttentionRequested, value)),
                    .. usage.Values.Select(value => Event(AssistantBrokerProtocol.ContextUsageChanged, value)),
                    .. storedSessions.Values.Select(value => Event(AssistantBrokerProtocol.StoredSessionAdded, value))
                ];
            }
            foreach (AssistantBrokerMessage message in snapshot)
            {
                await connection.SendAsync(message, cancellationToken);
            }
        }

        private void Broadcast<T>(string name, T payload)
        {
            BrokerConnection[] snapshot;
            lock (stateLock) snapshot = clients.ToArray();
            AssistantBrokerMessage message = Event(name, payload);
            _ = Task.Run(async () =>
            {
                foreach (BrokerConnection client in snapshot)
                {
                    try
                    {
                        await client.SendAsync(message, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                    {
                        System.Diagnostics.Debug.WriteLine(exception);
                    }
                }
            });
        }

        private static T Deserialize<T>(AssistantBrokerMessage message) =>
            message.Payload is { } payload
                ? payload.Deserialize<T>(PipeProtocol.JsonOptions)
                    ?? throw new InvalidOperationException("The broker request payload was empty.")
                : throw new InvalidOperationException("The broker request payload was missing.");

        private static AssistantBrokerMessage Success(AssistantBrokerMessage request, object? payload) => new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Response,
            request.Id,
            request.Name,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        private static AssistantBrokerMessage Failure(AssistantBrokerMessage request, string error) => new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Response,
            request.Id,
            request.Name,
            Success: false,
            Error: error);

        private static AssistantBrokerMessage Event<T>(string name, T payload) => new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Event,
            Name: name,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        private static Dictionary<string, AssistantStoredSession> LoadStoredSessions()
        {
            if (!File.Exists(StoredSessionsPath))
            {
                return [];
            }
            AssistantStoredSession[] sessions =
                JsonSerializer.Deserialize<AssistantStoredSession[]>(
                    File.ReadAllText(StoredSessionsPath),
                    StoredSessionJsonOptions)
                ?? throw new InvalidDataException("The stored sessions file was empty.");
            return sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        }

        private static void SaveStoredSessions(IEnumerable<AssistantStoredSession> sessions)
        {
            string directory = Path.GetDirectoryName(StoredSessionsPath)!;
            Directory.CreateDirectory(directory);
            string temporaryPath = StoredSessionsPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(sessions.ToArray(), StoredSessionJsonOptions));
            File.Move(temporaryPath, StoredSessionsPath, true);
        }

        private static string StoredSessionsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotBuddy",
            "stored-sessions.json");
    }

    private sealed class BrokerConnection(NamedPipeServerStream pipe) : IDisposable
    {
        private readonly StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        private readonly SemaphoreSlim writeLock = new(1, 1);

        public StreamReader Reader { get; } = new(pipe, leaveOpen: true);

        public async Task SendAsync(AssistantBrokerMessage message, CancellationToken cancellationToken)
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

        public void Dispose()
        {
            pipe.Dispose();
            Reader.Dispose();
            writer.Dispose();
            writeLock.Dispose();
        }
    }
}
