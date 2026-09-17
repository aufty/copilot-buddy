using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using TaskbarBuddy.Core;

namespace TaskbarBuddy.Composition;

internal sealed class NamedPipeCommandServer(string pipeName, Func<PresentationRequest, Task<PresentationResponse>> handler) : IDisposable
{
    private NamedPipeServerStream? activePipe;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                activePipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await activePipe.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(activePipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
            finally
            {
                activePipe?.Dispose();
                activePipe = null;
            }
        }
    }

    public void Dispose() => activePipe?.Dispose();

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, leaveOpen: true);
        using StreamWriter writer = new(stream, leaveOpen: true) { AutoFlush = true };
        PresentationResponse response;
        try
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            PresentationRequest? request = line is null
                ? null
                : JsonSerializer.Deserialize<PresentationRequest>(line, PipeProtocol.JsonOptions);
            response = request is null
                ? new PresentationResponse(PipeProtocol.Version, false, "Request was empty.")
                : await handler(request);
        }
        catch (JsonException exception)
        {
            response = new PresentationResponse(PipeProtocol.Version, false, $"Invalid JSON: {exception.Message}");
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeProtocol.JsonOptions));
    }
}