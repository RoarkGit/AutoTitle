namespace AutoTitle.Core;

/// <param name="Titles">The titles to pick from, or null to leave the title alone.</param>
/// <param name="Source">"Shared", or the label of the character whose settings won.</param>
public readonly record struct Resolution(TitleRule? Rule, IReadOnlyList<ushort>? Titles, string Source);

public static class RuleEngine
{
    public const string SharedSource = "Shared";

    /// <summary>
    /// Character rules, then shared rules, then the character's default, then the shared default.
    /// The shared steps are skipped for a character with <see cref="CharacterProfile.IncludeShared"/> off.
    /// </summary>
    public static Resolution Resolve(Configuration config, CharacterProfile? profile, GameContext ctx)
    {
        var own = profile is { UseOwnRules: true } ? profile : null;
        var shared = own == null || own.IncludeShared;

        if (own != null && FirstMatch(own.Rules, ctx) is { } ownRule)
            return new Resolution(ownRule, TitlesOf(ownRule), own.Label);
        if (shared && FirstMatch(config.Rules, ctx) is { } sharedRule)
            return new Resolution(sharedRule, TitlesOf(sharedRule), SharedSource);
        if (own?.DefaultTitleId is { } ownDefault)
            return new Resolution(null, [ownDefault], own.Label);
        return shared
            ? new Resolution(null, config.DefaultTitleId is { } id ? [id] : null, SharedSource)
            : new Resolution(null, null, own!.Label);
    }

    public static IReadOnlyList<ushort> TitlesOf(TitleRule rule) => rule.TitleIds.Count > 0 ? rule.TitleIds : [0];

    private static TitleRule? FirstMatch(List<TitleRule> rules, GameContext ctx) =>
        rules.FirstOrDefault(r => r.Enabled && Matches(r, ctx));

    /// <summary>
    /// Every condition with values selected has to match. Empty conditions are ignored, but a rule
    /// with nothing selected at all never matches, so a half-built rule can't match everywhere.
    /// </summary>
    public static bool Matches(TitleRule rule, GameContext ctx)
    {
        var any = false;
        foreach (var cond in rule.Conditions)
        {
            if (cond.Values.Count == 0) continue;
            if (!ctx.Matches(cond.Kind, cond.Values)) return false;
            any = true;
        }
        return any;
    }
}
