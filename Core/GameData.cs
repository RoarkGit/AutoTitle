using System.Globalization;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace AutoTitle.Core;

// An entry in a picker. Hidden entries still have a label for saved values but aren't listed.
public readonly record struct Choice(
    uint Id,
    string Label,
    string? Section = null,
    bool Hidden = false
);

/// <summary>
/// Builds the picker lists and labels from the game's sheets.
/// </summary>
public sealed unsafe class GameData
{
    private const uint CategoryWar = 30;
    private const uint CategoryMagic = 31;
    private const uint CategoryGatherer = 32;
    private const uint CategoryCrafter = 33;
    private const int MaxGearsets = 100;

    private static readonly JobGroup[] GroupOrder =
    [
        JobGroup.Tank,
        JobGroup.Healer,
        JobGroup.Dps,
        JobGroup.Melee,
        JobGroup.PhysicalRanged,
        JobGroup.MagicalRanged,
        JobGroup.DiscipleOfWar,
        JobGroup.DiscipleOfMagic,
        JobGroup.DiscipleOfTheHand,
        JobGroup.DiscipleOfTheLand,
    ];

    // TerritoryIntendedUse values for towns, fields, the Firmament, the Gold Saucer, Island
    // Sanctuary and Cosmic Exploration.
    private static readonly HashSet<uint> OpenZoneUses = [0, 1, 21, 23, 49, 60];
    private const uint UseHousingWard = 13;
    private const uint UseHouseInterior = 14;

    // Seasonal events (22) and the old Heavensward Diadem (23).
    private static readonly HashSet<uint> OmittedDutyTypes = [22, 23];

    // Fall Guys collab duty.
    private const uint DutyBlunderville = 958;

    // Blunderville Square and Keybound Square, which only exist during collab events.
    private static readonly HashSet<uint> OmittedZones = [1197, 1360];

    // Not a real ContentType. Unreal gets its own duty type since the fight changes every patch.
    public const uint DutyTypeUnreal = 10000;

    private readonly Dictionary<ConditionKind, Catalog> catalogs = new();

    // Maps every ID of a merged zone or duty to the one it's stored as.
    private readonly Dictionary<ConditionKind, Dictionary<uint, uint>> merged = new();
    private Catalog? titlesMasculine;
    private Catalog? titlesFeminine;
    private Dictionary<ushort, ushort[]>? titleVariants;
    private Dictionary<uint, JobGroup[]>? jobGroups;
    private HashSet<uint>? sharedDuties;
    private HashSet<uint>? unrealDuties;

    // Conditions

    /// <summary>Gets the picker entries for a condition kind.</summary>
    /// <param name="kind">The condition kind</param>
    public IReadOnlyList<Choice> Choices(ConditionKind kind)
    {
        if (kind == ConditionKind.Gearset)
            return Gearsets();
        return CatalogFor(kind).Items;
    }

    /// <summary>Gets the display name for a condition value.</summary>
    /// <param name="kind">The condition kind</param>
    /// <param name="id">The value's ID</param>
    public string Label(ConditionKind kind, uint id)
    {
        if (kind == ConditionKind.Gearset)
        {
            foreach (var gearset in Gearsets())
            {
                if (gearset.Id == id)
                    return gearset.Label;
            }
            return $"{id:00}: (not on this character)";
        }

        if (CatalogFor(kind).ById.TryGetValue(id, out var label))
            return label;
        return $"#{id}";
    }

    /// <summary>
    /// Gets the ID a zone or duty is stored as. Some entries cover several IDs, e.g. all housing
    /// wards or all Ocean Fishing routes.
    /// </summary>
    /// <param name="kind">The condition kind</param>
    /// <param name="id">The zone or duty ID</param>
    public uint Canonical(ConditionKind kind, uint id)
    {
        // Building the catalog fills in the merged IDs.
        CatalogFor(kind);
        if (merged.TryGetValue(kind, out var map) && map.TryGetValue(id, out var canonical))
            return canonical;
        return id;
    }

