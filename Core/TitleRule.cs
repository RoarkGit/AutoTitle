namespace AutoTitle.Core;

/// <summary>Stored in configs, so don't renumber.</summary>
public enum ConditionKind
{
    Job = 0,
    Zone = 1,
    Duty = 3,
    DutyType = 4,
    Gearset = 5,
}

/// <summary>Groups a Job condition can select instead of single jobs. Stored in configs, so don't renumber.</summary>
public enum JobGroup : uint
{
    None = 0,
    Tank = 1,
    Healer = 2,
    Melee = 3,
    PhysicalRanged = 4,
    MagicalRanged = 5,
    DiscipleOfTheHand = 6,
    DiscipleOfTheLand = 7,
    Dps = 8,
    DiscipleOfWar = 9,
    DiscipleOfMagic = 10,
}

public static class JobGroups
{
    // Job condition values hold both ClassJob row ids and groups, so groups are offset past any job id.
    public const uint IdBase = 1000;

    public static uint ToValue(JobGroup group) => IdBase + (uint)group;
}

public interface IRuleSet
{
    ushort? DefaultTitleId { get; set; }
    List<TitleRule> Rules { get; }
}

[Serializable]
public sealed class CharacterProfile : IRuleSet
{
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    public bool UseOwnRules { get; set; }

    /// <summary>Check the shared rules and default after this character's own.</summary>
    public bool IncludeShared { get; set; } = true;

    /// <summary>Null falls through to the shared default, or leaves the title alone without <see cref="IncludeShared"/>.</summary>
    public ushort? DefaultTitleId { get; set; }

    public List<TitleRule> Rules { get; set; } = [];

    /// <summary>
    /// The game's unlocked title bitmask, saved whenever it has one so later sessions can check
    /// titles without asking the server. Null until it has been loaded once for this character.
    /// </summary>
    public List<byte>? UnlockedTitles { get; set; }

    public string Label => World.Length > 0 ? $"{Name} @ {World}" : Name;
}

[Serializable]
public sealed class TitleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// One is picked at random each time the rule takes over. 0 means no title, and so does an
    /// empty list.
    /// </summary>
    public List<ushort> TitleIds { get; set; } = [];

    public List<RuleCondition> Conditions { get; set; } = [];
}

[Serializable]
public sealed class RuleCondition
{
    public ConditionKind Kind { get; set; }
    public List<uint> Values { get; set; } = [];
}
