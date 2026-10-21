using Dalamud.Configuration;
using AutoTitle.Core;

namespace AutoTitle;

[Serializable]
public sealed class Configuration : IPluginConfiguration, IRuleSet
{
    public int Version { get; set; } = 2;

    public bool Enabled { get; set; } = true;
    public bool NotifyInChat { get; set; } = true;

    /// <summary>Null leaves the title alone; 0 clears it.</summary>
    public ushort? DefaultTitleId { get; set; }

    public List<TitleRule> Rules { get; set; } = [];

    /// <summary>Keyed by content id. Every character that logs in gets an entry.</summary>
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    /// <summary>Brings a config saved by an older version up to date.</summary>
    public void Migrate()
    {
        if (Version >= 2) return;

        // Version 2 dropped Map conditions (kind 2); zone conditions cover the same places.
        foreach (var rule in AllRules())
            rule.Conditions.RemoveAll(c => (int)c.Kind == 2);

        Version = 2;
        Save();
    }

    /// <summary>
    /// Rewrites titles, zones, and duties to the id they're matched as, for settings saved before
    /// those were merged into one entry. Run on every load, since a game update can add more.
    /// </summary>
    public void MergeValues(GameData data)
    {
        var changed = false;
        foreach (IRuleSet set in Characters.Values.Prepend<IRuleSet>(this))
        {
            if (set.DefaultTitleId is not { } id || data.CanonicalTitle(id) == id) continue;
            set.DefaultTitleId = data.CanonicalTitle(id);
            changed = true;
        }
        foreach (var rule in AllRules().Where(r => data.CanonicalTitle(r.TitleId) != r.TitleId))
        {
            rule.TitleId = data.CanonicalTitle(rule.TitleId);
            changed = true;
        }
        foreach (var cond in AllRules().SelectMany(r => r.Conditions))
        {
            if (cond.Kind is not (ConditionKind.Zone or ConditionKind.Duty)) continue;
            var values = cond.Values.Select(v => data.Canonical(cond.Kind, v)).Distinct().ToList();
            if (values.SequenceEqual(cond.Values)) continue;
            cond.Values = values;
            changed = true;
        }
        if (changed) Save();
    }

    private IEnumerable<TitleRule> AllRules() => Rules.Concat(Characters.Values.SelectMany(c => c.Rules));
}
