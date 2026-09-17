namespace TaskbarBuddy.Core;

public sealed record SessionAttention(string SessionId, string Message);

public enum HandoffOutput
{
    Specification,
    ResearchInstructions,
    ImplementationInstructions,
    Custom
}

public sealed record HandoffRequest(HandoffOutput Output, string? CustomInstructions = null)
{
    public string OutputInstructions => Output switch
    {
        HandoffOutput.Specification =>
            "Write the handoff as a specification describing requirements, constraints, acceptance criteria, and unresolved decisions.",
        HandoffOutput.ResearchInstructions =>
            "Write the handoff as research instructions describing the questions to answer, relevant context, likely sources, and the expected research deliverable.",
        HandoffOutput.ImplementationInstructions =>
            "Write concrete implementation instructions with a detailed work breakdown. Include relevant file paths, symbols, and line numbers where available.",
        HandoffOutput.Custom when !string.IsNullOrWhiteSpace(CustomInstructions) =>
            $"Write the handoff in this requested form:\n{CustomInstructions.Trim()}",
        HandoffOutput.Custom =>
            throw new InvalidOperationException("Custom handoffs require output instructions."),
        _ => throw new ArgumentOutOfRangeException(nameof(Output))
    };
}

public sealed record AssistantHandoff(
    string SourceSessionId,
    string SourceLabel,
    string ArtifactPath,
    string WorkingDirectory);

public sealed record AssistantSessionTarget(string SessionId);

public sealed record AssistantStoredSession(
    string SessionId,
    string Label,
    string WorkingDirectory);

public interface IAssistantSessions : IAsyncDisposable
{
    event EventHandler<SessionAttention>? AttentionRequested;
    event EventHandler<SessionContextUsage>? ContextUsageChanged;
    event EventHandler<AssistantHandoff>? HandoffCompleted;
    event EventHandler<string>? SessionFocused;
    event EventHandler<string>? SessionClosed;
    event EventHandler<string>? ConnectionFailed;
    Task OpenAsync(string workingDirectory, CancellationToken cancellationToken);
    Task<bool> FocusAsync(string sessionId, CancellationToken cancellationToken);
    Task<AssistantSessionTarget?> CaptureSessionTargetAsync(CancellationToken cancellationToken);
    Task<AssistantHandoff?> StartHandoffAsync(
        AssistantSessionTarget target,
        HandoffRequest request,
        CancellationToken cancellationToken);
    Task CloseHandoffSourceAsync(AssistantHandoff handoff, CancellationToken cancellationToken);
    Task OpenHandoffAsync(AssistantHandoff handoff, CancellationToken cancellationToken);
}

public interface IStoredAssistantSessions : IAssistantSessions
{
    event EventHandler<AssistantStoredSession>? StoredSessionAdded;
    event EventHandler<string>? StoredSessionRemoved;
    Task<AssistantStoredSession?> StoreSessionAsync(
        AssistantSessionTarget target,
        CancellationToken cancellationToken);
    Task OpenStoredSessionAsync(
        AssistantStoredSession storedSession,
        CancellationToken cancellationToken);
}

public sealed class AttentionQueue
{
    private readonly List<SessionAttention> pending = [];

    public int Count => pending.Count;
    public SessionAttention? Current => pending.FirstOrDefault();

    public void Enqueue(SessionAttention attention)
    {
        int existing = pending.FindIndex(item => item.SessionId == attention.SessionId);
        if (existing >= 0)
        {
            pending[existing] = attention;
        }
        else
        {
            pending.Add(attention);
        }
    }

    public bool Remove(string sessionId) => pending.RemoveAll(item => item.SessionId == sessionId) > 0;

    public bool Acknowledge(SessionAttention attention)
    {
        if (!ReferenceEquals(Current, attention))
        {
            return false;
        }
        pending.RemoveAt(0);
        return true;
    }
}