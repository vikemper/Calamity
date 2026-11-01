using Robust.Shared.Network;

namespace Content.Server._NF.GameRule.Components;

/// <summary>
/// Stores the player's round bank summary on their character body so a world restore can rebuild it.
/// </summary>
[RegisterComponent, Access(typeof(NFAdventureRuleSystem))]
public sealed partial class NFAdventurePlayerRoundBankComponent : Component
{
    [DataField]
    public int StartBalance;

    [DataField]
    public int EndBalance = -1;

    [DataField]
    public string Name = string.Empty;

    [DataField]
    public NetUserId UserId;
}
