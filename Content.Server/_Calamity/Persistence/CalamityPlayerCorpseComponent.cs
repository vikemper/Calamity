namespace Content.Server._Calamity.Persistence;

/// <summary>
/// Marks a mob body that has been controlled by a player account.
/// </summary>
[RegisterComponent]
public sealed partial class CalamityPlayerCharacterBodyComponent : Component
{
}

/// <summary>
/// Tracks the TimedDespawn state temporarily replaced by the player corpse lifetime.
/// </summary>
[RegisterComponent]
public sealed partial class CalamityPlayerCorpseDespawnComponent : Component
{
    [DataField]
    public bool HadPreviousTimedDespawn;

    [DataField]
    public float PreviousTimedDespawnLifetime;
}
