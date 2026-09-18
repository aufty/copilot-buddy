namespace CopilotBuddy.Composition;

internal sealed record BuddySprite(string Name, string Path);

internal static class BuddySpriteCatalog
{
    public const string DefaultName = "sprout";
    public const int SheetWidth = 98;
    public const int SheetHeight = 20;
    public const int FrameWidth = 14;
    public const int FrameHeight = 20;

    public static string DirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Sprites", "Buddies");

    public static IReadOnlyList<BuddySprite> GetAvailable()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        List<BuddySprite> sprites = [];
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.png")
                     .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using Bitmap bitmap = new(path);
                if (bitmap.Width == SheetWidth && bitmap.Height == SheetHeight)
                {
                    sprites.Add(new BuddySprite(Path.GetFileNameWithoutExtension(path), path));
                }
            }
            catch (ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine($"Ignoring unreadable buddy sprite '{path}'.");
            }
        }
        return sprites;
    }

    public static BuddySprite Resolve(string? selectedName)
    {
        IReadOnlyList<BuddySprite> available = GetAvailable();
        BuddySprite? selected = available.FirstOrDefault(sprite =>
            string.Equals(sprite.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        selected ??= available.FirstOrDefault(sprite =>
            string.Equals(sprite.Name, DefaultName, StringComparison.OrdinalIgnoreCase));
        selected ??= available.FirstOrDefault();
        return selected ?? throw new InvalidOperationException(
            $"No {SheetWidth}x{SheetHeight} buddy sprite sheets were found in '{DirectoryPath}'.");
    }
}
