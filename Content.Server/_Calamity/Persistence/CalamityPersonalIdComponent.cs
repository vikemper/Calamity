namespace Content.Server._Calamity.Persistence;

[RegisterComponent]
public sealed partial class CalamityPersonalIdComponent : Component
{
    public const string PersonalIdSlotId = "calamity-personal-id";

    [DataField(required: true)]
    public EntityUid OwnerMind;

    [DataField]
    public string? StoredShipYaml;

    [DataField]
    public string? StoredShipName;
}
