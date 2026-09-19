namespace CopilotBuddy.Composition;

internal sealed record SessionShortcutSettings(
    string Summon,
    string SkillMenu,
    string Inquire,
    string Handoff,
    string Gather,
    string Store)
{
    public static SessionShortcutSettings Default { get; } = new(
        "Alt+Enter",
        "Alt+Space",
        "Alt+Shift+G",
        "Alt+Shift+H",
        "Alt+Shift+T",
        "Alt+Shift+S");

    public IEnumerable<(string Name, string Shortcut)> All()
    {
        yield return ("Summon", Summon);
        yield return ("Buddy Menu", SkillMenu);
        yield return ("Inquire", Inquire);
        yield return ("Handoff", Handoff);
        yield return ("Gather", Gather);
        yield return ("Store", Store);
    }
}

internal readonly record struct ShortcutBinding(Keys Key, uint Modifiers)
{
    private const uint AltModifier = 0x1;
    private const uint ControlModifier = 0x2;
    private const uint ShiftModifier = 0x4;
    private const uint NoRepeatModifier = 0x4000;

    public static ShortcutBinding Parse(string shortcut)
    {
        Keys keys = (Keys)(new KeysConverter().ConvertFromInvariantString(shortcut)
            ?? throw new ArgumentException("Choose a modifier and a key."));
        Keys key = keys & Keys.KeyCode;
        uint modifiers = NoRepeatModifier;
        if ((keys & Keys.Alt) != 0) modifiers |= AltModifier;
        if ((keys & Keys.Control) != 0) modifiers |= ControlModifier;
        if ((keys & Keys.Shift) != 0) modifiers |= ShiftModifier;
        if (modifiers == NoRepeatModifier ||
            key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu)
        {
            throw new ArgumentException("Choose a key with Alt, Ctrl, or Shift.");
        }
        return new ShortcutBinding(key, modifiers);
    }

    public static string FromKeyData(Keys keyData)
    {
        string shortcut = new KeysConverter().ConvertToInvariantString(keyData)
            ?? throw new ArgumentException("Choose a modifier and a key.");
        _ = Parse(shortcut);
        return shortcut;
    }
}
