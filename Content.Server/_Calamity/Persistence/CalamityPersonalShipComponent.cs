namespace Content.Server._Calamity.Persistence;

[RegisterComponent]
public sealed partial class CalamityPersonalShipComponent : Component
{
    [DataField(required: true)]
    public EntityUid OwnerId;

    [DataField(required: true)]
    public EntityUid NavigationConsole;
}

[RegisterComponent]
public sealed partial class CalamityShipNavigationConsoleComponent : Component
{
    [DataField]
    public EntityUid? RootGrid;

    [DataField]
    public bool Disabled = true;

    [DataField]
    public bool Primary;
}

[RegisterComponent]
public sealed partial class CalamityPersonalShipTerminalComponent : Component
{
}
