using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace AutoTitle.Core;

/// <param name="Section">Heading the picker lists this under, if any.</param>
/// <param name="Hidden">Has a label (so saved values still display) but isn't offered in the picker.</param>
public readonly record struct Choice(uint Id, string Label, string? Section = null, bool Hidden = false);

/// <summary>Picker contents and labels, built from the Excel sheets on first use.</summary>
public sealed unsafe class GameData
{
    private const uint CategoryWar = 30;
    private const uint CategoryMagic = 31;
    private const uint CategoryGatherer = 32;
    private const uint CategoryCrafter = 33;
    private const int MaxGearsets = 100;

    /// <summary>Order of the Roles section, which also orders the job sections below it.</summary>
    private static readonly JobGroup[] GroupOrder =
    [
        JobGroup.Tank, JobGroup.Healer, JobGroup.Dps, JobGroup.Melee, JobGroup.PhysicalRanged,
        JobGroup.MagicalRanged, JobGroup.DiscipleOfWar, JobGroup.DiscipleOfMagic,
        JobGroup.DiscipleOfTheHand, JobGroup.DiscipleOfTheLand,
    ];

    private readonly IDataManager data;
    private readonly Dictionary<ConditionKind, Catalog> catalogs = [];
    private Catalog? titlesMasculine;
    private Catalog? titlesFeminine;
    private Dictionary<uint, JobGroup[]>? jobGroups;
    private HashSet<uint>? sharedDuties;

    public GameData(IDataManager data) => this.data = data;

    public IReadOnlyList<Choice> Choices(ConditionKind kind) =>
        kind == ConditionKind.Gearset ? Gearsets() : CatalogFor(kind).Items;

    public string Label(ConditionKind kind, uint id)
    {
        if (kind == ConditionKind.Gearset)
            return Gearsets().FirstOrDefault(c => c.Id == id).Label ?? $"{id:00}: (not on this character)";
        return CatalogFor(kind).ById.TryGetValue(id, out var label) ? label : $"#{id}";
    }

    public IReadOnlyList<Choice> Titles(bool feminine) => TitleCatalog(feminine).Items;

    public string TitleLabel(ushort id, bool feminine) => id == 0
        ? "(No title)"
        : TitleCatalog(feminine).ById.TryGetValue(id, out var label) ? label : $"Title #{id}";

    /// <summary>Every group a job belongs to, its main role first.</summary>
    public IReadOnlyList<JobGroup> GroupsOf(uint jobId)
    {
        jobGroups ??= data.GetExcelSheet<ClassJob>().ToDictionary(j => j.RowId, ClassifyGroups);
        return jobGroups.TryGetValue(jobId, out var groups) ? groups : [];
    }

    public static string GroupName(JobGroup group) => group switch
    {
        JobGroup.Tank => "Tanks",
        JobGroup.Healer => "Healers",
        JobGroup.Melee => "Melee DPS",
        JobGroup.PhysicalRanged => "Physical Ranged DPS",
        JobGroup.MagicalRanged => "Magical Ranged DPS",
        JobGroup.Dps => "All DPS",
        JobGroup.DiscipleOfWar => "Disciples of War",
        JobGroup.DiscipleOfMagic => "Disciples of Magic",
        JobGroup.DiscipleOfTheHand => "Disciples of the Hand",
        JobGroup.DiscipleOfTheLand => "Disciples of the Land",
        _ => group.ToString(),
    };

    private static JobGroup[] ClassifyGroups(ClassJob job)
    {
        var category = job.ClassJobCategory.RowId;
        if (category == CategoryCrafter) return [JobGroup.DiscipleOfTheHand];
        if (category == CategoryGatherer) return [JobGroup.DiscipleOfTheLand];

        // ClassJob.Role is 3 for both physical and magical ranged; the category tells them apart.
        var role = job.Role switch
        {
            1 => JobGroup.Tank,
            2 => JobGroup.Melee,
            3 => category == CategoryMagic ? JobGroup.MagicalRanged : JobGroup.PhysicalRanged,
            4 => JobGroup.Healer,
            _ => JobGroup.None,
        };
        var groups = new List<JobGroup>();
        if (role != JobGroup.None) groups.Add(role);
        if (role is JobGroup.Melee or JobGroup.PhysicalRanged or JobGroup.MagicalRanged) groups.Add(JobGroup.Dps);
        if (category == CategoryWar) groups.Add(JobGroup.DiscipleOfWar);
        if (category == CategoryMagic) groups.Add(JobGroup.DiscipleOfMagic);
        return groups.ToArray();
    }

