namespace CopilotBuddy.Core;

public sealed record SessionContextUsage(string SessionId, string Title, double CurrentTokens, double TokenLimit);

public sealed class ContextPressure
{
    public const double TokenThreshold = 140_000;
    public const double FractionThreshold = 0.4;
    public const double HeavyWalkSpeedMultiplier = 0.5;

    private readonly Dictionary<string, SessionContextUsage> sessions = [];
    private string? presentedSessionId;

    public bool IsHeavy => sessions.Count > 0;
    public double WalkSpeedMultiplier => IsHeavy ? HeavyWalkSpeedMultiplier : 1;
    public SessionContextUsage? Presented => presentedSessionId is not null
        ? sessions.GetValueOrDefault(presentedSessionId) : null;
    public string? Message => Presented is { } session
        ? $"{session.Title} session is getting heavy on context. I recommend compacting or handing off to a new session"
        : null;

    public static bool IsContextHeavy(double currentTokens, double tokenLimit) =>
        double.IsFinite(currentTokens) && currentTokens >= 0 &&
        (currentTokens >= TokenThreshold ||
            (double.IsFinite(tokenLimit) && tokenLimit > 0 && currentTokens / tokenLimit > FractionThreshold));

    public void Update(SessionContextUsage usage)
    {
        if (IsContextHeavy(usage.CurrentTokens, usage.TokenLimit))
        {
            sessions[usage.SessionId] = usage;
        }
        else
        {
            Remove(usage.SessionId);
        }
    }

    public void Remove(string sessionId)
    {
        sessions.Remove(sessionId);
        if (presentedSessionId == sessionId)
        {
            presentedSessionId = null;
        }
    }

    public bool Reveal()
    {
        presentedSessionId ??= sessions.Values.FirstOrDefault()?.SessionId;
        return Presented is not null;
    }

    public void Acknowledge(string sessionId)
    {
        if (presentedSessionId == sessionId)
        {
            presentedSessionId = null;
        }
    }
}