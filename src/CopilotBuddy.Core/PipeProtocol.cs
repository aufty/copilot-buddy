using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBuddy.Core;

public static class PipeProtocol
{
    public const int Version = 1;
    public const int MaximumMessageLength = 16_384;
    public const string Show = "show";
    public const string Dismiss = "dismiss";
    public const string Visible = "visible";
    public const string Quip = "quip";
    public const string DemoHeavyContext = "demo-heavy-context";
    public const string DemoNeed = "demo-need";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record PresentationRequest(
    int Version,
    string Command,
    string? Text = null,
    bool? Visible = null,
    bool? Enabled = null,
    BuddyNeed? Need = null);
public sealed record PresentationResponse(int Version, bool Success, string? Error = null);