using Content.Shared._Calamity.Sectors;

namespace Content.Server._Calamity.Persistence;

[RegisterComponent]
public sealed partial class CalamitySectorComponent : Component
{
    [DataField]
    public int X;

    [DataField]
    public int Y;

    public CalamitySectorCoordinates Coordinates => new(X, Y);
}

[RegisterComponent]
public sealed partial class CalamitySafeSectorComponent : Component
{
}

[RegisterComponent]
public sealed partial class CalamitySafeSectorPacificationComponent : Component
{
    [DataField]
    public bool AddedPacifiedComponent;
}

[RegisterComponent]
public sealed partial class CalamitySectorNavigationComponent : Component
{
}
