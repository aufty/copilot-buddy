using System.Text.Json;
using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void AssistantBrokerMessagesRoundTripHandoffRequests()
    {
        BrokerStartHandoffRequest payload = new(
            new AssistantSessionTarget("terminal/session", new AssistantWindowBounds(100, 200, 1200, 800)),
            new HandoffRequest(HandoffOutput.ImplementationInstructions));
        AssistantBrokerMessage message = new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Request,
            Guid.NewGuid(),
            AssistantBrokerProtocol.StartHandoff,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        string json = JsonSerializer.Serialize(message, PipeProtocol.JsonOptions);
        AssistantBrokerMessage restored = JsonSerializer.Deserialize<AssistantBrokerMessage>(
            json, PipeProtocol.JsonOptions)!;
        BrokerStartHandoffRequest restoredPayload = restored.Payload!.Value.Deserialize<BrokerStartHandoffRequest>(
            PipeProtocol.JsonOptions)!;

        Assert.Equal(message.Id, restored.Id);
        Assert.Equal("terminal/session", restoredPayload.Target.SessionId);
        Assert.Equal(new AssistantWindowBounds(100, 200, 1200, 800), restoredPayload.Target.WindowBounds);
        Assert.Equal(HandoffOutput.ImplementationInstructions, restoredPayload.Request.Output);
    }

    [Fact]
    public void AssistantBrokerMessagesRoundTripPromptTargets()
    {
        BrokerInjectPromptRequest payload = new(
            new AssistantSessionTarget("terminal/session", new AssistantWindowBounds(100, 200, 1200, 800)),
            "Ask every unblocked question.");
        AssistantBrokerMessage message = new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Request,
            Guid.NewGuid(),
            AssistantBrokerProtocol.InjectPrompt,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        string json = JsonSerializer.Serialize(message, PipeProtocol.JsonOptions);
        AssistantBrokerMessage restored = JsonSerializer.Deserialize<AssistantBrokerMessage>(
            json, PipeProtocol.JsonOptions)!;
        BrokerInjectPromptRequest restoredPayload =
            restored.Payload!.Value.Deserialize<BrokerInjectPromptRequest>(PipeProtocol.JsonOptions)!;

        Assert.Equal(payload, restoredPayload);
    }

    [Fact]
    public void AssistantBrokerMessagesRoundTripLaunchCommands()
    {
        AssistantLaunchCommand payload = new("custom-launcher", ["copilot", "--custom-option"]);
        AssistantBrokerMessage message = new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Request,
            Guid.NewGuid(),
            AssistantBrokerProtocol.ConfigureLaunch,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        string json = JsonSerializer.Serialize(message, PipeProtocol.JsonOptions);
        AssistantBrokerMessage restored = JsonSerializer.Deserialize<AssistantBrokerMessage>(
            json, PipeProtocol.JsonOptions)!;
        AssistantLaunchCommand restoredPayload =
            restored.Payload!.Value.Deserialize<AssistantLaunchCommand>(PipeProtocol.JsonOptions)!;

        Assert.Equal(payload.Executable, restoredPayload.Executable);
        Assert.Equal(payload.Arguments, restoredPayload.Arguments);
    }

    [Fact]
    public void AssistantBrokerMessagesRoundTripGatherWorkAreas()
    {
        AssistantWindowBounds payload = new(0, 0, 1920, 1040);
        AssistantBrokerMessage message = new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Request,
            Guid.NewGuid(),
            AssistantBrokerProtocol.Gather,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        string json = JsonSerializer.Serialize(message, PipeProtocol.JsonOptions);
        AssistantBrokerMessage restored = JsonSerializer.Deserialize<AssistantBrokerMessage>(
            json, PipeProtocol.JsonOptions)!;
        AssistantWindowBounds restoredPayload =
            restored.Payload!.Value.Deserialize<AssistantWindowBounds>(PipeProtocol.JsonOptions)!;

        Assert.Equal(payload, restoredPayload);
    }

    [Fact]
    public void AssistantBrokerMessagesRoundTripGatherResults()
    {
        AssistantGatherResult payload = new(
            [new AssistantWindowBounds(0, 0, 960, 1040), new AssistantWindowBounds(960, 0, 960, 1040)]);
        AssistantBrokerMessage message = new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Response,
            Guid.NewGuid(),
            AssistantBrokerProtocol.Gather,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        string json = JsonSerializer.Serialize(message, PipeProtocol.JsonOptions);
        AssistantBrokerMessage restored = JsonSerializer.Deserialize<AssistantBrokerMessage>(
            json, PipeProtocol.JsonOptions)!;
        AssistantGatherResult restoredPayload =
            restored.Payload!.Value.Deserialize<AssistantGatherResult>(PipeProtocol.JsonOptions)!;

        Assert.Equal(payload.Windows, restoredPayload.Windows);
    }

    [Fact]
    public void RequestRoundTripsMultilineText()
    {
        PresentationRequest request = new(PipeProtocol.Version, PipeProtocol.Show, "First line\nSecond line");

        string json = JsonSerializer.Serialize(request);
        PresentationRequest? result = JsonSerializer.Deserialize<PresentationRequest>(json);

        Assert.Equal(request, result);
    }

    [Fact]
    public void AssistantBrokerMessagesRoundTripStoredSessions()
    {
        AssistantStoredSession payload = new(
            "0cb916db-26aa-40f2-86b5-1ba81b225fd2",
            "Persistent Store skill",
            @"C:\source\copilot-buddy");
        AssistantBrokerMessage message = new(
            AssistantBrokerProtocol.Version,
            AssistantBrokerProtocol.Event,
            Name: AssistantBrokerProtocol.StoredSessionAdded,
            Payload: JsonSerializer.SerializeToElement(payload, PipeProtocol.JsonOptions));

        string json = JsonSerializer.Serialize(message, PipeProtocol.JsonOptions);
        AssistantBrokerMessage restored = JsonSerializer.Deserialize<AssistantBrokerMessage>(
            json, PipeProtocol.JsonOptions)!;
        AssistantStoredSession restoredPayload =
            restored.Payload!.Value.Deserialize<AssistantStoredSession>(PipeProtocol.JsonOptions)!;

        Assert.Equal(payload, restoredPayload);
        Assert.Equal(AssistantBrokerProtocol.Version, restored.Version);
    }
}