    // Titles

    /// <summary>Gets the title picker entries.</summary>
    /// <param name="feminine">Whether to use the feminine title names</param>
    public IReadOnlyList<Choice> Titles(bool feminine)
    {
        return TitleCatalog(feminine).Items;
    }

    /// <summary>
    /// Gets the ID a title is stored as. Some titles exist multiple times with the same name (one
    /// per PvP season, for example), so we always use the lowest ID.
    /// </summary>
    /// <param name="id">The title's ID</param>
    public ushort CanonicalTitle(ushort id)
    {
        return TitleVariants(id)[0];
    }

    /// <summary>Gets all IDs that share a title's name, lowest first.</summary>
    /// <param name="id">The title's ID</param>
    public IReadOnlyList<ushort> TitleVariants(ushort id)
    {
        if (titleVariants == null)
        {
            // Group title IDs by their masculine and feminine names.
            var byName = new Dictionary<(string, string), List<ushort>>();
            foreach (var title in Plugin.DataManager.GetExcelSheet<Title>())
            {
                if (title.Masculine.IsEmpty)
                    continue;
                var name = (title.Masculine.ExtractText(), title.Feminine.ExtractText());
                if (!byName.TryGetValue(name, out var ids))
                {
                    ids = new List<ushort>();
                    byName[name] = ids;
                }
                ids.Add((ushort)title.RowId);
            }

            // Only names with more than one ID need an entry.
            titleVariants = new Dictionary<ushort, ushort[]>();
            foreach (var ids in byName.Values)
            {
                if (ids.Count < 2)
                    continue;
                ids.Sort();
                var variants = ids.ToArray();
                foreach (var variant in variants)
                    titleVariants[variant] = variants;
            }
        }

        if (titleVariants.TryGetValue(id, out var found))
            return found;
        return new[] { id };
    }

    /// <summary>Gets a title's display name.</summary>
    /// <param name="id">The title's ID</param>
    /// <param name="feminine">Whether to use the feminine title name</param>
    public string TitleLabel(ushort id, bool feminine)
    {
        if (id == 0)
            return "(No title)";
        if (TitleCatalog(feminine).ById.TryGetValue(id, out var label))
            return label;
        return $"Title #{id}";
    }

    // Jobs

    /// <summary>Gets every group a job belongs to, with its main role first.</summary>
    /// <param name="jobId">The ClassJob ID</param>
    public IReadOnlyList<JobGroup> GroupsOf(uint jobId)
    {
        if (jobGroups == null)
        {
            jobGroups = new Dictionary<uint, JobGroup[]>();
            foreach (var job in Plugin.DataManager.GetExcelSheet<ClassJob>())
                jobGroups[job.RowId] = ClassifyGroups(job);
        }

        if (jobGroups.TryGetValue(jobId, out var groups))
            return groups;
        return Array.Empty<JobGroup>();
    }

    public static string GroupName(JobGroup group)
    {
        switch (group)
        {
            case JobGroup.Tank:
                return "Tanks";
            case JobGroup.Healer:
                return "Healers";
            case JobGroup.Melee:
                return "Melee DPS";
            case JobGroup.PhysicalRanged:
                return "Physical Ranged DPS";
            case JobGroup.MagicalRanged:
                return "Magical Ranged DPS";
            case JobGroup.Dps:
                return "All DPS";
            case JobGroup.DiscipleOfWar:
                return "Disciples of War";
            case JobGroup.DiscipleOfMagic:
                return "Disciples of Magic";
            case JobGroup.DiscipleOfTheHand:
                return "Disciples of the Hand";
            case JobGroup.DiscipleOfTheLand:
                return "Disciples of the Land";
            default:
                return group.ToString();
        }
    }

