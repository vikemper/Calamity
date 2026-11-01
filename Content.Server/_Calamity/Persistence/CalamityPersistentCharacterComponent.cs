namespace Content.Server._Calamity.Persistence;

/// <summary>
/// Records the database profile identity associated with a persistent in-world mind.
/// The account association is the owning mind's MindComponent.UserId.
/// </summary>
[RegisterComponent]
public sealed partial class CalamityPersistentCharacterComponent : Component
{
    /// <summary>
    /// Legacy slot used to migrate snapshots created before profile IDs were recorded.
    /// </summary>
    [DataField(required: true)]
    public int CharacterSlot;

    /// <summary>
    /// Stable ID of the account's Profile row. Zero means this is a legacy or non-database profile.
    /// </summary>
    [DataField]
    public int CharacterProfileId;
}
