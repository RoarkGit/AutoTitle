using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using AutoTitle.Core;

namespace AutoTitle.Windows;

public sealed class MainWindow : Window
{
    private static readonly Choice NoTitle = new(0, "(No title)");

    // Picker id for leaving the default unset; title ids never get this high.
    private const uint UnsetId = uint.MaxValue;
    private static readonly ConditionKind[] Kinds = Enum.GetValues<ConditionKind>();

    private readonly Plugin plugin;
    private bool dirty;

    /// <summary>Content id of the character whose settings are shown, or null for the shared ones.</summary>
    private ulong? selected;

    private enum RuleState
    {
        Idle,
        Active,
        Outranked,
        Blocked,
    }

    public MainWindow(Plugin plugin) : base("AutoTitle###autotitle_main")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Configuration Cfg => plugin.Config;
    private bool Feminine => plugin.Applier.Context?.Feminine ?? false;

    private CharacterProfile? SelectedProfile =>
        selected is { } id && Cfg.Characters.TryGetValue(id, out var p) ? p : null;

    private IRuleSet Editing => (IRuleSet?)SelectedProfile ?? Cfg;

    // Another character's rules can't be checked against this character's job and zone.
    private bool EditingCurrentCharacter => selected == null || selected == plugin.Applier.CharacterId;

    private bool CurrentSkipsShared =>
        Cfg.Characters.TryGetValue(plugin.Applier.CharacterId, out var p) && p is { UseOwnRules: true, IncludeShared: false };

    public override void OnOpen()
    {
        plugin.Applier.LoadTitleList();

        // Start on whichever settings are in effect for the logged-in character.
        var id = plugin.Applier.CharacterId;
        selected = Cfg.Characters.TryGetValue(id, out var p) && p.UseOwnRules ? id : null;
    }

    public override void Draw()
    {
        dirty = false;
        plugin.Applier.CheckTitleList();

        DrawHeader();
        ImGui.Spacing();

        SeparatorText("Settings for");
        DrawProfilePicker();
        if (SelectedProfile is not { } profile || DrawProfileOptions(profile))
        {
            SeparatorText("Default");
            DrawDefault();
            ImGui.Spacing();

            SeparatorText("Rules");
            DrawRules();
        }

        if (!dirty) return;
        Cfg.Save();
        plugin.Applier.Refresh();
    }

    private void DrawHeader()
    {
        var enabled = Cfg.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) plugin.SetEnabled(enabled);

        ImGui.SameLine();
        if (ImGui.Button("Apply now")) plugin.Applier.Reapply();
        Tooltip("Re-send the title for the current situation, e.g. after changing it by hand.");

        ImGui.SameLine();
        var notify = Cfg.NotifyInChat;
        if (ImGui.Checkbox("Chat message on change", ref notify))
        {
            Cfg.NotifyInChat = notify;
            dirty = true;
        }

        var applier = plugin.Applier;
        if (applier.Context is not { } ctx)
        {
            ImGui.TextDisabled(applier.Status);
            return;
        }

        var data = plugin.GameData;
        var duty = ctx.DutyId != 0 ? $" | {data.Label(ConditionKind.Duty, ctx.DutyId)}" : "";
        ImGui.TextDisabled($"{data.Label(ConditionKind.Job, ctx.JobId)} | {data.Label(ConditionKind.Zone, ctx.TerritoryId)}{duty}");

