namespace AutoTitle.Core;

// Saved in configs, don't renumber.
public enum ConditionKind
{
    Job = 0,
    Zone = 1,
    Duty = 3,
    DutyType = 4,
    Gearset = 5,
}

// Saved in configs, don't renumber.
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
    // Job conditions store both ClassJob IDs and groups, so groups start after any job ID.
    public const uint IdBase = 1000;

    public static uint ToValue(JobGroup group)
    {
        return IdBase + (uint)group;
    }
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

    public bool IncludeShared { get; set; } = true;

    public ushort? DefaultTitleId { get; set; }

    public List<TitleRule> Rules { get; set; } = new();

    // Unlocked title bitmask saved from the game, null until it's been loaded once.
    public List<byte>? UnlockedTitles { get; set; }

    public string Label
    {
        get
        {
            if (World.Length == 0)
                return Name;
            return $"{Name} @ {World}";
        }
    }
}

[Serializable]
public sealed class TitleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;

    // One is picked at random when the rule becomes active. Empty means no title.
    public List<ushort> TitleIds { get; set; } = new();

    public List<RuleCondition> Conditions { get; set; } = new();
}

[Serializable]
public sealed class RuleCondition
{
    public ConditionKind Kind { get; set; }
    public List<uint> Values { get; set; } = new();
}
