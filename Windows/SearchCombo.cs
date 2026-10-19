using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using AutoTitle.Core;

namespace AutoTitle.Windows;

/// <summary>Combos with a search box, for lists too long to scroll (titles, zones).</summary>
internal static class SearchCombo
{
    private const int MaxShown = 300;
    private const float MaxPopupWidth = 520;
    private const float ListHeight = 300;

    // Only one combo popup can be open at a time, so they share one filter.
    private static string filter = "";

    // Widest label per list. Catalog lists live for the session, so each is measured once; gearset
    // lists are rebuilt every call, hence the size cap.
    private static readonly Dictionary<IReadOnlyList<Choice>, float> LabelWidths =
        new(ReferenceEqualityComparer.Instance);

    /// <param name="pinned">Always listed first and never filtered out, e.g. "(No title)".</param>
    public static bool Draw(string id, string preview, IReadOnlyList<Choice> items, out uint picked,
        float width = 0, params Choice[] pinned)
    {
        picked = 0;
        using var combo = Begin(id, preview, items, pinned, width, 0);
        if (!combo) return false;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Search...", ref filter, 128);

        var chosen = false;
        using (ImRaii.Child("##items", new Vector2(0, ListHeight * ImGuiHelpers.GlobalScale)))
        {
            foreach (var c in pinned.Concat(Filtered(items, pinned.Length > 0)))
            {
                if (!ImGui.Selectable($"{c.Label}##{c.Id}")) continue;
                (chosen, picked) = (true, c.Id);
            }
        }

        if (chosen) ImGui.CloseCurrentPopup();
        return chosen;
    }

    /// <summary>Checkbox list that edits <paramref name="values"/> in place and stays open.</summary>
    /// <returns>Whether <paramref name="values"/> changed.</returns>
    public static bool DrawMulti(string id, string preview, IReadOnlyList<Choice> items, List<uint> values,
        float width = 0)
    {
        var style = ImGui.GetStyle();
        using var combo = Begin(id, preview, items, [], width, ImGui.GetFrameHeight() + style.ItemInnerSpacing.X);
        if (!combo) return false;

        ImGui.SetNextItemWidth(-ImGui.CalcTextSize("Clear all").X - style.FramePadding.X * 2 - style.ItemSpacing.X);
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
            string? section = null;
            foreach (var c in Filtered(items, false))
            {
                if (c.Section != null && c.Section != section)
                {
                    if (section != null) ImGui.Spacing();
                    section = c.Section;
                    ImGui.TextDisabled(section);
                }

                var on = values.Contains(c.Id);
                if (!ImGui.Checkbox($"{c.Label}##{c.Id}", ref on)) continue;
                if (on) values.Add(c.Id);
                else values.Remove(c.Id);
                changed = true;
            }
        }

        return changed;
    }

    private static ImRaii.ComboDisposable Begin(string id, string preview, IReadOnlyList<Choice> items, Choice[] pinned,
        float width, float extra)
    {
        if (width > 0) ImGui.SetNextItemWidth(width);
        FitPopupTo(items, pinned, extra);

        var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLargest);
        bool open = combo;
        if (open && ImGui.IsWindowAppearing())
        {
            filter = "";
            ImGui.SetKeyboardFocusHere();
        }
        return combo;
    }

    /// <summary>Items matching the filter, capped at <see cref="MaxShown"/>, with a note when the cap or no match is hit.</summary>
    private static IEnumerable<Choice> Filtered(IReadOnlyList<Choice> items, bool hasPinned)
    {
        var shown = 0;
        foreach (var c in items)
        {
            if (filter.Length > 0 && !c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (shown++ == MaxShown)
            {
                ImGui.TextDisabled("Keep typing to narrow the list...");
                yield break;
            }
            yield return c;
        }
        if (shown == 0 && !hasPinned) ImGui.TextDisabled("No matches");
    }

    /// <summary>
    /// A combo popup is only as wide as the combo itself, and the scrolling child inside doesn't
    /// widen it, so set a minimum width that fits the longest label.
    /// </summary>
    private static void FitPopupTo(IReadOnlyList<Choice> items, Choice[] pinned, float extra)
    {
        if (!LabelWidths.TryGetValue(items, out var widest))
        {
            if (LabelWidths.Count > 16) LabelWidths.Clear();
            widest = items.Count == 0 ? 0 : items.Max(c => ImGui.CalcTextSize(c.Label).X);
            LabelWidths[items] = widest;
        }
        foreach (var c in pinned) widest = Math.Max(widest, ImGui.CalcTextSize(c.Label).X);

        var style = ImGui.GetStyle();
        var min = widest + extra + style.WindowPadding.X * 2 + style.ScrollbarSize + style.ItemSpacing.X;
        min = Math.Min(min, MaxPopupWidth * ImGuiHelpers.GlobalScale);
        ImGui.SetNextWindowSizeConstraints(new Vector2(min, 0), new Vector2(float.MaxValue, float.MaxValue));
    }
}
