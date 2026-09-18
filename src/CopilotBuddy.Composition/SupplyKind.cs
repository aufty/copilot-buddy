using CopilotBuddy.Core;

namespace CopilotBuddy.Composition;

internal enum SupplyKind
{
    Ball,
    Food,
    Water,
    Chair
}

internal static class SupplyKindExtensions
{
    public static BuddyNeed? Need(this SupplyKind kind) => kind switch
    {
        SupplyKind.Ball => BuddyNeed.Play,
        SupplyKind.Food => BuddyNeed.Food,
        SupplyKind.Water => BuddyNeed.Water,
        SupplyKind.Chair => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
