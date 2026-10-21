using System.Numerics;
using AutoTitle.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AutoTitle.Windows;

/// <summary>
/// Combo boxes with a search filter, for lists too long to scroll through.
/// </summary>
internal static class SearchCombo
{
    private const int MaxShown = 300;
    private const float MaxPopupWidth = 520;
    private const float ListHeight = 300;

    // Only one popup can be open at a time, so they all share one filter.
    private static string filter = "";

    // Widest label for each list. Gearset lists get rebuilt every call, so this is capped.
    private static readonly Dictionary<IReadOnlyList<Choice>, float> LabelWidths = new(
        ReferenceEqualityComparer.Instance
    );

    /// <summary>Draws a combo where one item can be picked.</summary>
    /// <param name="id">The ImGui ID</param>
    /// <param name="preview">Text shown in the closed combo</param>
    /// <param name="items">The items to pick from</param>
    /// <param name="picked">The picked item's ID</param>
    /// <param name="width">The combo's width, or 0 for the default</param>
    /// <param name="pinned">Items that are always listed first, e.g. "(No title)"</param>
    /// <returns>Whether an item was picked.</returns>
    public static bool Draw(
        string id,
        string preview,
        IReadOnlyList<Choice> items,
        out uint picked,
        float width = 0,
        params Choice[] pinned
    )
    {
        picked = 0;
        using var combo = Begin(id, preview, items, pinned, width, 0);
        if (!combo)
            return false;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Search...", ref filter, 128);

        var chosen = false;
        using (ImRaii.Child("##items", new Vector2(0, ListHeight * ImGuiHelpers.GlobalScale)))
        {
            foreach (var item in pinned)
            {
                if (ImGui.Selectable($"{item.Label}##{item.Id}"))
                {
                    chosen = true;
                    picked = item.Id;
                }
            }

            var matches = FilterItems(items, out var truncated);
            foreach (var item in matches)
            {
                if (ImGui.Selectable($"{item.Label}##{item.Id}"))
                {
                    chosen = true;
                    picked = item.Id;
                }
            }
            DrawFilterNote(matches.Count == 0 && pinned.Length == 0, truncated);
        }

        if (chosen)
            ImGui.CloseCurrentPopup();
        return chosen;
    }

    /// <summary>Draws a combo with a checkbox for each item. Stays open while clicking.</summary>
    /// <param name="id">The ImGui ID</param>
    /// <param name="preview">Text shown in the closed combo</param>
    /// <param name="items">The items to pick from</param>
    /// <param name="values">The checked IDs, edited in place</param>
    /// <param name="width">The combo's width, or 0 for the default</param>
    /// <returns>Whether any values changed.</returns>
    public static bool DrawMulti(
        string id,
        string preview,
        IReadOnlyList<Choice> items,
        List<uint> values,
        float width = 0
    )
    {
        var style = ImGui.GetStyle();
        // Leave room for the checkboxes.
        var checkboxWidth = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X;
        using var combo = Begin(id, preview, items, Array.Empty<Choice>(), width, checkboxWidth);
        if (!combo)
            return false;

        var clearWidth = ImGui.CalcTextSize("Clear all").X + style.FramePadding.X * 2;
        ImGui.SetNextItemWidth(-clearWidth - style.ItemSpacing.X);
        ImGui.InputTextWithHint("##filter", "Search...", ref filter, 128);

        var changed = false;
        ImGui.SameLine();
        using (ImRaii.Disabled(values.Count == 0))
        {
            if (ImGui.Button("Clear all"))
            {
                values.Clear();
                changed = true;
            }
        }

        using (ImRaii.Child("##items", new Vector2(0, ListHeight * ImGuiHelpers.GlobalScale)))
        {
            var matches = FilterItems(items, out var truncated);
            string? section = null;
            foreach (var item in matches)
            {
                // Show a heading whenever the section changes.
                if (item.Section != null && item.Section != section)
                {
                    if (section != null)
                        ImGui.Spacing();
                    section = item.Section;
                    ImGui.TextDisabled(section);
                }

                var on = values.Contains(item.Id);
                if (!ImGui.Checkbox($"{item.Label}##{item.Id}", ref on))
                    continue;
                if (on)
                    values.Add(item.Id);
                else
                    values.Remove(item.Id);
                changed = true;
            }
            DrawFilterNote(matches.Count == 0, truncated);
        }

        return changed;
    }

    // Opens the combo, focusing and clearing the search box the first frame it's open.
    private static ImRaii.ComboDisposable Begin(
        string id,
        string preview,
        IReadOnlyList<Choice> items,
        Choice[] pinned,
        float width,
        float extra
    )
    {
        if (width > 0)
            ImGui.SetNextItemWidth(width);
        FitPopupTo(items, pinned, extra);

        var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLargest);
        if (combo.Success && ImGui.IsWindowAppearing())
        {
            filter = "";
            ImGui.SetKeyboardFocusHere();
        }
        return combo;
    }

    // Items matching the filter, capped at MaxShown so huge lists stay fast.
    private static List<Choice> FilterItems(IReadOnlyList<Choice> items, out bool truncated)
    {
        truncated = false;
        var matches = new List<Choice>();
        foreach (var item in items)
        {
            if (
                filter.Length > 0
                && !item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (matches.Count == MaxShown)
            {
                truncated = true;
                break;
            }
            matches.Add(item);
        }
        return matches;
    }

    private static void DrawFilterNote(bool empty, bool truncated)
    {
        if (truncated)
            ImGui.TextDisabled("Keep typing to narrow the list...");
        else if (empty)
            ImGui.TextDisabled("No matches");
    }

    // Combo popups are only as wide as the combo, so make room for the longest label.
    private static void FitPopupTo(IReadOnlyList<Choice> items, Choice[] pinned, float extra)
    {
        if (!LabelWidths.TryGetValue(items, out var widest))
        {
            if (LabelWidths.Count > 16)
                LabelWidths.Clear();
            widest = 0;
            foreach (var item in items)
                widest = Math.Max(widest, ImGui.CalcTextSize(item.Label).X);
            LabelWidths[items] = widest;
        }
        foreach (var item in pinned)
            widest = Math.Max(widest, ImGui.CalcTextSize(item.Label).X);

        var style = ImGui.GetStyle();
        var min =
            widest + extra + style.WindowPadding.X * 2 + style.ScrollbarSize + style.ItemSpacing.X;
        min = Math.Min(min, MaxPopupWidth * ImGuiHelpers.GlobalScale);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(min, 0),
            new Vector2(float.MaxValue, float.MaxValue)
        );
    }
}
