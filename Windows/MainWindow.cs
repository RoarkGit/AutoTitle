using System.Numerics;
using AutoTitle.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AutoTitle.Windows;

/// <summary>
/// The settings window, for editing rules and seeing which one is active.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Choice NoTitle = new(0, "(No title)");

    // ID for the "unset" option in the default title picker.
    private const uint UnsetId = uint.MaxValue;
    private static readonly ConditionKind[] Kinds = Enum.GetValues<ConditionKind>();

    private readonly Plugin plugin;
    private bool dirty;

    // Title list with "(No title)" added, cached so its width only gets measured once.
    private List<Choice>? ruleTitles;
    private bool ruleTitlesFeminine;

    // The character whose settings are being edited, or null for shared settings.
    private ulong? selected;

    private enum RuleState
    {
        Idle,
        Active,
        Outranked,
        Blocked,
    }

    public MainWindow(Plugin plugin)
        : base("AutoTitle###autotitle_main")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Configuration Cfg
    {
        get { return plugin.Config; }
    }

    private bool Feminine
    {
        get
        {
            var ctx = plugin.Applier.Context;
            return ctx != null && ctx.Value.Feminine;
        }
    }

    private CharacterProfile? SelectedProfile
    {
        get
        {
            if (selected == null)
                return null;
            Cfg.Characters.TryGetValue(selected.Value, out var profile);
            return profile;
        }
    }

    // The rules and default being edited, either a character's or the shared ones.
    private IRuleSet Editing
    {
        get
        {
            var profile = SelectedProfile;
            if (profile != null)
                return profile;
            return Cfg;
        }
    }

    // We can't show status for another character's rules.
    private bool EditingCurrentCharacter
    {
        get { return selected == null || selected == plugin.Applier.CharacterId; }
    }

    private bool CurrentSkipsShared
    {
        get
        {
            if (!Cfg.Characters.TryGetValue(plugin.Applier.CharacterId, out var profile))
                return false;
            return profile.UseOwnRules && !profile.IncludeShared;
        }
    }

    public override void OnOpen()
    {
        plugin.Applier.LoadTitleList();

        // Start on whichever settings the current character is using.
        var id = plugin.Applier.CharacterId;
        selected = null;
        if (Cfg.Characters.TryGetValue(id, out var profile) && profile.UseOwnRules)
            selected = id;
    }

    public override void Draw()
    {
        dirty = false;
        plugin.Applier.CheckTitleList();

        DrawHeader();
        ImGui.Spacing();

        SeparatorText("Settings for");
        DrawProfilePicker();

        // Characters that follow the shared rules have nothing else to edit.
        var showRules = true;
        var profile = SelectedProfile;
        if (profile != null)
            showRules = DrawProfileOptions(profile);

        if (showRules)
        {
            SeparatorText("Default");
            DrawDefault();
            ImGui.Spacing();

            SeparatorText("Rules");
            DrawRules();
        }

        // Save and re-check rules if anything changed.
        if (dirty)
        {
            Cfg.Save();
            plugin.Applier.Refresh();
        }
    }

    private void DrawHeader()
    {
        var enabled = Cfg.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
            plugin.SetEnabled(enabled);

        ImGui.SameLine();
        if (ImGui.Button("Apply now"))
            plugin.Applier.Reapply();
        Tooltip("Re-send the title for the current situation, e.g. after changing it by hand.");

        ImGui.SameLine();
        var notify = Cfg.NotifyInChat;
        if (ImGui.Checkbox("Chat message on change", ref notify))
        {
            Cfg.NotifyInChat = notify;
            dirty = true;
        }

        var applier = plugin.Applier;
        if (applier.Context == null)
        {
            ImGui.TextDisabled(applier.Status);
            return;
        }
        var ctx = applier.Context.Value;
        var data = plugin.GameData;

        // Show current job/zone/duty.
        var job = data.Label(ConditionKind.Job, ctx.JobId);
        var zone = data.Label(ConditionKind.Zone, ctx.TerritoryId);
        var where = $"{job} | {zone}";
        if (ctx.DutyId != 0)
            where += $" | {data.Label(ConditionKind.Duty, ctx.DutyId)}";
        ImGui.TextDisabled(where);

        // Show which title we want and why.
        var res = applier.Resolution;
        var source = "default";
        if (res.Rule != null)
            source = $"rule \"{res.Rule.Name}\"";
        var target = "(leave unchanged)";
        if (applier.DesiredTitleId != null)
            target = data.TitleLabel(applier.DesiredTitleId.Value, ctx.Feminine);
        var from = res.Source;
        if (res.Titles != null && res.Titles.Count > 1)
            from += $", picked from {res.Titles.Count}";
        ImGui.TextUnformatted($"Wants: {target} via {source} ({from})");
        ImGui.TextDisabled(applier.Status);
    }

    private void DrawProfilePicker()
    {
        var preview = "Shared (all characters)";
        var profile = SelectedProfile;
        if (profile != null)
            preview = DescribeProfile(selected!.Value, profile);

        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##profile", preview))
        {
            if (combo)
            {
                if (ImGui.Selectable("Shared (all characters)", selected == null))
                    selected = null;

                var ids = Cfg.Characters.Keys.ToList();
                ids.Sort(
                    (a, b) =>
                        string.Compare(
                            Cfg.Characters[a].Label,
                            Cfg.Characters[b].Label,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
                foreach (var id in ids)
                {
                    var label = DescribeProfile(id, Cfg.Characters[id]);
                    if (ImGui.Selectable($"{label}##{id}", selected == id))
                        selected = id;
                }
            }
        }

        ImGui.SameLine();
        if (selected == null)
            Hint("Applies to every character without rules of its own.");
        else
            Hint("Characters appear here once they've logged in with AutoTitle installed.");
    }

    private string DescribeProfile(ulong id, CharacterProfile profile)
    {
        var label = profile.Label;
        if (profile.UseOwnRules)
            label += " (own rules)";
        if (id == plugin.Applier.CharacterId)
            label += " [you]";
        return label;
    }

    // Returns whether the character uses its own rules, i.e. whether there's anything else to edit.
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
        Tooltip(
            "Hold Ctrl to remove this character and its rules.\n"
                + "It reappears, with no rules, next time it logs in.",
            ImGuiHoveredFlags.AllowWhenDisabled
        );

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
        if (profile.IncludeShared)
            Hint(
                "This character's rules are checked first, then the shared rules, "
                    + "then the defaults."
            );
        else
            Hint("Only this character's rules and default apply, shared settings are ignored.");
        return true;
    }

    private void DrawDefault()
    {
        // With shared fallback on, a character without its own default uses the shared one.
        var unset = "(Don't change)";
        var profile = SelectedProfile;
        if (profile != null && profile.IncludeShared)
            unset = "(Use shared default)";

        var set = Editing;
        var preview = unset;
        if (set.DefaultTitleId != null)
            preview = plugin.GameData.TitleLabel(set.DefaultTitleId.Value, Feminine);

        var titles = plugin.GameData.Titles(Feminine);
        var width = 300 * ImGuiHelpers.GlobalScale;
        if (
            SearchCombo.Draw(
                "##default",
                preview,
                titles,
                out var picked,
                width,
                new Choice(UnsetId, unset),
                NoTitle
            )
        )
        {
            if (picked == UnsetId)
                set.DefaultTitleId = null;
            else
                set.DefaultTitleId = (ushort)picked;
            dirty = true;
        }
        ImGui.SameLine();
        Hint("Used when no rule matches.");
    }

    private void DrawRules()
    {
        Hint(
            "Checked top to bottom, the first rule whose conditions all match wins. "
                + "Within one condition, any listed value counts."
        );

        var rules = Editing.Rules;
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add rule"))
        {
            rules.Add(new TitleRule { Name = $"Rule {rules.Count + 1}" });
            dirty = true;
        }

        var moveFrom = -1;
        var moveTo = -1;
        var delete = -1;
        for (var i = 0; i < rules.Count; i++)
        {
            using var id = ImRaii.PushId(rules[i].Id.ToString());
            ImGui.Spacing();
            DrawRule(rules[i], i, rules.Count, ref moveFrom, ref moveTo, ref delete);
        }

        // Do this after the loop so we don't modify the list while drawing it.
        if (moveFrom != -1)
        {
            var moved = rules[moveFrom];
            rules[moveFrom] = rules[moveTo];
            rules[moveTo] = moved;
            dirty = true;
        }
        if (delete != -1)
        {
            rules.RemoveAt(delete);
            dirty = true;
        }
    }

    // The border is drawn by hand since a bordered child can't auto-size its height and fills the
    // window.
    private void DrawRule(
        TitleRule rule,
        int index,
        int count,
        ref int moveFrom,
        ref int moveTo,
        ref int delete
    )
    {
        var style = ImGui.GetStyle();
        var pad = style.WindowPadding.X / 2;
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;

        ImGui.Dummy(new Vector2(0, pad));
        ImGui.Indent(pad);
        ImGui.BeginGroup();
        DrawRuleBody(rule, index, count, ref moveFrom, ref moveTo, ref delete);
        ImGui.EndGroup();
        ImGui.Unindent(pad);

        var bottom = ImGui.GetItemRectMax().Y + pad;
        var color = ImGui.GetColorU32(ImGuiCol.Border);
        ImGui
            .GetWindowDrawList()
            .AddRect(start, new Vector2(right, bottom), color, style.FrameRounding);
        ImGui.Dummy(new Vector2(0, pad));
    }

    private void DrawRuleBody(
        TitleRule rule,
        int index,
        int count,
        ref int moveFrom,
        ref int moveTo,
        ref int delete
    )
    {
        // Header row: status, enabled, name, titles, move/delete buttons.
        var state = GetRuleState(rule, out var message);
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
        DrawRuleTitles(rule);

        ImGui.SameLine();
        using (ImRaii.Disabled(index == 0))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.ArrowUp))
            {
                moveFrom = index;
                moveTo = index - 1;
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(index == count - 1))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.ArrowDown))
            {
                moveFrom = index;
                moveTo = index + 1;
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                delete = index;
        }
        Tooltip("Hold Ctrl to delete.", ImGuiHoveredFlags.AllowWhenDisabled);

        // Conditions.
        var removeCondition = -1;
        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            if (DrawCondition(rule.Conditions[i]))
                removeCondition = i;
        }
        if (removeCondition != -1)
        {
            rule.Conditions.RemoveAt(removeCondition);
            dirty = true;
        }

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Condition"))
        {
            rule.Conditions.Add(new RuleCondition { Kind = UnusedKind(rule) });
            dirty = true;
        }

        if (!HasAnyValues(rule))
        {
            ImGui.SameLine();
            if (rule.Conditions.Count == 0)
                ImGui.TextColored(ImGuiColors.DalamudYellow, "Add a condition to use this rule.");
            else
                ImGui.TextColored(
                    ImGuiColors.DalamudYellow,
                    "Select a value for a condition to use this rule."
                );
        }
    }

    private void DrawRuleTitles(TitleRule rule)
    {
        // The picker works with uint IDs, titles are ushort.
        var values = new List<uint>();
        foreach (var id in rule.TitleIds)
            values.Add(id);

        var width = 200 * ImGuiHelpers.GlobalScale;
        if (SearchCombo.DrawMulti("##title", TitlesPreview(rule), RuleTitles(), values, width))
        {
            rule.TitleIds.Clear();
            foreach (var id in values)
                rule.TitleIds.Add((ushort)id);
            dirty = true;
        }

        if (rule.TitleIds.Count > 1)
        {
            var tip = "One is picked at random each time this rule takes over:";
            foreach (var id in rule.TitleIds)
                tip += "\n" + TitleName(id);
            Tooltip(tip);
        }
    }

    // Defaults new conditions to a kind the rule isn't using yet.
    private static ConditionKind UnusedKind(TitleRule rule)
    {
        foreach (var kind in Kinds)
        {
            var used = false;
            foreach (var cond in rule.Conditions)
            {
                if (cond.Kind == kind)
                    used = true;
            }
            if (!used)
                return kind;
        }
        return Kinds[0];
    }

    private static bool HasAnyValues(TitleRule rule)
    {
        foreach (var cond in rule.Conditions)
        {
            if (cond.Values.Count > 0)
                return true;
        }
        return false;
    }

    private RuleState GetRuleState(TitleRule rule, out string message)
    {
        var applier = plugin.Applier;
        if (applier.Context == null || !EditingCurrentCharacter)
        {
            message = "Only shown for your current character.";
            return RuleState.Idle;
        }
        if (!rule.Enabled)
        {
            message = "Disabled.";
            return RuleState.Idle;
        }
        if (!HasAnyValues(rule))
        {
            message = "No conditions set.";
            return RuleState.Idle;
        }
        if (!RuleEngine.Matches(rule, applier.Context.Value))
        {
            message = "Doesn't match right now.";
            return RuleState.Idle;
        }
        if (selected == null && CurrentSkipsShared)
        {
            message = "Matches, but this character doesn't use shared rules.";
            return RuleState.Idle;
        }

        // Check this before priority so a broken rule further down still shows up red.
        var titles = RuleEngine.TitlesOf(rule);
        var locked = new List<string>();
        foreach (var id in titles)
        {
            if (applier.IsUnlocked(id) == false)
                locked.Add(TitleName(id));
        }
        if (locked.Count == titles.Count)
        {
            if (titles.Count == 1)
                message = $"{TitleName(titles[0])} isn't unlocked on this character.";
            else
                message = "None of these titles are unlocked on this character.";
            return RuleState.Blocked;
        }

        var skipped = "";
        if (locked.Count > 0)
            skipped = $"\nNot unlocked: {string.Join(", ", locked)}.";

        var res = applier.Resolution;
        if (res.Rule == rule)
        {
            message = "Active.";
            if (titles.Count > 1 && applier.DesiredTitleId != null)
                message = $"Active, picked {TitleName(applier.DesiredTitleId.Value)}.";
            message += skipped;
            return RuleState.Active;
        }

        var winner = "the default";
        if (res.Rule != null)
            winner = $"\"{res.Rule.Name}\"";
        message = $"Matches, but {winner} takes priority.{skipped}";
        return RuleState.Outranked;
    }

    private string TitleName(ushort id)
    {
        return plugin.GameData.TitleLabel(id, Feminine);
    }

    private string TitlesPreview(TitleRule rule)
    {
        if (rule.TitleIds.Count == 0)
            return NoTitle.Label;
        if (rule.TitleIds.Count == 1)
            return TitleName(rule.TitleIds[0]);
        return $"{rule.TitleIds.Count} titles (random)";
    }

    private List<Choice> RuleTitles()
    {
        if (ruleTitles == null || ruleTitlesFeminine != Feminine)
        {
            ruleTitles = new List<Choice> { NoTitle };
            ruleTitles.AddRange(plugin.GameData.Titles(Feminine));
            ruleTitlesFeminine = Feminine;
        }
        return ruleTitles;
    }

    private static void DrawRuleIcon(RuleState state, string message)
    {
        var icon = FontAwesomeIcon.Minus;
        var color = ImGuiColors.DalamudGrey3;
        switch (state)
        {
            case RuleState.Active:
                icon = FontAwesomeIcon.Check;
                color = ImGuiColors.HealerGreen;
                break;
            case RuleState.Outranked:
                icon = FontAwesomeIcon.Check;
                color = ImGuiColors.DalamudYellow;
                break;
            case RuleState.Blocked:
                icon = FontAwesomeIcon.Times;
                color = ImGuiColors.DalamudRed;
                break;
        }

        ImGui.AlignTextToFramePadding();
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
        Tooltip(message);
    }

    // Returns whether the condition's remove button was clicked.
    private bool DrawCondition(RuleCondition cond)
    {
        var data = plugin.GameData;
        var removed = ImGuiComponents.IconButton(FontAwesomeIcon.Times);
        Tooltip("Remove condition.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##kind", KindName(cond.Kind)))
        {
            if (combo)
            {
                foreach (var kind in Kinds)
                {
                    if (!ImGui.Selectable(KindName(kind), kind == cond.Kind) || kind == cond.Kind)
                        continue;
                    // Old values are IDs for the other kind, so clear them.
                    cond.Kind = kind;
                    cond.Values.Clear();
                    dirty = true;
                }
            }
        }

        if (cond.Kind == ConditionKind.Gearset)
        {
            ImGui.SameLine();
            ImGuiComponents.HelpMarker(
                "Matches by gearset number, as shown in the Gear Set List. Names shown are the "
                    + "logged-in character's, numbers mean different gearsets on different "
                    + "characters, so these usually belong in character-specific rules."
            );
        }

        ImGui.SameLine();
        ImGui.TextDisabled("is");

        // Selected values, click one to remove it.
        uint? removeValue = null;
        foreach (var value in cond.Values)
        {
            var label = $"{data.Label(cond.Kind, value)}  x";
            WrapFor(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2);
            if (ImGui.SmallButton($"{label}##{value}"))
                removeValue = value;
            Tooltip("Click to remove.");
        }
        if (removeValue != null)
        {
            cond.Values.Remove(removeValue.Value);
            dirty = true;
        }

        var addWidth = 140 * ImGuiHelpers.GlobalScale;
        WrapFor(addWidth);
        var addLabel = cond.Values.Count == 0 ? "Select..." : "Edit...";
        if (
            SearchCombo.DrawMulti("##add", addLabel, data.Choices(cond.Kind), cond.Values, addWidth)
        )
            dirty = true;

        // Button to add whatever the current job/zone/etc. is.
        uint current = 0;
        if (plugin.Applier.Context != null)
            current = plugin.Applier.Context.Value.ValueFor(cond.Kind);
        using (ImRaii.Disabled(current == 0 || cond.Values.Contains(current)))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("+ Current"))
            {
                cond.Values.Add(current);
                dirty = true;
            }
        }
        if (current != 0)
            Tooltip(data.Label(cond.Kind, current), ImGuiHoveredFlags.AllowWhenDisabled);

        return removed;
    }

    // SameLine, unless the next item won't fit.
    private static void WrapFor(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X < width)
            ImGui.NewLine();
    }

    private static string KindName(ConditionKind kind)
    {
        if (kind == ConditionKind.DutyType)
            return "Duty type";
        return kind.ToString();
    }

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
        if (!ImGui.IsItemHovered(flags))
            return;
        using var tip = ImRaii.Tooltip();
        ImGui.TextUnformatted(text);
    }
}
