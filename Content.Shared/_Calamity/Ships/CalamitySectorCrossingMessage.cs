using Robust.Shared.Serialization;
using Robust.Shared.GameObjects;
using Content.Shared._Mono.FireControl;

namespace Content.Shared._Calamity.Ships;

[Serializable, NetSerializable]
public enum CalamitySectorDirection : byte
{
    North,
    East,
    South,
    West,
}

[Serializable, NetSerializable]
public sealed class CalamitySectorCrossingMessage : BoundUserInterfaceMessage
{
    public CalamitySectorDirection Direction;

    public CalamitySectorCrossingMessage(CalamitySectorDirection direction)
    {
        Direction = direction;
    }
}

[ByRefEvent]
public struct FireControlConsoleStateRequestEvent
{
    public bool Connected;
    public FireControllableEntry[] Controllables;

    public FireControlConsoleStateRequestEvent()
    {
        Connected = false;
        Controllables = [];
    }
}