    private static JobGroup[] ClassifyGroups(ClassJob job)
    {
        var category = job.ClassJobCategory.RowId;
        if (category == CategoryCrafter)
            return new[] { JobGroup.DiscipleOfTheHand };
        if (category == CategoryGatherer)
            return new[] { JobGroup.DiscipleOfTheLand };

        var groups = new List<JobGroup>();
        switch (job.Role)
        {
            case 1:
                groups.Add(JobGroup.Tank);
                break;
            case 2:
                groups.Add(JobGroup.Melee);
                groups.Add(JobGroup.Dps);
                break;
            case 3:
                // Role is 3 for both physical and magical ranged, so check the category too.
                if (category == CategoryMagic)
                    groups.Add(JobGroup.MagicalRanged);
                else
                    groups.Add(JobGroup.PhysicalRanged);
                groups.Add(JobGroup.Dps);
                break;
            case 4:
                groups.Add(JobGroup.Healer);
                break;
        }

        if (category == CategoryWar)
            groups.Add(JobGroup.DiscipleOfWar);
        if (category == CategoryMagic)
            groups.Add(JobGroup.DiscipleOfMagic);
        return groups.ToArray();
    }

    // Not cached since gearsets can change mid-session.
    private static List<Choice> Gearsets()
    {
        var list = new List<Choice>();
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return list;

        for (var i = 0; i < MaxGearsets; i++)
        {
            if (!module->IsValidGearset(i))
                continue;
            var name = module->GetGearset(i)->NameString;
            list.Add(new Choice((uint)i + 1, $"{i + 1:00}: {name}"));
        }
        return list;
    }

    // Building lists

    private Catalog TitleCatalog(bool feminine)
    {
        if (feminine && titlesFeminine != null)
            return titlesFeminine;
        if (!feminine && titlesMasculine != null)
            return titlesMasculine;

        var rows = new List<Choice>();
        foreach (var title in Plugin.DataManager.GetExcelSheet<Title>())
        {
            var name = feminine ? title.Feminine.ExtractText() : title.Masculine.ExtractText();
            // Duplicate titles are only listed once, under their lowest ID.
            var hidden = CanonicalTitle((ushort)title.RowId) != title.RowId;
            rows.Add(new Choice(title.RowId, name, Hidden: hidden));
        }

        var catalog = Catalog.From(rows, true);
        if (feminine)
            titlesFeminine = catalog;
        else
            titlesMasculine = catalog;
        return catalog;
    }

    private Catalog CatalogFor(ConditionKind kind)
    {
        if (catalogs.TryGetValue(kind, out var cached))
            return cached;

        Catalog catalog;
        switch (kind)
        {
            case ConditionKind.Job:
                // Jobs are already in the order we want.
                catalog = Catalog.From(BuildJobs(), false);
                break;
            case ConditionKind.Zone:
                catalog = Catalog.From(BuildZones(), true);
                break;
            case ConditionKind.Duty:
                catalog = Catalog.From(BuildDuties(), true);
                break;
            case ConditionKind.DutyType:
                catalog = Catalog.From(BuildDutyTypes(), true);
                break;
            default:
                catalog = Catalog.From(new List<Choice>(), true);
                break;
        }

        catalogs[kind] = catalog;
        return catalog;
    }

    // Roles first, then jobs grouped by role. Base classes (GLA, THM, etc.) are hidden.
    private List<Choice> BuildJobs()
    {
        var list = new List<Choice>();
        foreach (var group in GroupOrder)
            list.Add(new Choice(JobGroups.ToValue(group), GroupName(group), "Roles"));

        var jobs = new List<ClassJob>();
        foreach (var job in Plugin.DataManager.GetExcelSheet<ClassJob>())
        {
            if (job.RowId != 0 && GroupsOf(job.RowId).Count > 0)
                jobs.Add(job);
        }

        // Sort by main role, then the game's own order within each role.
        jobs.Sort(
            (a, b) =>
            {
                var byRole = RoleOrder(a).CompareTo(RoleOrder(b));
                if (byRole != 0)
                    return byRole;
                return a.UIPriority.CompareTo(b.UIPriority);
            }
        );

        foreach (var job in jobs)
        {
            // Names are lowercase in the sheet ("blue mage").
            var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(job.Name.ExtractText());
            var abbreviation = job.Abbreviation.ExtractText();
            var category = job.ClassJobCategory.RowId;
            var isClass =
                job.JobIndex == 0 && (category == CategoryWar || category == CategoryMagic);
            var section = GroupName(GroupsOf(job.RowId)[0]);
            list.Add(new Choice(job.RowId, $"{name} ({abbreviation})", section, isClass));
        }
        return list;
    }

