using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class AttentionQueueTests
{
    [Fact]
    public void AcknowledgementCannotSkipOrDiscardANewerAlert()
    {
        AttentionQueue queue = new();
        SessionAttention first = new("first", "Permission needed");
        SessionAttention second = new("second", "Finished");
        queue.Enqueue(first);
        queue.Enqueue(second);
        Assert.False(queue.Acknowledge(second));
        queue.Enqueue(new("first", "Question needs an answer"));
        Assert.False(queue.Acknowledge(first));
        Assert.True(queue.Acknowledge(queue.Current!));
        Assert.Same(second, queue.Current);
    }

    [Fact]
    public void SessionsStayInArrivalOrderUntilAcknowledged()
    {
        AttentionQueue queue = new();
        queue.Enqueue(new("first", "Approval needed"));
        queue.Enqueue(new("second", "Finished"));
        Assert.Equal(2, queue.Count);
        Assert.Equal("first", queue.Current!.SessionId);
        Assert.True(queue.Remove("first"));
        Assert.Equal("second", queue.Current!.SessionId);
        Assert.Equal(1, queue.Count);
        Assert.True(queue.Remove("second"));
        Assert.Null(queue.Current);
    }

    [Fact]
    public void UpdatedSessionKeepsItsPlaceWithoutAddingAnotherOrb()
    {
        AttentionQueue queue = new();
        queue.Enqueue(new("first", "Approval needed"));
        queue.Enqueue(new("second", "Finished"));
        queue.Enqueue(new("first", "Finished"));
        Assert.Equal(2, queue.Count);
        Assert.Equal(new SessionAttention("first", "Finished"), queue.Current);
        queue.Remove("second");
        Assert.Equal("first", queue.Current!.SessionId);
        Assert.False(queue.Remove("missing"));
        Assert.Equal(1, queue.Count);
    }
}