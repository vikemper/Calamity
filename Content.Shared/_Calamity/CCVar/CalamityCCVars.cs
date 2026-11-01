using Robust.Shared.Configuration;

namespace Content.Shared._Calamity.CCVar;

[CVarDefs]
public sealed class CalamityCCVars
{
    /// <summary>
    /// Keeps Calamity in one continuous game session instead of restarting rounds.
    /// </summary>
    public static readonly CVarDef<bool> PersistentRoundEnabled =
        CVarDef.Create("calamity.persistence.persistent_round", true, CVar.SERVERONLY);

    /// <summary>
    /// Enables automatic snapshots of the active world map.
    /// </summary>
    public static readonly CVarDef<bool> WorldPersistenceEnabled =
        CVarDef.Create("calamity.persistence.enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Time between global world-map snapshots, in seconds.
    /// </summary>
    public static readonly CVarDef<float> WorldPersistenceInterval =
        CVarDef.Create("calamity.persistence.interval", 1200f, CVar.SERVERONLY);

    /// <summary>
    /// How long an abandoned persistent character's corpse remains before despawning, in seconds.
    /// </summary>
    public static readonly CVarDef<float> AbandonedCharacterCorpseLifetime =
        CVarDef.Create("calamity.persistence.abandoned_character_corpse_lifetime", 1200f, CVar.SERVERONLY);
}
