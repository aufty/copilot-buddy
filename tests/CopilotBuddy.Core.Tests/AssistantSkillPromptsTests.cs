using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class AssistantSkillPromptsTests
{
    [Fact]
    public void GrillingIncludesRoundProtocolWithoutFrontmatter()
    {
        Assert.StartsWith("Interview the user relentlessly", AssistantSkillPrompts.Grilling);
        Assert.DoesNotContain("name: grilling", AssistantSkillPrompts.Grilling);
        Assert.Contains("Map this as a **design tree**", AssistantSkillPrompts.Grilling);
        Assert.Contains("Ask the whole frontier in one round", AssistantSkillPrompts.Grilling);
        Assert.Contains("❓ **Q1**", AssistantSkillPrompts.Grilling);
        Assert.Contains("Do not act on it until the user confirms", AssistantSkillPrompts.Grilling);
    }
}
