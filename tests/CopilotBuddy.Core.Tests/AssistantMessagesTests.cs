using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class AssistantMessagesTests
{
    [Fact]
    public void CatalogContainsFiftyMessagesForEachOfFiveTypes()
    {
        IReadOnlyList<string>[] pools = [AssistantMessages.Completion, AssistantMessages.Permission,
            AssistantMessages.Question, AssistantMessages.PlanReview, AssistantMessages.Error];
        Assert.All(pools, pool => Assert.Equal(50, pool.Count));
        string[] messages = pools.SelectMany(pool => pool).ToArray();
        Assert.Equal(250, messages.Distinct(StringComparer.Ordinal).Count());
        Assert.All(messages, message => Assert.InRange(message.Length, 1, 100));
    }

    [Fact]
    public void SelectionUsesTheMatchingPool()
    {
        const string questionPrefix = "Copilot has a question. ";
        for (int sample = 0; sample < 100; sample++)
        {
            Assert.Contains(AssistantMessages.PickCompletion(), AssistantMessages.Completion);
            Assert.Contains(AssistantMessages.PickPermission(), AssistantMessages.Permission);
            string question = AssistantMessages.PickQuestion();
            Assert.StartsWith(questionPrefix, question);
            Assert.Contains(question[questionPrefix.Length..], AssistantMessages.Question);
            Assert.Contains(AssistantMessages.PickPlanReview(), AssistantMessages.PlanReview);
            Assert.Contains(AssistantMessages.PickError(), AssistantMessages.Error);
        }
    }
}