namespace CopilotBuddy.Core;

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

public sealed record AssistantSessionTarget(
    string SessionId,
    AssistantWindowBounds? WindowBounds = null);

public sealed record AssistantWindowBounds(int Left, int Top, int Width, int Height);

public sealed record AssistantGatherResult(AssistantWindowBounds[] Windows);

public sealed record AssistantPromptInjection(
    string SessionId,
    AssistantWindowBounds? WindowBounds);

public static class AssistantSkillPrompts
{
    public const string Grilling = """
        Interview the user relentlessly until you reach a shared understanding. Map this as a **design tree**: every decision branches into the decisions that hang off it.

        Work the tree in **rounds**. The **frontier** is every decision whose prerequisites are already settled: the questions you can ask _now_ without guessing at answers you haven't heard yet. Ask the whole frontier in one round: number each question and give your recommended answer. Then wait for the user's answers before the next round.

        Format a round like so:

        ```
        ❓ **Q1** - **<question title>**: <question body, might be multiple paragraphs, including multiple choices>

        ➡️ <your recommended answer>

        ---

        ❓ **Q2** - **<question title>**: <question body, might be multiple paragraphs, including multiple choices>

        ➡️ <your recommended answer>
        ```

        Each round the user answers reshapes the tree: settled decisions push the frontier outward and unblock questions that depended on them. Recompute the frontier and ask the next round. A question whose answer depends on another question still open in this round belongs to a _later_ round, not this one.

        Finding _facts_ is your job, never the user's. When a frontier question needs a fact from the environment (filesystem, tools, etc.), dispatch a sub-agent to find it; don't ask the user for anything you could look up yourself. Don't block on it: a running exploration is an unsettled prerequisite, so only the questions downstream of it wait for the sub-agent to report; ask the rest of the frontier now. The _decisions_ are the user's: put each to them and wait.

        The session is done when the frontier is empty: every branch of the design tree visited, nothing left silently assumed. Do not act on it until the user confirms you have reached a shared understanding.
        """;
}

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
    Task<AssistantGatherResult> GatherAsync(
        AssistantWindowBounds workArea,
        CancellationToken cancellationToken);
    Task<AssistantSessionTarget?> CaptureSessionTargetAsync(CancellationToken cancellationToken);
    Task<AssistantPromptInjection?> InjectPromptAsync(
        AssistantSessionTarget target,
        string prompt,
        CancellationToken cancellationToken);
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