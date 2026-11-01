namespace Content.Server._Calamity.Persistence;

[RegisterComponent]
public sealed partial class CalamityPersistentMapComponent : Component
{
    [DataField(required: true)]
    public string Key = string.Empty;
}
