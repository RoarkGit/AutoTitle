namespace AutoTitle.Core;

public readonly record struct GameContext(
    uint JobId,
    IReadOnlyList<JobGroup> JobGroups,
    uint TerritoryId,
    uint DutyId,
    uint DutyTypeId,
    uint Gearset,
    bool Feminine)
{
    /// <summary>The current value for a kind, or 0 when there isn't one (not in a duty, no gearset).</summary>
    public uint ValueFor(ConditionKind kind) => kind switch
    {
        ConditionKind.Job => JobId,
        ConditionKind.Zone => TerritoryId,
        ConditionKind.Duty => DutyId,
        ConditionKind.DutyType => DutyTypeId,
        ConditionKind.Gearset => Gearset,
        _ => 0,
    };

    public bool Matches(ConditionKind kind, List<uint> values)
    {
        if (kind == ConditionKind.Job)
            return values.Contains(JobId) || JobGroups.Any(g => values.Contains(Core.JobGroups.ToValue(g)));

        var value = ValueFor(kind);
        return value != 0 && values.Contains(value);
    }
}
