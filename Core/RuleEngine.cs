namespace AutoTitle.Core;

// The result of checking the rules. Titles is null if the title should be left alone, and Source is
// "Shared" or the label of the character whose settings won.
public readonly record struct Resolution(
    TitleRule? Rule,
    IReadOnlyList<ushort>? Titles,
    string Source
);

public static class RuleEngine
{
    public const string SharedSource = "Shared";

    /// <summary>
    /// Finds which rule applies. Checks the character's rules, then shared rules, then the
    /// character's default, then the shared default.
    /// </summary>
    /// <param name="config">The plugin config</param>
    /// <param name="profile">The current character's profile, if any</param>
    /// <param name="ctx">The current game state</param>
    public static Resolution Resolve(
        Configuration config,
        CharacterProfile? profile,
        GameContext ctx
    )
    {
        // Characters without their own rules just use the shared ones.
        CharacterProfile? own = null;
        if (profile != null && profile.UseOwnRules)
            own = profile;
        var useShared = own == null || own.IncludeShared;

        if (own != null)
        {
            var ownRule = FirstMatch(own.Rules, ctx);
            if (ownRule != null)
                return new Resolution(ownRule, TitlesOf(ownRule), own.Label);
        }

        if (useShared)
        {
            var sharedRule = FirstMatch(config.Rules, ctx);
            if (sharedRule != null)
                return new Resolution(sharedRule, TitlesOf(sharedRule), SharedSource);
        }

        // No rule matched, so fall back to the defaults.
        if (own != null && own.DefaultTitleId != null)
            return new Resolution(null, new[] { own.DefaultTitleId.Value }, own.Label);
        if (!useShared)
            return new Resolution(null, null, own!.Label);
        if (config.DefaultTitleId != null)
            return new Resolution(null, new[] { config.DefaultTitleId.Value }, SharedSource);
        return new Resolution(null, null, SharedSource);
    }

    /// <summary>Gets a rule's titles, treating an empty list as "no title".</summary>
    /// <param name="rule">The rule</param>
    public static IReadOnlyList<ushort> TitlesOf(TitleRule rule)
    {
        if (rule.TitleIds.Count == 0)
            return new ushort[] { 0 };
        return rule.TitleIds;
    }

    private static TitleRule? FirstMatch(List<TitleRule> rules, GameContext ctx)
    {
        foreach (var rule in rules)
        {
            if (rule.Enabled && Matches(rule, ctx))
                return rule;
        }
        return null;
    }

    /// <summary>
    /// Checks if all of a rule's conditions match. Empty conditions are skipped, but a rule with
    /// nothing selected at all never matches.
    /// </summary>
    /// <param name="rule">The rule</param>
    /// <param name="ctx">The current game state</param>
    public static bool Matches(TitleRule rule, GameContext ctx)
    {
        var any = false;
        foreach (var cond in rule.Conditions)
        {
            if (cond.Values.Count == 0)
                continue;
            if (!ctx.Matches(cond.Kind, cond.Values))
                return false;
            any = true;
        }
        return any;
    }
}
