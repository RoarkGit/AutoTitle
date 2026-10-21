using System.Globalization;
using Dalamud.Game;
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

    /// <summary>
    /// TerritoryIntendedUse values for open areas: cities, field zones, the Firmament, the Gold Saucer,
    /// Island Sanctuary, and Cosmic Exploration. Housing is handled separately.
    /// </summary>
    private static readonly HashSet<uint> OpenZoneUses = [0, 1, 21, 23, 49, 60];
    /// <summary>
    /// Duty types left out of the duty lists: seasonal event duties (22), which only exist while the
    /// event runs, and the Heavensward Diadem (23), which is no longer in the game.
    /// </summary>
    private static readonly HashSet<uint> OmittedDutyTypes = [22, 23];
    // Blunderville, the Fall Guys collaboration, which only runs during the event.
    private const uint DutyBlunderville = 958;
    /// <summary>Gold Saucer areas that only exist during a collaboration: Blunderville Square and Keybound Square.</summary>
    private static readonly HashSet<uint> OmittedZones = [1197, 1360];
    /// <summary>
    /// Not a real ContentType row. The Unreal trial is one duty that becomes a different fight every
    /// patch, so it's offered as a type of its own rather than by name.
    /// </summary>
    public const uint DutyTypeUnreal = 10000;
    private const uint UseHousingWard = 13;
    private const uint UseHouseInterior = 14;

    private readonly IDataManager data;
    private readonly Dictionary<ConditionKind, Catalog> catalogs = [];
    private readonly Dictionary<ConditionKind, Dictionary<uint, uint>> merged = [];
    private Catalog? titlesMasculine;
    private Catalog? titlesFeminine;
    private Dictionary<ushort, ushort[]>? titleVariants;
    private Dictionary<uint, JobGroup[]>? jobGroups;
    private HashSet<uint>? sharedDuties;
    private HashSet<uint>? unrealDuties;

    public GameData(IDataManager data) => this.data = data;

    public IReadOnlyList<Choice> Choices(ConditionKind kind) =>
        kind == ConditionKind.Gearset ? Gearsets() : CatalogFor(kind).Items;

    public string Label(ConditionKind kind, uint id)
    {
        if (kind == ConditionKind.Gearset)
            return Gearsets().FirstOrDefault(c => c.Id == id).Label ?? $"{id:00}: (not on this character)";
        return CatalogFor(kind).ById.TryGetValue(id, out var label) ? label : $"#{id}";
    }

    /// <summary>
    /// The id a zone or duty is stored and matched as. Some places are offered as one entry but
    /// are several in the game: every housing ward, every house interior, and duties that share a
    /// name (the Ocean Fishing routes, the Diadem, the Leap of Faith courses).
    /// </summary>
    public uint Canonical(ConditionKind kind, uint id)
    {
        CatalogFor(kind);
        return merged.TryGetValue(kind, out var map) && map.TryGetValue(id, out var canonical) ? canonical : id;
    }

    public IReadOnlyList<Choice> Titles(bool feminine) => TitleCatalog(feminine).Items;

    /// <summary>
    /// The id a title is stored and matched as. Some titles exist several times under the same
    /// name (one per PvP season, for example), so those are offered once, as their lowest id.
    /// </summary>
    public ushort CanonicalTitle(ushort id) => TitleVariants(id)[0];

    /// <summary>Every id sharing this title's name, lowest first; just the id itself for most titles.</summary>
    public IReadOnlyList<ushort> TitleVariants(ushort id)
    {
        titleVariants ??= data.GetExcelSheet<Title>()
            .Where(t => !t.Masculine.IsEmpty)
            .GroupBy(t => (t.Masculine.ExtractText(), t.Feminine.ExtractText()))
            .Where(g => g.Count() > 1)
            .SelectMany(g =>
            {
                var ids = g.Select(t => (ushort)t.RowId).Order().ToArray();
                return ids.Select(i => (i, ids));
            })
            .ToDictionary(x => x.i, x => x.ids);
        return titleVariants.TryGetValue(id, out var ids) ? ids : [id];
    }

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
            .Select(t => new Choice(t.RowId, (feminine ? t.Feminine : t.Masculine).ExtractText(),
                Hidden: CanonicalTitle((ushort)t.RowId) != t.RowId)));
    }

    private Catalog CatalogFor(ConditionKind kind)
    {
        if (catalogs.TryGetValue(kind, out var cached)) return cached;
        var built = kind == ConditionKind.Job ? Catalog.From(BuildJobs(), sort: false) : Catalog.From(kind switch
        {
            ConditionKind.Zone => BuildZones(),
            ConditionKind.Duty => BuildDuties(),
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
            .Where(id => id != 0 && duties.GetRowOrDefault(id) is { } duty && MaxPlayers(duty) > 1
                && !duty.Name.IsEmpty && !OmittedDutyTypes.Contains(duty.ContentType.RowId) && id != DutyBlunderville)
            .ToHashSet();
        return sharedDuties;
    }

    /// <summary>Shared duties, with same-named ones merged into the lowest id.</summary>
    private IEnumerable<Choice> BuildDuties()
    {
        var map = merged[ConditionKind.Duty] = [];
        var shared = SharedDuties();
        var canonicalByName = new Dictionary<string, uint>();
        foreach (var c in data.GetExcelSheet<ContentFinderCondition>())
        {
            var name = Capitalize(c.Name.ExtractText());
            if (!shared.Contains(c.RowId) || IsUnreal(c.RowId))
            {
                yield return new Choice(c.RowId, name, Hidden: true);
                continue;
            }
            if (!canonicalByName.TryAdd(name, c.RowId))
            {
                map[c.RowId] = canonicalByName[name];
                continue;
            }
            yield return new Choice(c.RowId, name);
        }
    }

    /// <summary>The duty's ContentType, or <see cref="DutyTypeUnreal"/> for the Unreal trial.</summary>
    public uint DutyTypeOf(ContentFinderCondition duty) =>
        IsUnreal(duty.RowId) ? DutyTypeUnreal : duty.ContentType.RowId;

    /// <summary>Nothing in the sheet marks the Unreal, so this goes by the English name in any client language.</summary>
    private bool IsUnreal(uint dutyId)
    {
        unrealDuties ??= data.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English)
            .Where(c => c.Name.ExtractText().EndsWith("(Unreal)", StringComparison.Ordinal))
            .Select(c => c.RowId)
            .ToHashSet();
        return unrealDuties.Contains(dutyId);
    }

    private static int MaxPlayers(ContentFinderCondition duty) =>
        duty.ContentMemberType.ValueNullable is { } m ? m.MembersPerParty * m.PartyCount : 0;

    /// <summary>Only types some listed duty has; most of the sheet is journal categories like Levequests.</summary>
    private IEnumerable<Choice> BuildDutyTypes()
    {
        var duties = data.GetExcelSheet<ContentFinderCondition>();
        var used = SharedDuties().Select(id => DutyTypeOf(duties.GetRow(id))).ToHashSet();
        return data.GetExcelSheet<ContentType>()
            .Select(c => new Choice(c.RowId, c.Name.ExtractText(), Hidden: !used.Contains(c.RowId)))
            .Append(new Choice(DutyTypeUnreal, "Unreal Trials", Hidden: !used.Contains(DutyTypeUnreal)));
    }

    /// <summary>
    /// Only open areas where other players can see you are listed. Duties have their own condition,
    /// and the rest are story instances, solo quest battles, inn rooms, and copies used in cutscenes.
    /// Hidden zones keep their labels so existing rules still read correctly. All housing wards
    /// are one entry, and so are all house, apartment, and company interiors.
    /// </summary>
    private IEnumerable<Choice> BuildZones()
    {
        var map = merged[ConditionKind.Zone] = [];
        var zones = data.GetExcelSheet<TerritoryType>();
        var listed = new HashSet<(string, string)>();
        foreach (var (use, label) in new[] { (UseHousingWard, "Housing ward"), (UseHouseInterior, "Inside a house") })
        {
            var wards = zones.Where(t => t.TerritoryIntendedUse.RowId == use).ToList();
            if (wards.Count == 0) continue;
            var ids = wards.Select(t => t.RowId).ToList();
            foreach (var t in wards)
            {
                map[t.RowId] = ids[0];
                listed.Add((PlaceOf(t), t.Bg.ExtractText()));
            }
            yield return new Choice(ids[0], label);
        }

        foreach (var t in zones.Reverse())
        {
            var place = PlaceOf(t);
            if (place.Length == 0 || map.ContainsKey(t.RowId)) continue;

            // Duties often share a place name, so add the duty name when it differs.
            var duty = Capitalize(t.ContentFinderCondition.ValueNullable?.Name.ExtractText() ?? "");
            var label = duty.Length > 0 && !duty.Equals(place, StringComparison.OrdinalIgnoreCase)
                ? $"{place} ({duty})"
                : place;

            // Some areas have an older copy with the same name and level files (e.g. Mist); list the newest.
            var open = t.ContentFinderCondition.RowId == 0 && OpenZoneUses.Contains(t.TerritoryIntendedUse.RowId)
                && !OmittedZones.Contains(t.RowId) && listed.Add((place, t.Bg.ExtractText()));
            yield return new Choice(t.RowId, label, Hidden: !open);
        }
    }

    private static string PlaceOf(TerritoryType t) => t.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

    private static string Capitalize(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private sealed class Catalog
    {
        public required List<Choice> Items { get; init; }
        public required Dictionary<uint, string> ById { get; init; }

        /// <summary>Drops blank rows and appends the row id to labels listed more than once.</summary>
        public static Catalog From(IEnumerable<Choice> rows, bool sort = true)
        {
            var list = rows.Where(c => !string.IsNullOrWhiteSpace(c.Label)).ToList();
            var dupes = list.Where(c => !c.Hidden).GroupBy(c => c.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
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
