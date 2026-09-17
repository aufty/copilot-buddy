using System.Text.Json;
using TaskbarBuddy.Core;

namespace TaskbarBuddy.Core.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void AssistantBrokerMessagesRoundTripHandoffRequests()
    {
        BrokerStartHandoffRequest payload = new(
            new AssistantSessionTarget("terminal/session"),
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
        Assert.Equal(HandoffOutput.ImplementationInstructions, restoredPayload.Request.Output);
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
            @"C:\source\hackathon2026");
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
        Assert.Equal(2, restored.Version);
    }
}