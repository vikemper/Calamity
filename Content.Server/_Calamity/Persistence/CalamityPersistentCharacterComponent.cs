namespace Content.Server._Calamity.Persistence;

/// <summary>
/// Records which account character slot owns a persistent in-world mind.
/// </summary>
[RegisterComponent]
public sealed partial class CalamityPersistentCharacterComponent : Component
{
    [DataField(required: true)]
    public int CharacterSlot;
}
