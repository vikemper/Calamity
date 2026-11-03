using Robust.Shared.Serialization;

namespace Content.Shared._Calamity.Ships;

[Serializable, NetSerializable]
public enum CalamityPersonalShipConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum CalamityPersonalShipConsoleAction : byte
{
    Refresh,
    Link,
    Store,
    Retrieve,
    Unlink,
}

[Serializable, NetSerializable]
public sealed class CalamityPersonalShipConsoleActionMessage : BoundUserInterfaceMessage
{
    public CalamityPersonalShipConsoleAction Action;
    public NetEntity? Ship;

    public CalamityPersonalShipConsoleActionMessage(CalamityPersonalShipConsoleAction action, NetEntity? ship = null)
    {
        Action = action;
        Ship = ship;
    }
}

[Serializable, NetSerializable]
public sealed class CalamityPersonalShipConsoleState : BoundUserInterfaceState
{
    public string IdName;
    public string CharacterName;
    public string? LinkedShipName;
    public string? StoredShipName;
    public bool CanStore;
    public string? StoreUnavailableReason;
    public bool CanRetrieve;
    public bool CanUnlink;
    public CalamityPersonalShipCandidate[] DockedShips;

    public CalamityPersonalShipConsoleState(
        string idName,
        string characterName,
        string? linkedShipName,
        string? storedShipName,
        bool canStore,
        string? storeUnavailableReason,
        bool canRetrieve,
        bool canUnlink,
        CalamityPersonalShipCandidate[] dockedShips)
    {
        IdName = idName;
        CharacterName = characterName;
        LinkedShipName = linkedShipName;
        StoredShipName = storedShipName;
        CanStore = canStore;
        StoreUnavailableReason = storeUnavailableReason;
        CanRetrieve = canRetrieve;
        CanUnlink = canUnlink;
        DockedShips = dockedShips;
    }
}

[Serializable, NetSerializable]
public sealed class CalamityPersonalShipCandidate
{
    public NetEntity Ship;
    public string Name;
    public bool CanLink;

    public CalamityPersonalShipCandidate(NetEntity ship, string name, bool canLink)
    {
        Ship = ship;
        Name = name;
        CanLink = canLink;
    }
}
