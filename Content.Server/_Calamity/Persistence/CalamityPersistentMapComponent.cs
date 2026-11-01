namespace Content.Server._Calamity.Persistence;

[RegisterComponent]
public sealed partial class CalamityPersistentMapComponent : Component
{
    [DataField(required: true)]
    public string Key = string.Empty;

    /// <summary>
    /// The game-map prototype used to create this map, when it has one.
    /// </summary>
    [DataField]
    public string? GameMapPrototype;
}