    /// <summary>Read fresh each call, since gearsets change mid-session and differ per character.</summary>
    private static List<Choice> Gearsets()
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return [];
        var list = new List<Choice>();
        for (var i = 0; i < MaxGearsets; i++)
        {
            if (!module->IsValidGearset(i)) continue;
            list.Add(new Choice((uint)i + 1, $"{i + 1:00}: {module->GetGearset(i)->NameString}"));
        }
        return list;
    }

    private Catalog TitleCatalog(bool feminine)
    {
        ref var slot = ref feminine ? ref titlesFeminine : ref titlesMasculine;
        return slot ??= Catalog.From(data.GetExcelSheet<Title>()
            .Select(t => new Choice(t.RowId, (feminine ? t.Feminine : t.Masculine).ExtractText())));
    }

    private Catalog CatalogFor(ConditionKind kind)
    {
        if (catalogs.TryGetValue(kind, out var cached)) return cached;
        var built = kind == ConditionKind.Job ? Catalog.From(BuildJobs(), sort: false) : Catalog.From(kind switch
        {
            ConditionKind.Zone => BuildZones(),
            ConditionKind.Map => BuildMaps(),
            ConditionKind.Duty => data.GetExcelSheet<ContentFinderCondition>()
                .Select(c => new Choice(c.RowId, Capitalize(c.Name.ExtractText()), Hidden: !SharedDuties().Contains(c.RowId))),
            ConditionKind.DutyType => BuildDutyTypes(),
            _ => [],
        });
        catalogs[kind] = built;
        return built;
    }

    /// <summary>
    /// Role groups, then jobs sectioned by main role in the game's own order. Combat base classes
    /// (GLA, THM, ...) are hidden; crafters and gatherers are classes too but have no job form.
    /// </summary>
    private IEnumerable<Choice> BuildJobs()
    {
        foreach (var g in GroupOrder)
            yield return new Choice(JobGroups.ToValue(g), GroupName(g), "Roles");

        var jobs = data.GetExcelSheet<ClassJob>()
            .Where(j => j.RowId != 0 && GroupsOf(j.RowId).Count > 0)
            .OrderBy(j => Array.IndexOf(GroupOrder, GroupsOf(j.RowId)[0]))
            .ThenBy(j => j.UIPriority);
        foreach (var j in jobs)
        {
            // Stored all lowercase ("blue mage").
            var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(j.Name.ExtractText());
            var isClass = j.JobIndex == 0 && j.ClassJobCategory.RowId is CategoryWar or CategoryMagic;
            yield return new Choice(j.RowId, $"{name} ({j.Abbreviation.ExtractText()})",
                GroupName(GroupsOf(j.RowId)[0]), Hidden: isClass);
        }
    }

    /// <summary>
    /// Duties some zone links to (anything else can never match, since the duty is read from the
    /// zone) that hold more than one player, so someone else is there to see the title.
    /// </summary>
    private HashSet<uint> SharedDuties()
    {
        if (sharedDuties != null) return sharedDuties;
        var duties = data.GetExcelSheet<ContentFinderCondition>();
        sharedDuties = data.GetExcelSheet<TerritoryType>()
            .Select(t => t.ContentFinderCondition.RowId)
            .Where(id => id != 0 && duties.GetRowOrDefault(id) is { } duty && MaxPlayers(duty) > 1)
            .ToHashSet();
        return sharedDuties;
    }

    private static int MaxPlayers(ContentFinderCondition duty) =>
        duty.ContentMemberType.ValueNullable is { } m ? m.MembersPerParty * m.PartyCount : 0;

    /// <summary>Only types some listed duty has; most of the sheet is journal categories like Levequests.</summary>
    private IEnumerable<Choice> BuildDutyTypes()
    {
        var duties = data.GetExcelSheet<ContentFinderCondition>();
        var used = SharedDuties().Select(id => duties.GetRow(id).ContentType.RowId).ToHashSet();
        return data.GetExcelSheet<ContentType>()
            .Select(c => new Choice(c.RowId, c.Name.ExtractText(), Hidden: !used.Contains(c.RowId)));
    }

    private IEnumerable<Choice> BuildZones()
    {
        foreach (var t in data.GetExcelSheet<TerritoryType>())
        {
            var place = t.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            if (place.Length == 0) continue;

            // Duties often share a place name, so add the duty name when it differs.
            var duty = Capitalize(t.ContentFinderCondition.ValueNullable?.Name.ExtractText() ?? "");
            var label = duty.Length > 0 && !duty.Equals(place, StringComparison.OrdinalIgnoreCase)
                ? $"{place} ({duty})"
                : place;
            yield return new Choice(t.RowId, label);
        }
    }

    private IEnumerable<Choice> BuildMaps()
    {
        foreach (var m in data.GetExcelSheet<Map>())
        {
            var place = m.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            if (place.Length == 0) continue;
            var sub = m.PlaceNameSub.ValueNullable?.Name.ExtractText() ?? "";
            yield return new Choice(m.RowId, sub.Length > 0 ? $"{place} - {sub}" : place);
        }
    }

    private static string Capitalize(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private sealed class Catalog
    {
        public required List<Choice> Items { get; init; }
        public required Dictionary<uint, string> ById { get; init; }

        /// <summary>Drops blank rows and appends the row id to labels that appear more than once.</summary>
        public static Catalog From(IEnumerable<Choice> rows, bool sort = true)
        {
            var list = rows.Where(c => !string.IsNullOrWhiteSpace(c.Label)).ToList();
            var dupes = list.GroupBy(c => c.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var items = list
                .Select(c => dupes.Contains(c.Label) ? c with { Label = $"{c.Label} #{c.Id}" } : c)
                .ToList();
            if (sort) items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));
            return new Catalog
            {
                Items = items.Where(c => !c.Hidden).ToList(),
                ById = items.ToDictionary(c => c.Id, c => c.Label),
            };
        }
    }
}
