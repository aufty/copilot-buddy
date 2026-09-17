using System.Runtime.InteropServices;
using System.Text.Json;

namespace TaskbarBuddy.Copilot;

internal static class CopilotRuntimeCompatibility
{
    private const string Version = "1.0.86-2";
    private const string Constructor = "this.server=new qM({port:t.port,host:t.host,stdio:!1,";
    private const string AuthenticatedConstructor = "this.server=new qM({connectionToken:(()=>{const token=process.env.COPILOT_CONNECTION_TOKEN;delete process.env.COPILOT_CONNECTION_TOKEN;return token;})(),port:t.port,host:t.host,stdio:!1,";
    private const string IdeAutoConnect = "c.ide?.autoConnect!==!1";

    public static string Prepare(CancellationToken cancellationToken)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        string source = Path.Combine(localData, "copilot", "pkg", $"win32-{architecture}", Version);
        string destination = Path.Combine(
            localData,
            "TaskbarBuddy",
            "copilot-runtime",
            Version + "-ui-auth-no-ide-v2");
        string marker = Path.Combine(destination, ".buddy-ui-auth-no-ide");
        if (File.Exists(marker))
        {
            return destination;
        }

        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "package.json")));
        if (metadata.RootElement.GetProperty("version").GetString() != Version)
        {
            throw new InvalidOperationException("Unexpected Copilot runtime version; refusing to apply the UI-server authentication fix.");
        }
        string app = File.ReadAllText(Path.Combine(source, "app.js"));
        int constructorIndex = app.IndexOf(Constructor, StringComparison.Ordinal);
        if (constructorIndex < 0 || constructorIndex != app.LastIndexOf(Constructor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Copilot's embedded-server constructor changed; refusing to apply an unverified authentication fix.");
        }
        if (CountOccurrences(app, IdeAutoConnect) != 3)
        {
            throw new InvalidOperationException("Copilot's IDE auto-connect gates changed; refusing to apply an unverified isolation fix.");
        }
        string patchedApp = app
            .Replace(Constructor, AuthenticatedConstructor, StringComparison.Ordinal)
            .Replace(IdeAutoConnect, "!1", StringComparison.Ordinal);

        string staging = destination + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            File.WriteAllText(Path.Combine(staging, "app.js"), patchedApp);
            File.WriteAllText(Path.Combine(staging, Path.GetFileName(marker)), Version);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return destination;
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int start = 0;
        while ((start = value.IndexOf(search, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += search.Length;
        }
        return count;
    }
}