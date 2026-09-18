#:package GitHub.Copilot.SDK@1.0.14
#:property CopilotSkipCliDownload=true

using GitHub.Copilot;

if (args.Length != 1 || !int.TryParse(args[0], out int port) || port is < 1 or > 65535)
{
    throw new ArgumentException("Provide the attached CLI's loopback port.");
}

CopilotClient client = new(new CopilotClientOptions
{
    Connection = RuntimeConnection.ForUri($"127.0.0.1:{port}", "copilot-buddy-intentionally-invalid-token")
});
using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(3));
try
{
    await client.StartAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(3));
    throw new InvalidOperationException("SECURITY CHECK FAILED: the CLI accepted an incorrect connection token.");
}
catch (IOException exception) when (exception.InnerException?.Message == "AUTHENTICATION_FAILED")
{
    Console.WriteLine("PASS: the attached CLI rejected an incorrect connection token.");
}
finally
{
    await client.ForceStopAsync().WaitAsync(TimeSpan.FromSeconds(1));
}