    private int RoleOrder(ClassJob job)
    {
        return Array.IndexOf(GroupOrder, GroupsOf(job.RowId)[0]);
    }

    // Duties with their own zone and more than one player, i.e. anywhere someone else can see
    // your title.
    private HashSet<uint> SharedDuties()
    {
        if (sharedDuties != null)
            return sharedDuties;

        sharedDuties = new HashSet<uint>();
        var duties = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        foreach (var zone in Plugin.DataManager.GetExcelSheet<TerritoryType>())
        {
            var id = zone.ContentFinderCondition.RowId;
            if (id == 0 || id == DutyBlunderville)
                continue;

            var duty = duties.GetRowOrDefault(id);
            if (duty == null || duty.Value.Name.IsEmpty)
                continue;
            if (MaxPlayers(duty.Value) < 2)
                continue;
            if (OmittedDutyTypes.Contains(duty.Value.ContentType.RowId))
                continue;
            sharedDuties.Add(id);
        }
        return sharedDuties;
    }

    // Same-named duties (Leap of Faith courses, Ocean Fishing routes, etc.) are merged into one
    // entry.
    private List<Choice> BuildDuties()
    {
        var list = new List<Choice>();
        var map = new Dictionary<uint, uint>();
        merged[ConditionKind.Duty] = map;

        var shared = SharedDuties();
        var firstByName = new Dictionary<string, uint>();
        foreach (var duty in Plugin.DataManager.GetExcelSheet<ContentFinderCondition>())
        {
            var name = Capitalize(duty.Name.ExtractText());
            if (!shared.Contains(duty.RowId) || IsUnreal(duty.RowId))
            {
                list.Add(new Choice(duty.RowId, name, Hidden: true));
                continue;
            }

            if (firstByName.TryGetValue(name, out var first))
            {
                map[duty.RowId] = first;
                continue;
            }
            firstByName[name] = duty.RowId;
            list.Add(new Choice(duty.RowId, name));
        }
        return list;
    }

    /// <summary>Gets a duty's type, or <see cref="DutyTypeUnreal"/> for the Unreal.</summary>
    /// <param name="duty">The duty</param>
    public uint DutyTypeOf(ContentFinderCondition duty)
    {
        if (IsUnreal(duty.RowId))
            return DutyTypeUnreal;
        return duty.ContentType.RowId;
    }

    // Nothing in the sheet marks a duty as Unreal, so check the English name instead.
    private bool IsUnreal(uint dutyId)
    {
        if (unrealDuties == null)
        {
            unrealDuties = new HashSet<uint>();
            var duties = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(
                ClientLanguage.English
            );
            foreach (var duty in duties)
            {
                if (duty.Name.ExtractText().EndsWith("(Unreal)", StringComparison.Ordinal))
                    unrealDuties.Add(duty.RowId);
            }
        }
        return unrealDuties.Contains(dutyId);
    }

    private static int MaxPlayers(ContentFinderCondition duty)
    {
        var members = duty.ContentMemberType.ValueNullable;
        if (members == null)
            return 0;
        return members.Value.MembersPerParty * members.Value.PartyCount;
    }

    // Only list types that are used by at least one listed duty.
    private List<Choice> BuildDutyTypes()
    {
        var duties = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        var used = new HashSet<uint>();
        foreach (var id in SharedDuties())
            used.Add(DutyTypeOf(duties.GetRow(id)));

        var list = new List<Choice>();
        foreach (var type in Plugin.DataManager.GetExcelSheet<ContentType>())
            list.Add(
                new Choice(type.RowId, type.Name.ExtractText(), Hidden: !used.Contains(type.RowId))
            );
        list.Add(
            new Choice(DutyTypeUnreal, "Unreal Trials", Hidden: !used.Contains(DutyTypeUnreal))
        );
        return list;
    }

