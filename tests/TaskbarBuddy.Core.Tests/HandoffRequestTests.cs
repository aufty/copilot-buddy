using TaskbarBuddy.Core;

namespace TaskbarBuddy.Core.Tests;

public sealed class HandoffRequestTests
{
    [Theory]
    [InlineData(HandoffOutput.Specification, "specification")]
    [InlineData(HandoffOutput.ResearchInstructions, "research instructions")]
    [InlineData(HandoffOutput.ImplementationInstructions, "line numbers")]
    public void CannedOutputsProduceSpecificInstructions(HandoffOutput output, string expected)
    {
        Assert.Contains(expected, new HandoffRequest(output).OutputInstructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomOutputRequiresFreeformInstructions()
    {
        Assert.Throws<InvalidOperationException>(() => new HandoffRequest(HandoffOutput.Custom).OutputInstructions);
        Assert.Contains("release checklist",
            new HandoffRequest(HandoffOutput.Custom, "A release checklist").OutputInstructions,
            StringComparison.OrdinalIgnoreCase);
    }
}
