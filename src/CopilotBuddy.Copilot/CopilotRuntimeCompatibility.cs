using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CopilotBuddy.Copilot;

internal static class CopilotRuntimeCompatibility
{
    private const string Version = "1.0.86-2";
    private const string Constructor = "this.server=new qM({port:t.port,host:t.host,stdio:!1,";
    private const string AuthenticatedConstructor = "this.server=new qM({connectionToken:(()=>{const token=process.env.COPILOT_CONNECTION_TOKEN;delete process.env.COPILOT_CONNECTION_TOKEN;return token;})(),port:t.port,host:t.host,stdio:!1,";
    private const string IdeAutoConnect = "c.ide?.autoConnect!==!1";
    private static readonly object PreparationLock = new();

    public static string Prepare(CancellationToken cancellationToken)
    {
        lock (PreparationLock)
        {
            return PrepareCore(cancellationToken);
        }
    }

    private static string PrepareCore(CancellationToken cancellationToken)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string source = ResolveSource(localData, architecture, cancellationToken);
        string destination = Path.Combine(
            localData,
            "CopilotBuddy",
            "copilot-runtime",
            $"{Version}-win32-{architecture}-ui-auth-no-ide-v4");
        string marker = Path.Combine(destination, ".copilot-buddy-ui-auth-no-ide");
        if (File.Exists(marker) &&
            File.Exists(Path.Combine(destination, "package.json")) &&
            File.Exists(Path.Combine(destination, "app.js")))
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

    private static string ResolveSource(
        string localData,
        string architecture,
        CancellationToken cancellationToken)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "CopilotRuntime");
        if (File.Exists(Path.Combine(bundled, "package.json")))
        {
            return bundled;
        }

        string archive = Path.Combine(bundled, "copilot.tgz");
        if (File.Exists(archive))
        {
            return ExtractBundledRuntime(archive, localData, architecture, cancellationToken);
        }

        string installed = Path.Combine(localData, "copilot", "pkg", $"win32-{architecture}", Version);
        if (File.Exists(Path.Combine(installed, "package.json")))
        {
            return installed;
        }

        throw new FileNotFoundException(
            $"Copilot Buddy could not find its bundled Copilot {Version} runtime. " +
            "Reinstall Copilot Buddy from the GitHub Release or rebuild it with runtime acquisition enabled.");
    }

    private static string ExtractBundledRuntime(
        string archive,
        string localData,
        string architecture,
        CancellationToken cancellationToken)
    {
        string cacheRoot = Path.Combine(localData, "CopilotBuddy", "copilot-runtime-source");
        string destination = Path.Combine(cacheRoot, $"{Version}-win32-{architecture}");
        if (File.Exists(Path.Combine(destination, "package.json")) &&
            File.Exists(Path.Combine(destination, "app.js")))
        {
            return destination;
        }

        Directory.CreateDirectory(cacheRoot);
        string staging = destination + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            using FileStream file = File.OpenRead(archive);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: false);
            cancellationToken.ThrowIfCancellationRequested();

            string extracted = Path.Combine(staging, "package");
            if (!File.Exists(Path.Combine(extracted, "package.json")))
            {
                throw new InvalidDataException("The bundled Copilot runtime archive has an unexpected layout.");
            }
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(extracted, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
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