    // Only lists open areas where other players can see you. All housing wards are one entry, and
    // so are all house interiors.
    private List<Choice> BuildZones()
    {
        var list = new List<Choice>();
        var map = new Dictionary<uint, uint>();
        merged[ConditionKind.Zone] = map;

        var zones = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        // Place name and level files of every area listed so far, to skip old copies.
        var listed = new HashSet<(string, string)>();

        AddHousing(list, map, listed, zones, UseHousingWard, "Housing ward");
        AddHousing(list, map, listed, zones, UseHouseInterior, "Inside a house");

        // Go newest first so old copies of an area are the ones skipped.
        foreach (var zone in zones.Reverse())
        {
            var place = PlaceOf(zone);
            if (place.Length == 0 || map.ContainsKey(zone.RowId))
                continue;

            // Add the duty name if it's different from the place name.
            var label = place;
            var duty = Capitalize(
                zone.ContentFinderCondition.ValueNullable?.Name.ExtractText() ?? ""
            );
            if (duty.Length > 0 && !duty.Equals(place, StringComparison.OrdinalIgnoreCase))
                label = $"{place} ({duty})";

            var open =
                zone.ContentFinderCondition.RowId == 0
                && OpenZoneUses.Contains(zone.TerritoryIntendedUse.RowId)
                && !OmittedZones.Contains(zone.RowId);
            // Skip old copies of the same area (e.g. Mist).
            if (open && !listed.Add((place, zone.Bg.ExtractText())))
                open = false;

            list.Add(new Choice(zone.RowId, label, Hidden: !open));
        }
        return list;
    }

    // Adds one entry covering every zone with the given intended use.
    private static void AddHousing(
        List<Choice> list,
        Dictionary<uint, uint> map,
        HashSet<(string, string)> listed,
        IEnumerable<TerritoryType> zones,
        uint use,
        string label
    )
    {
        uint? first = null;
        foreach (var zone in zones)
        {
            if (zone.TerritoryIntendedUse.RowId != use)
                continue;
            first ??= zone.RowId;
            map[zone.RowId] = first.Value;
            listed.Add((PlaceOf(zone), zone.Bg.ExtractText()));
        }

        if (first != null)
            list.Add(new Choice(first.Value, label));
    }

    private static string PlaceOf(TerritoryType zone)
    {
        var place = zone.PlaceName.ValueNullable;
        if (place == null)
            return "";
        return place.Value.Name.ExtractText();
    }

    private static string Capitalize(string s)
    {
        if (s.Length == 0 || !char.IsLower(s[0]))
            return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    private sealed class Catalog
    {
        public List<Choice> Items = new();
        public Dictionary<uint, string> ById = new();

        // Drops blank rows and adds the ID to duplicate labels.
        public static Catalog From(List<Choice> rows, bool sort)
        {
            var visibleCounts = new Dictionary<string, int>();
            foreach (var row in rows)
            {
                if (row.Hidden || string.IsNullOrWhiteSpace(row.Label))
                    continue;
                visibleCounts.TryGetValue(row.Label, out var count);
                visibleCounts[row.Label] = count + 1;
            }

            var items = new List<Choice>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Label))
                    continue;
                if (visibleCounts.TryGetValue(row.Label, out var count) && count > 1)
                    items.Add(row with { Label = $"{row.Label} #{row.Id}" });
                else
                    items.Add(row);
            }

            if (sort)
                items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));

            var catalog = new Catalog();
            foreach (var item in items)
            {
                catalog.ById[item.Id] = item.Label;
                if (!item.Hidden)
                    catalog.Items.Add(item);
            }
            return catalog;
        }
    }
}
