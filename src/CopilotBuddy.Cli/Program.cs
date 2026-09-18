using System.IO.Pipes;
using System.Text.Json;
using CopilotBuddy.Core;

return await BuddyCli.RunAsync(args);

internal static class BuddyCli
{
	private const string DefaultPipeName = "CopilotBuddy.Presentation.v1";

	public static async Task<int> RunAsync(string[] args)
	{
		if (!TryParse(args, out PresentationRequest? request, out string? error))
		{
			Console.Error.WriteLine(error);
			PrintUsage();
			return 2;
		}

		try
		{
			using NamedPipeClientStream pipe = new(".", DefaultPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
			await pipe.ConnectAsync(timeout.Token);
			using StreamReader reader = new(pipe, leaveOpen: true);
			using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
			await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeProtocol.JsonOptions));
			string? line = await reader.ReadLineAsync(timeout.Token);
			PresentationResponse? response = line is null
				? null
				: JsonSerializer.Deserialize<PresentationResponse>(line, PipeProtocol.JsonOptions);
			if (response is null)
			{
				Console.Error.WriteLine("Copilot Buddy returned no response.");
				return 1;
			}
			if (!response.Success)
			{
				Console.Error.WriteLine(response.Error);
				return 1;
			}
			return 0;
		}
		catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException)
		{
			Console.Error.WriteLine("Copilot Buddy is not running or did not respond.");
			return 1;
		}
	}

	private static bool TryParse(string[] args, out PresentationRequest? request, out string? error)
	{
		request = null;
		error = null;
		if (args is ["show", var text] && !string.IsNullOrWhiteSpace(text))
		{
			request = new(PipeProtocol.Version, PipeProtocol.Show, text);
			return true;
		}
		if (args is ["dismiss"])
		{
			request = new(PipeProtocol.Version, PipeProtocol.Dismiss);
			return true;
		}
		if (args is ["visible", var value] && (value == "on" || value == "off"))
		{
			request = new(PipeProtocol.Version, PipeProtocol.Visible, Visible: value == "on");
			return true;
		}
		error = "Invalid command.";
		return false;
	}

	private static void PrintUsage()
	{
		Console.Error.WriteLine("Usage:");
		Console.Error.WriteLine("  copilot-buddyctl show \"Build needs your approval\"");
		Console.Error.WriteLine("  copilot-buddyctl dismiss");
		Console.Error.WriteLine("  copilot-buddyctl visible on|off");
	}
}
