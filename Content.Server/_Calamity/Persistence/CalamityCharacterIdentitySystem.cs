using System.Linq;
using Content.Server.Preferences.Managers;
using Content.Shared._Calamity.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Preferences;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server._Calamity.Persistence;

/// <summary>
/// Binds an in-world character mind to the owning account's persistent character profile.
/// </summary>
public sealed class CalamityCharacterIdentitySystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedMindSystem _mindSystem = default!;
    [Dependency] private CalamityWorldPersistenceSystem _worldPersistence = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerSelectedCharacterChangedEvent>(OnPlayerSelectedCharacterChanged);
    }

    /// <summary>
    /// Checks that the restored mind still matches the character profile selected by its account.
    /// A mismatch permanently abandons the old body.
    /// </summary>
    public bool MatchesSelectedCharacter(NetUserId userId, EntityUid mindId, MindComponent mind)
    {
        if (!_preferences.TryGetCachedPreferences(userId, out var preferences) || preferences == null)
            return true;

        var hasProfileId = _preferences.TryGetCachedCharacterProfileId(
            userId,
            preferences.SelectedCharacterIndex,
            out var selectedProfileId);

        if (!TryComp(mindId, out CalamityPersistentCharacterComponent? identity))
        {
            identity = AddComp<CalamityPersistentCharacterComponent>(mindId);
            identity.CharacterSlot = FindLegacyCharacterSlot(preferences, mind.CharacterName)
                                     ?? preferences.SelectedCharacterIndex;
        }

        var matches = hasProfileId && identity.CharacterProfileId != 0
            ? identity.CharacterProfileId == selectedProfileId
            : identity.CharacterSlot == preferences.SelectedCharacterIndex;

        if (matches)
        {
            identity.CharacterSlot = preferences.SelectedCharacterIndex;
            if (hasProfileId)
                identity.CharacterProfileId = selectedProfileId;

            return true;
        }

        _worldPersistence.AbandonCharacter(mindId, mind);
        return false;
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !_preferences.TryGetCachedPreferences(ev.Player.UserId, out var preferences) || preferences == null ||
            !_mindSystem.TryGetMind(ev.Player.UserId, out var mindId, out _) || mindId == null)
        {
            return;
        }

        var identity = EnsureComp<CalamityPersistentCharacterComponent>(mindId.Value);
        identity.CharacterSlot = preferences.SelectedCharacterIndex;
        identity.CharacterProfileId = _preferences.TryGetCachedCharacterProfileId(
            ev.Player.UserId,
            preferences.SelectedCharacterIndex,
            out var profileId)
            ? profileId
            : 0;
    }

    private void OnPlayerSelectedCharacterChanged(PlayerSelectedCharacterChangedEvent ev)
    {
        if (!_configuration.GetCVar(CalamityCCVars.WorldPersistenceEnabled) ||
            !_mindSystem.TryGetMind(ev.UserId, out var mindId, out var mind) || mindId == null || mind == null ||
            !TryComp(mindId.Value, out CalamityPersistentCharacterComponent? identity))
        {
            return;
        }

        var matchesSelectedProfile = _preferences.TryGetCachedCharacterProfileId(
            ev.UserId,
            ev.SelectedCharacterSlot,
            out var selectedProfileId) &&
                                     identity.CharacterProfileId != 0 &&
                                     identity.CharacterProfileId == selectedProfileId;

        if (matchesSelectedProfile ||
            identity.CharacterProfileId == 0 && identity.CharacterSlot == ev.SelectedCharacterSlot)
        {
            identity.CharacterSlot = ev.SelectedCharacterSlot;
            if (matchesSelectedProfile)
                identity.CharacterProfileId = selectedProfileId;

            return;
        }

        _worldPersistence.AbandonCharacter(mindId.Value, mind);
    }

    private static int? FindLegacyCharacterSlot(PlayerPreferences preferences, string? characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return null;

        var matches = preferences.Characters
            .Where(pair => pair.Value is HumanoidCharacterProfile profile && profile.Name == characterName)
            .Select(pair => pair.Key)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }
}