        var res = applier.Resolution;
        var source = res.Rule is { } r ? $"rule \"{r.Name}\"" : "default";
        var target = res.TitleId is { } id ? data.TitleLabel(id, ctx.Feminine) : "(leave unchanged)";
        ImGui.TextUnformatted($"Wants: {target} via {source} ({res.Source})");
        ImGui.TextDisabled(applier.Status);
    }

    private void DrawProfilePicker()
    {
        var current = plugin.Applier.CharacterId;
        string Describe(ulong id, CharacterProfile p) =>
            p.Label + (p.UseOwnRules ? " (own rules)" : "") + (id == current ? " [you]" : "");

        var preview = SelectedProfile is { } sel ? Describe(selected!.Value, sel) : "Shared (all characters)";
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##profile", preview))
        {
            if (combo)
            {
                if (ImGui.Selectable("Shared (all characters)", selected == null)) selected = null;
                foreach (var (id, p) in Cfg.Characters.OrderBy(kv => kv.Value.Label, StringComparer.OrdinalIgnoreCase))
                {
                    if (ImGui.Selectable($"{Describe(id, p)}##{id}", selected == id)) selected = id;
                }
            }
        }
        ImGui.SameLine();
        Hint(selected == null
            ? "Applies to every character without rules of its own."
            : "Characters appear here once they've logged in with AutoTitle installed.");
    }

    /// <returns>Whether the character uses its own rules, i.e. whether there's anything to edit below.</returns>
    private bool DrawProfileOptions(CharacterProfile profile)
    {
        var own = profile.UseOwnRules;
        if (ImGui.Checkbox("Use rules specific to this character", ref own))
        {
            profile.UseOwnRules = own;
            dirty = true;
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, "Forget"))
            {
                Cfg.Characters.Remove(selected!.Value);
                selected = null;
                dirty = true;
                return false;
            }
        }
        Tooltip("Hold Ctrl to remove this character and its rules.\nIt reappears, with no rules, next time it logs in.",
            ImGuiHoveredFlags.AllowWhenDisabled);

        if (!profile.UseOwnRules)
        {
            Hint("This character follows the shared rules.");
            return false;
        }

        var include = profile.IncludeShared;
        if (ImGui.Checkbox("Fall back to shared rules", ref include))
        {
            profile.IncludeShared = include;
            dirty = true;
        }
        Hint(profile.IncludeShared
            ? "This character's rules are checked first, then the shared rules, then the defaults."
            : "Only this character's rules and default apply; shared settings are ignored.");
        return true;
    }

    private void DrawDefault()
    {
        // With shared fallback on, a character with no default of its own uses the shared one.
        var unset = SelectedProfile is { IncludeShared: true } ? "(Use shared default)" : "(Don't change)";
        var set = Editing;
        var preview = set.DefaultTitleId is { } id ? plugin.GameData.TitleLabel(id, Feminine) : unset;
        if (SearchCombo.Draw("##default", preview, plugin.GameData.Titles(Feminine), out var picked, 300 * ImGuiHelpers.GlobalScale,
                new Choice(UnsetId, unset), NoTitle))
        {
            set.DefaultTitleId = picked == UnsetId ? null : (ushort)picked;
            dirty = true;
        }
        ImGui.SameLine();
        Hint("Used when no rule matches.");
    }

    private void DrawRules()
    {
        Hint("Checked top to bottom, the first rule whose conditions all match wins. " +
             "Within one condition, any listed value counts.");

        var rules = Editing.Rules;
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add rule"))
        {
            rules.Add(new TitleRule { Name = $"Rule {rules.Count + 1}" });
            dirty = true;
        }

        int? moveFrom = null, moveTo = null, delete = null;
        for (var i = 0; i < rules.Count; i++)
        {
            using var id = ImRaii.PushId(rules[i].Id.ToString());
            ImGui.Spacing();
            DrawRule(rules[i], i, rules.Count, ref moveFrom, ref moveTo, ref delete);
        }

        // Applied after the loop so the list isn't changed while it's being drawn.
        if (moveFrom is { } from && moveTo is { } to)
        {
            (rules[from], rules[to]) = (rules[to], rules[from]);
            dirty = true;
        }
        if (delete is { } del)
        {
            rules.RemoveAt(del);
            dirty = true;
        }
    }

    /// <summary>
    /// Draws a border around the rule by hand: this ImGui version can't auto-size a child window's
    /// height, so a bordered child would fill the rest of the window.
    /// </summary>
    private void DrawRule(TitleRule rule, int index, int count, ref int? moveFrom, ref int? moveTo, ref int? delete)
    {
        var pad = ImGui.GetStyle().WindowPadding.X / 2;
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;

        ImGui.Dummy(new Vector2(0, pad));
        ImGui.Indent(pad);
        ImGui.BeginGroup();
        DrawRuleBody(rule, index, count, ref moveFrom, ref moveTo, ref delete);
        ImGui.EndGroup();
        ImGui.Unindent(pad);

        var bottom = ImGui.GetItemRectMax().Y + pad;
        ImGui.GetWindowDrawList().AddRect(start, new Vector2(right, bottom),
            ImGui.GetColorU32(ImGuiCol.Border), ImGui.GetStyle().FrameRounding);
        ImGui.Dummy(new Vector2(0, pad));
    }

    private void DrawRuleBody(TitleRule rule, int index, int count, ref int? moveFrom, ref int? moveTo, ref int? delete)
    {
        var (state, message) = GetRuleState(rule);
        DrawRuleIcon(state, message);
        ImGui.SameLine();

        var enabled = rule.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            rule.Enabled = enabled;
            dirty = true;
        }
        Tooltip("Enabled");

        ImGui.SameLine();
        var name = rule.Name;
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##name", ref name, 64))
        {
            rule.Name = name;
            dirty = true;
        }

        ImGui.SameLine();
        if (SearchCombo.Draw("##title", plugin.GameData.TitleLabel(rule.TitleId, Feminine),
                plugin.GameData.Titles(Feminine), out var picked, 200 * ImGuiHelpers.GlobalScale, NoTitle))
        {
            rule.TitleId = (ushort)picked;
            dirty = true;
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(index == 0))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.ArrowUp)) (moveFrom, moveTo) = (index, index - 1);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(index == count - 1))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.ArrowDown)) (moveFrom, moveTo) = (index, index + 1);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash)) delete = index;
        }
        Tooltip("Hold Ctrl to delete", ImGuiHoveredFlags.AllowWhenDisabled);

        int? removeCondition = null;
        for (var c = 0; c < rule.Conditions.Count; c++)
        {
            using var cid = ImRaii.PushId(c);
            if (DrawCondition(rule.Conditions[c])) removeCondition = c;
        }
        if (removeCondition is { } rc)
        {
            rule.Conditions.RemoveAt(rc);
            dirty = true;
        }

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Condition"))
        {
            // Two conditions of the same kind would both have to match, so default to an unused kind.
            var used = rule.Conditions.Select(x => x.Kind).ToHashSet();
            rule.Conditions.Add(new RuleCondition { Kind = Kinds.FirstOrDefault(k => !used.Contains(k)) });
            dirty = true;
        }
        if (rule.Conditions.All(x => x.Values.Count == 0))
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, rule.Conditions.Count == 0
                ? "Add a condition to use this rule."
                : "Select a value for a condition to use this rule.");
        }
    }

    private (RuleState State, string Message) GetRuleState(TitleRule rule)
    {
        var applier = plugin.Applier;
        if (applier.Context is not { } ctx || !EditingCurrentCharacter)
            return (RuleState.Idle, "Status is shown for the character you're logged in as.");
        if (!rule.Enabled)
            return (RuleState.Idle, "Disabled.");
        if (rule.Conditions.All(c => c.Values.Count == 0))
            return (RuleState.Idle, "No conditions set yet.");
        if (!RuleEngine.Matches(rule, ctx))
            return (RuleState.Idle, "Conditions don't match right now.");
        if (selected == null && CurrentSkipsShared)
            return (RuleState.Idle, "Conditions match, but this character doesn't use shared rules.");

        // Checked before priority, so a broken rule further down still stands out.
        var res = applier.Resolution;
        var winning = res.Rule == rule;
        if (applier.IsUnlocked(rule.TitleId) == false)
        {
            var title = plugin.GameData.TitleLabel(rule.TitleId, Feminine);
            return (RuleState.Blocked, winning
                ? $"Would be active, but {title} isn't unlocked on this character, so the title is left alone."
                : $"Conditions match, but {title} isn't unlocked on this character.");
        }
        if (winning)
            return (RuleState.Active, "Active: this rule's title is the one in use.");

        var where = res.Source == RuleEngine.SharedSource ? "shared" : "character-specific";
        var winner = res.Rule is { } r ? $"{where} rule \"{r.Name}\"" : $"the {where} default";
        return (RuleState.Outranked, $"Conditions match, but {winner} takes priority.");
    }

    private static void DrawRuleIcon(RuleState state, string message)
    {
        var (icon, color) = state switch
        {
            RuleState.Active => (FontAwesomeIcon.Check, ImGuiColors.HealerGreen),
            RuleState.Outranked => (FontAwesomeIcon.Check, ImGuiColors.DalamudYellow),
            RuleState.Blocked => (FontAwesomeIcon.Times, ImGuiColors.DalamudRed),
            _ => (FontAwesomeIcon.Minus, ImGuiColors.DalamudGrey3),
        };
        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
        Tooltip(message);
    }

    /// <returns>Whether the condition's remove button was pressed.</returns>
    private bool DrawCondition(RuleCondition cond)
    {
        var data = plugin.GameData;
        var removed = ImGuiComponents.IconButton(FontAwesomeIcon.Times);
        Tooltip("Remove condition");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##kind", KindName(cond.Kind)))
        {
            if (combo)
            {
                foreach (var k in Kinds)
                {
                    if (!ImGui.Selectable(KindName(k), k == cond.Kind) || k == cond.Kind) continue;
                    // Values are ids from a different sheet per kind.
                    cond.Kind = k;
                    cond.Values.Clear();
                    dirty = true;
                }
            }
        }

        if (cond.Kind == ConditionKind.Gearset)
        {
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Matches by gearset number, as shown in the Gear Set List. Names shown are the " +
                                       "logged-in character's; numbers mean different gearsets on different characters, " +
                                       "so these usually belong in character-specific rules.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("is");

        uint? removeValue = null;
        foreach (var v in cond.Values)
        {
            var label = $"{data.Label(cond.Kind, v)}  x";
            WrapFor(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2);
            if (ImGui.SmallButton($"{label}##{v}")) removeValue = v;
            Tooltip("Click to remove");
        }
        if (removeValue is { } rv)
        {
            cond.Values.Remove(rv);
            dirty = true;
        }

        var addWidth = 140 * ImGuiHelpers.GlobalScale;
        WrapFor(addWidth);
        if (SearchCombo.DrawMulti("##add", cond.Values.Count == 0 ? "Select..." : "Edit...",
                data.Choices(cond.Kind), cond.Values, addWidth))
            dirty = true;

        var current = plugin.Applier.Context?.ValueFor(cond.Kind) ?? 0;
        using (ImRaii.Disabled(current == 0 || cond.Values.Contains(current)))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("+ Current"))
            {
                cond.Values.Add(current);
                dirty = true;
            }
        }
        if (current != 0) Tooltip(data.Label(cond.Kind, current), ImGuiHoveredFlags.AllowWhenDisabled);

        return removed;
    }

    /// <summary>SameLine, unless an item this wide wouldn't fit before the rule's border.</summary>
    private static void WrapFor(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X < width) ImGui.NewLine();
    }

    private static string KindName(ConditionKind kind) => kind switch
    {
        ConditionKind.DutyType => "Duty type",
        _ => kind.ToString(),
    };

    private static void SeparatorText(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.Spacing();
    }

    private static void Hint(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
        ImGui.TextWrapped(text);
    }

    private static void Tooltip(string text, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
    {
        if (!ImGui.IsItemHovered(flags)) return;
        using var tip = ImRaii.Tooltip();
        ImGui.TextUnformatted(text);
    }
}
