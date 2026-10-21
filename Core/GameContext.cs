namespace AutoTitle.Core;

public readonly record struct GameContext(
    uint JobId,
    IReadOnlyList<JobGroup> JobGroups,
    uint TerritoryId,
    uint DutyId,
    uint DutyTypeId,
    uint Gearset,
    bool Feminine
)
{
    /// <summary>Gets the current value for a condition kind, or 0 if there isn't one.</summary>
    /// <param name="kind">The condition kind</param>
    public uint ValueFor(ConditionKind kind)
    {
        switch (kind)
        {
            case ConditionKind.Job:
                return JobId;
            case ConditionKind.Zone:
                return TerritoryId;
            case ConditionKind.Duty:
                return DutyId;
            case ConditionKind.DutyType:
                return DutyTypeId;
            case ConditionKind.Gearset:
                return Gearset;
            default:
                return 0;
        }
    }

    /// <summary>Checks if the current value for a condition kind is in the given values.</summary>
    /// <param name="kind">The condition kind</param>
    /// <param name="values">The values to check against</param>
    public bool Matches(ConditionKind kind, List<uint> values)
    {
        // Job conditions can also list groups like "Tanks".
        if (kind == ConditionKind.Job)
        {
            if (values.Contains(JobId))
                return true;
            foreach (var group in JobGroups)
            {
                if (values.Contains(Core.JobGroups.ToValue(group)))
                    return true;
            }
            return false;
        }

        var value = ValueFor(kind);
        return value != 0 && values.Contains(value);
    }
}
