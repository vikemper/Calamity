using System;
using System.Collections.Generic;

namespace Content.Server.Database;

public sealed class CalamityRollbackState
{
    public int Version { get; set; } = 1;
    public List<CalamityPreferenceState> Preferences { get; set; } = [];
    public List<CalamityCompanyMemberState> CompanyMembers { get; set; } = [];
    public List<CalamitySafetyDepositBoxState> SafetyDepositBoxes { get; set; } = [];
}

public sealed class CalamityPreferenceState
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int SelectedCharacterSlot { get; set; }
    public string AdminOOCColor { get; set; } = string.Empty;
    public long MonoCoins { get; set; }
    public List<CalamityProfileState> Profiles { get; set; } = [];
}

public sealed class CalamityProfileState
{
    public int Id { get; set; }
    public int Slot { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string FlavorText { get; set; } = string.Empty;
    public int Age { get; set; }
    public int BankBalance { get; set; }
    public string Sex { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string Species { get; set; } = string.Empty;
    public string? Markings { get; set; }
    public string HairName { get; set; } = string.Empty;
    public string HairColor { get; set; } = string.Empty;
    public string FacialHairName { get; set; } = string.Empty;
    public string FacialHairColor { get; set; } = string.Empty;
    public string EyeColor { get; set; } = string.Empty;
    public string SkinColor { get; set; } = string.Empty;
    public float Height { get; set; }
    public float Width { get; set; }
    public int SpawnPriority { get; set; }
    public int PreferenceUnavailable { get; set; }
    public string Company { get; set; } = string.Empty;
    public string[] Flags { get; set; } = [];
    public List<CalamityProfileComponentState> Components { get; set; } = [];
    public List<CalamityProfileItemState> Items { get; set; } = [];
    public List<CalamityJobState> Jobs { get; set; } = [];
    public List<string> Antags { get; set; } = [];
    public List<string> Traits { get; set; } = [];
    public List<CalamityRoleLoadoutState> Loadouts { get; set; } = [];
}

public sealed class CalamityProfileComponentState
{
    public string Data { get; set; } = string.Empty;
    public bool Sticky { get; set; }
}

public sealed class CalamityProfileItemState
{
    public string Data { get; set; } = string.Empty;
    public bool Sticky { get; set; }
}

public sealed class CalamityJobState
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
}

public sealed class CalamityRoleLoadoutState
{
    public string RoleName { get; set; } = string.Empty;
    public string? EntityName { get; set; }
    public List<CalamityLoadoutGroupState> Groups { get; set; } = [];
}

public sealed class CalamityLoadoutGroupState
{
    public string GroupName { get; set; } = string.Empty;
    public List<string> Loadouts { get; set; } = [];
}

public sealed class CalamityCompanyMemberState
{
    public Guid PlayerUserId { get; set; }
    public bool Owner { get; set; }
    public string CompanyId { get; set; } = string.Empty;
}

public sealed class CalamitySafetyDepositBoxState
{
    public Guid BoxId { get; set; }
    public Guid OwnerUserId { get; set; }
    public int CharacterIndex { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public string ProtoId { get; set; } = string.Empty;
    public DateTime PurchaseDate { get; set; }
    public DateTime? LastWithdrawn { get; set; }
    public int? LastWithdrawnRoundId { get; set; }
    public List<CalamitySafetyDepositBoxItemState> Items { get; set; } = [];
}

public sealed class CalamitySafetyDepositBoxItemState
{
    public string EntityData { get; set; } = string.Empty;
    public DateTime DepositDate { get; set; }
}
