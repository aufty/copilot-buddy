using CopilotBuddy.Core;

namespace CopilotBuddy.Core.Tests;

public sealed class AssistantLaunchCommandTests
{
    [Fact]
    public void ParsesExecutableAndPrefixArguments()
    {
        AssistantLaunchCommand command =
            AssistantLaunchCommand.Parse("custom-launcher copilot --custom-option");

        Assert.Equal("custom-launcher", command.Executable);
        Assert.Equal(["copilot", "--custom-option"], command.Arguments);
    }

    [Fact]
    public void RoundTripsQuotedWindowsCommandLines()
    {
        AssistantLaunchCommand command = new(
            @"C:\Program Files\Custom Launcher\custom-launcher.exe",
            ["copilot", "--label", "Buddy's \"special\" session", @"C:\trailing slash\"]);

        AssistantLaunchCommand restored = AssistantLaunchCommand.Parse(command.ToString());

        Assert.Equal(command.Executable, restored.Executable);
        Assert.Equal(command.Arguments, restored.Arguments);
    }

    [Fact]
    public void RejectsUnmatchedQuotes()
    {
        FormatException exception = Assert.Throws<FormatException>(
            () => AssistantLaunchCommand.Parse("custom-launcher \"copilot"));

        Assert.Contains("unmatched quote", exception.Message);
    }
}
