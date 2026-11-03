using System.Linq;
using Content.Server.Hands.Systems;
using Content.Server.Preferences.Managers;
using Content.Shared._Calamity.CCVar;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.PDA;
using Content.Shared.Preferences;
using Content.Shared.Standing;
using Content.Shared.Throwing;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._Calamity.Persistence;

public sealed partial class CalamityPersonalIdSystem : EntitySystem
{
    private const string PersonalIdContainerName = "calamity-personal-id";
    private const string IdSlotName = "id";
    private const string DefaultIdPrototype = "PassengerIDCard";

    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedIdCardSystem _idCards = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedMindSystem _minds = default!;

    private readonly HashSet<EntityUid> _internalTransfers = new();
    private readonly Dictionary<EntityUid, BaseContainer> _pendingRemovals = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<CalamityPersonalIdComponent, ContainerGettingInsertedAttemptEvent>(OnGettingInserted);
        SubscribeLocalEvent<CalamityPersonalIdComponent, ContainerGettingRemovedAttemptEvent>(OnGettingRemoved);
        SubscribeLocalEvent<CalamityPersonalIdComponent, FellDownThrowAttemptEvent>(OnFellDownThrowAttempt);
        SubscribeLocalEvent<IsUnequippingAttemptEvent>(OnIsUnequippingAttempt);
        SubscribeLocalEvent<BeforeThrowEvent>(OnBeforeThrow);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _pendingRemovals.Clear();
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !_minds.TryGetMind(ev.Player.UserId, out var mindId, out _) ||
            mindId == null)
        {
            return;
        }

        EnsureCharacterIdentity(mindId.Value, ev.Player.UserId);

        EnsurePersonalId(mindId.Value, ev.Mob, ev.Profile.Name);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !TryComp(ev.Entity, out MindContainerComponent? mindContainer) ||
            mindContainer.Mind is not { } mindId ||
            !TryComp(mindId, out MindComponent? mind) ||
            mind.UserId is not { } userId)
        {
            return;
        }

        EnsureCharacterIdentity(mindId, userId);
        EnsurePersonalId(mindId, ev.Entity, mind.CharacterName);
    }

    private void EnsureCharacterIdentity(EntityUid mindId, NetUserId userId)
    {
        if (HasComp<CalamityPersistentCharacterComponent>(mindId))
            return;

        if (!_preferences.TryGetCachedPreferences(userId, out var preferences) || preferences == null)
            return;

        var identity = AddComp<CalamityPersistentCharacterComponent>(mindId);
        identity.CharacterSlot = preferences.SelectedCharacterIndex;
        identity.CharacterProfileId = _preferences.TryGetCachedCharacterProfileId(
            userId,
            preferences.SelectedCharacterIndex,
            out var profileId)
            ? profileId
            : 0;
    }

    private void EnsurePersonalId(EntityUid mindId, EntityUid body, string? characterName)
    {
        var card = FindPersonalId(mindId);
        if (card == null && _inventory.TryGetSlotEntity(body, IdSlotName, out var initialId) &&
            initialId is { } initial)
        {
            if (TryComp(initial, out PdaComponent? pda) &&
                pda.ContainedId is { } pdaId &&
                HasComp<IdCardComponent>(pdaId))
            {
                card = pdaId;
            }
            else if (HasComp<IdCardComponent>(initial))
            {
                card = initial;
            }
        }

        if (card is { } existingCard)
        {
            EnsureComp<CalamityPersonalIdComponent>(existingCard).OwnerMind = mindId;
        }

        if (card == null)
        {
            card = Spawn(DefaultIdPrototype, Transform(body).Coordinates);
            EnsureComp<CalamityPersonalIdComponent>(card.Value).OwnerMind = mindId;

        }

        if (characterName != null && TryComp(card.Value, out IdCardComponent? idCard))
            _idCards.TryChangeFullName(card.Value, characterName, idCard);

        var vault = _containers.EnsureContainer<ContainerSlot>(mindId, PersonalIdContainerName);
        if (vault.ContainedEntity is { } replaced && replaced != card)
        {
            Log.Error($"Mind {ToPrettyString(mindId)} already stores a different personal ID {ToPrettyString(replaced)}.");
            return;
        }

        if (vault.ContainedEntity == card)
        {
            MoveIdToBody(card.Value, body, vault);
            return;
        }

        MoveIdToBody(card.Value, body, vault);
    }

    private EntityUid? FindPersonalId(EntityUid mindId)
    {
        var query = EntityQueryEnumerator<CalamityPersonalIdComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (component.OwnerMind == mindId)
                return uid;
        }

        return null;
    }

    private void MoveIdToBody(EntityUid card, EntityUid body, ContainerSlot vault)
    {
        if (_inventory.TryGetSlotEntity(body, IdSlotName, out var currentId) &&
            currentId == card)
        {
            return;
        }

        if (_inventory.TryGetSlotEntity(body, IdSlotName, out var worn) &&
            worn is { } wornEntity &&
            TryComp(wornEntity, out PdaComponent? pda) &&
            pda.IdSlot.ContainerSlot is { } pdaIdSlot)
        {
            if (pdaIdSlot.ContainedEntity == card)
                return;

            if (pdaIdSlot.ContainedEntity is { } oldId)
            {
                if (HasComp<CalamityPersonalIdComponent>(oldId))
                {
                    Log.Error($"PDA {ToPrettyString(wornEntity)} already contains another personal ID; refusing to replace it.");
                    return;
                }

                if (!_containers.Remove(oldId, pdaIdSlot, force: true))
                {
                    Log.Error($"Failed to replace the default ID in PDA {ToPrettyString(wornEntity)}.");
                    return;
                }

                QueueDel(oldId);
            }

            if (MoveCardToContainer(card, pdaIdSlot))
                return;
        }

        if (_inventory.TryGetSlotEntity(body, IdSlotName, out var occupiedId) &&
            occupiedId is { } occupied &&
            occupied != card)
        {
            if (HasComp<CalamityPersonalIdComponent>(occupied))
            {
                Log.Error($"Cannot equip personal ID {ToPrettyString(card)}: body {ToPrettyString(body)} already has another personal ID.");
                return;
            }

            if (TryComp(occupied, out PdaComponent? occupiedPda) &&
                occupiedPda.ContainedId is { } occupiedPdaId &&
                HasComp<CalamityPersonalIdComponent>(occupiedPdaId))
            {
                Log.Error($"Cannot equip personal ID {ToPrettyString(card)}: body {ToPrettyString(body)} already carries another personal ID in its PDA.");
                return;
            }

            if (HasComp<IdCardComponent>(occupied))
            {
                if (_containers.TryGetContainingContainer(body, occupied, out var occupiedContainer))
                    _containers.Remove(occupied, occupiedContainer, force: true);
                QueueDel(occupied);
            }
        }

        if (vault.ContainedEntity != card && vault.ContainedEntity != null)
            return;

        if (_inventory.HasSlot(body, IdSlotName) &&
            _inventory.TryEquip(body, card, IdSlotName, silent: true, force: true))
        {
            return;
        }

        if (vault.ContainedEntity != card && !_containers.Insert(card, vault))
            Log.Error($"Could not store personal ID {ToPrettyString(card)} for body {ToPrettyString(body)}.");
    }

    private bool MoveCardToContainer(EntityUid card, BaseContainer destination)
    {
        _internalTransfers.Add(card);
        try
        {
            var parent = Transform(card).ParentUid;
            if (_containers.TryGetContainingContainer(parent, card, out var currentContainer) &&
                currentContainer != destination &&
                !_containers.Remove(card, currentContainer, force: true))
            {
                Log.Error($"Failed to remove personal ID {ToPrettyString(card)} from its current container.");
                return false;
            }

            if (!_containers.Insert(card, destination))
            {
                Log.Error($"Failed to move personal ID {ToPrettyString(card)} into its inventory slot.");
                return false;
            }

            return true;
        }
        finally
        {
            _internalTransfers.Remove(card);
        }
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (ev.NewMobState != MobState.Dead ||
            !TryComp(ev.Target, out MindContainerComponent? mindContainer) ||
            mindContainer.Mind is not { } mindId ||
            !TryComp(mindId, out MindComponent? _))
        {
            return;
        }

        var card = FindPersonalId(mindId);
        if (card == null)
            return;

        var vault = _containers.EnsureContainer<ContainerSlot>(mindId, PersonalIdContainerName);
        if (vault.ContainedEntity != null && vault.ContainedEntity != card)
        {
            Log.Error($"Mind {ToPrettyString(mindId)} already has a personal ID stored; refusing to replace it.");
            return;
        }

        _internalTransfers.Add(card.Value);
        try
        {
            var parent = Transform(card.Value).ParentUid;
            if (_containers.TryGetContainingContainer(parent, card.Value, out var currentContainer) &&
                currentContainer != vault &&
                !_containers.Remove(card.Value, currentContainer, force: true))
            {
                Log.Error($"Failed to remove personal ID {ToPrettyString(card.Value)} from dead body {ToPrettyString(ev.Target)}.");
                return;
            }

            if (vault.ContainedEntity != card && !_containers.Insert(card.Value, vault))
                Log.Error($"Failed to store personal ID {ToPrettyString(card.Value)} after its owner died.");
        }
        finally
        {
            _internalTransfers.Remove(card.Value);
        }
    }

    private void OnGettingInserted(
        EntityUid uid,
        CalamityPersonalIdComponent component,
        ref ContainerGettingInsertedAttemptEvent args)
    {
        if (args.Container.Owner == component.OwnerMind &&
            args.Container.ID == PersonalIdContainerName)
        {
            return;
        }

        if (IsInCurrentBodyInventory(component.OwnerMind, args.Container.Owner) ||
            IsPersonalShipTerminalSlot(args.Container))
        {
            AuthorizeCurrentContainerRemoval(uid);
            return;
        }

        args.Cancel();
    }

    private void OnGettingRemoved(
        EntityUid uid,
        CalamityPersonalIdComponent component,
        ref ContainerGettingRemovedAttemptEvent args)
    {
        if (_internalTransfers.Remove(uid))
            return;

        if (_pendingRemovals.Remove(uid, out var source) && source == args.Container)
            return;

        if (IsPersonalShipTerminalSlot(args.Container))
            return;

        if (!IsHandContainer(args.Container) &&
            IsInCurrentBodyInventory(component.OwnerMind, args.Container.Owner))
        {
            return;
        }

        args.Cancel();
    }

    private void AuthorizeCurrentContainerRemoval(EntityUid card)
    {
        var parent = Transform(card).ParentUid;
        if (_containers.TryGetContainingContainer(parent, card, out var source))
            _pendingRemovals[card] = source;
    }

    private bool IsCurrentBody(EntityUid mindId, EntityUid body)
    {
        if (!TryComp(mindId, out MindComponent? mind))
            return false;

        return mind.OwnedEntity == body || mind.LastMob == body;
    }

    private bool IsInCurrentBodyInventory(EntityUid mindId, EntityUid entity)
    {
        var current = entity;
        while (Exists(current))
        {
            if (IsCurrentBody(mindId, current))
                return true;

            var parent = Transform(current).ParentUid;
            if (!_containers.TryGetContainingContainer(parent, current, out var container))
                return false;

            current = container.Owner;
        }

        return false;
    }

    private bool IsHandContainer(BaseContainer container)
    {
        return TryComp(container.Owner, out HandsComponent? hands) &&
               hands.Hands.Values.Any(hand => hand.Container == container);
    }

    private bool IsPersonalShipTerminalSlot(BaseContainer container)
    {
        return container.ID == CalamityPersonalIdComponent.PersonalIdSlotId &&
               (HasComp<CalamityPersonalShipTerminalComponent>(container.Owner) ||
                HasComp<CalamityShipNavigationConsoleComponent>(container.Owner));
    }

    private void OnBeforeThrow(ref BeforeThrowEvent args)
    {
        if (HasComp<CalamityPersonalIdComponent>(args.ItemUid))
            args.Cancelled = true;
    }

    private void OnIsUnequippingAttempt(IsUnequippingAttemptEvent args)
    {
        if (!TryComp(args.Equipment, out CalamityPersonalIdComponent? personalId))
            return;

        if (args.Slot != IdSlotName ||
            args.Unequipee != args.UnEquipTarget ||
            !IsCurrentBody(personalId.OwnerMind, args.UnEquipTarget) ||
            !_hands.CanPickupAnyHand(args.UnEquipTarget, args.Equipment))
        {
            args.Cancel();
            return;
        }

        AuthorizeCurrentContainerRemoval(args.Equipment);
    }

    private void OnFellDownThrowAttempt(
        EntityUid uid,
        CalamityPersonalIdComponent component,
        ref FellDownThrowAttemptEvent args)
    {
        args.Cancelled = true;
    }
}
