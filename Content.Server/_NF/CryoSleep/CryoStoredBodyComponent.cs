using Robust.Shared.Network;

namespace Content.Server._NF.CryoSleep;

/// <summary>
/// Persists the account link and expiration state for a body stored in cryosleep.
/// </summary>
[RegisterComponent]
public sealed partial class CryoStoredBodyComponent : Component
{
    [DataField]
    public NetUserId? UserId;

    [DataField(required: true)]
    public EntityUid Cryopod = EntityUid.Invalid;

    [DataField]
    public bool HadPreviousTimedDespawn;

    [DataField]
    public float PreviousTimedDespawnLifetime;

    [DataField]
    public float TimedDespawnLifetimeAtStorage;
}
