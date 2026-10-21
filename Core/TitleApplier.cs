using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace AutoTitle.Core;

/// <summary>
/// Re-checks the rules when the game reports a change (job, zone, equipment, zoning, login)
/// and sends the title they call for. A title is only sent when the resolved title changes or on
/// login, so a title picked by hand sticks until something relevant changes.
/// </summary>
public sealed unsafe class TitleApplier : IDisposable
{
    // Minimum time between two sends, whatever triggered them.
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);
    // How long the server gets to apply a title before we report that it didn't. Never resent.
    private static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(3);
    // There's no event for the character finishing setup after a load, so that's re-checked each
    // frame, giving up after this long.
    private static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(10);

    private readonly Configuration config;
    private readonly GameData gameData;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IGameInventory inventory;
    private readonly IDataManager dataManager;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private readonly CancellationTokenSource disposed = new();
    private CancellationTokenSource? scheduled;
    private DateTime lastSend = DateTime.MinValue;
    private bool force = true;
    private ushort? lastDesired;
    private DateTime? waitingSince;
    private CharacterProfile? profile;
    private bool waitingForTitleList;
    // Where the current pick came from (a rule, or the source of a default), and whether the next
    // evaluation should pick again even if that hasn't changed.
    private object? pickedFrom;
    private bool repick = true;

    /// <summary>Null while logged out.</summary>
    public GameContext? Context { get; private set; }

    /// <summary>Content id of the logged-in character, 0 while logged out.</summary>
    public ulong CharacterId { get; private set; }

    public Resolution Resolution { get; private set; }

    /// <summary>The title picked from <see cref="Resolution"/>, or null to leave the title alone.</summary>
    public ushort? DesiredTitleId { get; private set; }
    public ushort? CurrentTitleId { get; private set; }
    public string Status { get; private set; } = "Waiting for login";

    public TitleApplier(Configuration config, GameData gameData, IFramework framework, IClientState clientState,
        IObjectTable objects, IPlayerState playerState, ICondition condition, IGameInventory inventory,
        IDataManager dataManager, IChatGui chat, IPluginLog log)
    {
        this.config = config;
        this.gameData = gameData;
        this.framework = framework;
        this.clientState = clientState;
        this.objects = objects;
        this.playerState = playerState;
        this.condition = condition;
        this.inventory = inventory;
        this.dataManager = dataManager;
        this.chat = chat;
        this.log = log;

        clientState.Login += Reapply;
        clientState.Logout += OnLogout;
        clientState.ClassJobChanged += OnClassJobChanged;
        clientState.TerritoryChanged += OnTerritoryChanged;
        condition.ConditionChange += OnConditionChange;
        inventory.InventoryChangedRaw += OnInventoryChanged;

        if (clientState.IsLoggedIn) Reapply();
    }

    public void Dispose()
    {
        clientState.Login -= Reapply;
        clientState.Logout -= OnLogout;
        clientState.ClassJobChanged -= OnClassJobChanged;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        condition.ConditionChange -= OnConditionChange;
        inventory.InventoryChangedRaw -= OnInventoryChanged;
        disposed.Cancel();
        scheduled?.Cancel();
    }

    /// <summary>
    /// Picks again from rules with several titles and sends the result right away (subject to the
    /// cooldown) even if it hasn't changed.
    /// </summary>
    public void Reapply() => framework.RunOnTick(() =>
    {
        force = true;
        repick = true;
        waitingSince = null;
        Evaluate();
        Schedule(Apply, TimeSpan.Zero);
    }, cancellationToken: disposed.Token);

    /// <summary>Re-checks the rules after a change.</summary>
    public void Refresh() => framework.RunOnTick(() =>
    {
        waitingSince = null;
        Evaluate();
        Schedule(Apply, TimeSpan.Zero);
    }, cancellationToken: disposed.Token);

    /// <summary>Null if this character's unlocked titles have never been loaded.</summary>
    public bool? IsUnlocked(ushort id) => id == 0 ? true : UnlockedVariant(id) is { } v ? v != 0 : null;

    /// <summary>
    /// Asks the server for the unlocked title list if the game doesn't have it yet. Only called
    /// when the settings window opens, so the request always follows something the player did.
    /// </summary>
    public void LoadTitleList()
    {
        var ui = UIState.Instance();
        if (!clientState.IsLoggedIn || ui == null) return;
        if (ui->TitleList.DataReceived || ui->TitleList.DataPending || ui->TitleList.DataRequested) return;
        ui->TitleList.RequestTitleList();
        waitingForTitleList = true;
    }

    /// <summary>Called each frame the settings window is open, to apply the title once the list arrives.</summary>
    public void CheckTitleList()
    {
        if (!waitingForTitleList) return;
        var ui = UIState.Instance();
        if (ui == null || !ui->TitleList.DataReceived) return;
        waitingForTitleList = false;
        Refresh();
    }

    private void OnClassJobChanged(uint jobId) => Refresh();
    private void OnTerritoryChanged(uint territoryId) => Refresh();

    // Titles aren't sent while zoning or in a cutscene, so check again once that ends.
    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!value && IsBusyFlag(flag)) Refresh();
    }

    // There's no gearset event, but switching gearsets changes the equipped items.
    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (events.Any(e => e.Item.ContainerType == GameInventoryType.EquippedItems)) Refresh();
    }

    private void OnLogout(int type, int code)
    {
        scheduled?.Cancel();
        Context = null;
        CharacterId = 0;
        CurrentTitleId = null;
        lastDesired = null;
        profile = null;
        waitingForTitleList = false;
        DesiredTitleId = null;
        pickedFrom = null;
        repick = true;
        force = true;
        Status = "Waiting for login";
    }

    /// <summary>Runs <paramref name="action"/> after <paramref name="delay"/>, replacing anything already scheduled.</summary>
    private void Schedule(Action action, TimeSpan delay, int delayTicks = 0)
    {
        scheduled?.Cancel();
        scheduled = CancellationTokenSource.CreateLinkedTokenSource(disposed.Token);
        framework.RunOnTick(action, delay, delayTicks, scheduled.Token);
    }

    /// <summary>Runs <see cref="Apply"/> again next frame, unless we've been waiting longer than <see cref="LoadingTimeout"/>.</summary>
    private bool RetryNextFrame()
    {
        waitingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - waitingSince > LoadingTimeout)
        {
            waitingSince = null;
            return false;
        }
        Schedule(Apply, TimeSpan.Zero, delayTicks: 1);
        return true;
    }

    /// <summary>Reads the game state and resolves the rules. False while the character isn't loaded.</summary>
    private bool Evaluate()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || objects.LocalPlayer is not { } player) return false;

        profile = RecordCharacter();
        SaveTitleList(profile);
        CurrentTitleId = gameData.CanonicalTitle(((Character*)player.Address)->TitleId);
        Context = Snapshot(player.ClassJob.RowId, player.Customize[(int)CustomizeIndex.Gender] == 1);
        Resolution = RuleEngine.Resolve(config, profile, Context.Value);
        DesiredTitleId = PickTitle();
        return true;
    }

    /// <summary>
    /// Picks one of the resolved titles at random, skipping locked ones. The pick is kept while the
    /// same rule stays in effect, so it only changes when the rule takes over again.
    /// </summary>
    private ushort? PickTitle()
    {
        if (Resolution.Titles is not { } titles) return null;
        var from = (object?)Resolution.Rule ?? Resolution.Source;

        // With none usable, pick from all of them so Apply reports why.
        var usable = titles.Where(t => IsUnlocked(t) == true).ToList();
        if (usable.Count == 0) usable = [.. titles];

        if (!repick && Equals(pickedFrom, from) && DesiredTitleId is { } kept && usable.Contains(kept)) return kept;
        repick = false;
        pickedFrom = from;
        return usable[Random.Shared.Next(usable.Count)];
    }

    private void Apply()
    {
        if (!clientState.IsLoggedIn) return;
        if (!Evaluate())
        {
            Status = RetryNextFrame() ? "Waiting for the character to load" : "Character didn't finish loading";
            return;
        }

        if (!config.Enabled)
        {
            Status = "Disabled";
            force = true;
            return;
        }

        var desired = DesiredTitleId;
        if (!force && desired == lastDesired) return;

        if (desired is not { } id)
        {
            Finish(desired, "No rule matches and no default title; leaving title alone");
            return;
        }
        if (CurrentTitleId == id)
        {
            Finish(id, $"Title set to {TitleName(id)}");
            return;
        }

        // The ConditionChange handler checks again once this ends.
        if (IsBusy())
        {
            Status = "Waiting for zoning or cutscene to finish";
            return;
        }

        var wait = lastSend + Cooldown - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            Status = "Waiting a few seconds before changing the title again";
            Schedule(Apply, wait);
            return;
        }

        var ui = UIState.Instance();
        if (ui == null) return;

        // Titles sharing a name are offered as one; send whichever copy this character has.
        var variant = id == 0 ? id : UnlockedVariant(id);
        if (variant is not { } send || send == 0)
        {
            // Leave lastDesired unset so the next change checks again, once the list is loaded
            // or in case the title was unlocked since.
            waitingSince = null;
            force = false;
            lastDesired = null;
            Status = variant == null
                ? "Open AutoTitle's settings once on this character so it can load your unlocked titles"
                : $"{TitleName(id)} is not unlocked on this character";
            log.Warning(variant == null ? "Unlocked titles not loaded yet, skipping." : $"Title {id} is not unlocked, skipping.");
            return;
        }

        ui->TitleController.SendTitleIdUpdate(send);
        lastSend = DateTime.UtcNow;
        Finish(id, "Setting title");
        log.Debug($"Sent title {send}.");
        framework.RunOnTick(() => Confirm(id), ConfirmDelay, cancellationToken: disposed.Token);
    }

    /// <summary>
    /// The copy of a title this character has, or 0 if none. Uses the game's own list when it has
    /// one this session (the Titles window or our settings window loaded it), otherwise the saved
    /// one. Null if neither exists.
    /// </summary>
    private ushort? UnlockedVariant(ushort id)
    {
        var ui = UIState.Instance();
        if (ui != null && CharacterId != 0 && ui->TitleList.DataReceived)
            return gameData.TitleVariants(id).FirstOrDefault(v => ui->TitleList.IsTitleUnlocked(v));
        if (profile?.UnlockedTitles is not { } saved) return null;
        return gameData.TitleVariants(id).FirstOrDefault(v => v / 8 < saved.Count && (saved[v / 8] & (1 << (v % 8))) != 0);
    }

    /// <summary>Saves the game's unlocked title list once it has one, so later sessions don't have to ask for it.</summary>
    private void SaveTitleList(CharacterProfile character)
    {
        var ui = UIState.Instance();
        if (ui == null || !ui->TitleList.DataReceived) return;
        var unlocked = ui->TitleList.UnlockedTitles;
        if (character.UnlockedTitles is { } saved && unlocked.SequenceEqual(saved.ToArray())) return;
        character.UnlockedTitles = [.. unlocked];
        config.Save();
    }

    private void Finish(ushort? desired, string status)
    {
        waitingSince = null;
        force = false;
        lastDesired = desired;
        Status = status;
    }

    private void Confirm(ushort id)
    {
        // A newer change has taken over.
        if (lastDesired != id || !Evaluate()) return;

        if (CurrentTitleId == id)
        {
            Status = $"Title set to {TitleName(id)}";
            Announce(id);
            return;
        }
        Status = $"Server did not accept {TitleName(id)}";
        log.Warning($"Title {id} was not applied.");
    }

    private bool IsBusy() =>
        condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
        || condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene78];

    private static bool IsBusyFlag(ConditionFlag flag) =>
        flag is ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51
            or ConditionFlag.OccupiedInCutSceneEvent or ConditionFlag.WatchingCutscene78;

    /// <summary>Adds a profile for a character the first time it's seen, and keeps its name and world current.</summary>
    private CharacterProfile RecordCharacter()
    {
        CharacterId = playerState.ContentId;
        var name = playerState.CharacterName;
        var world = playerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";

        if (!config.Characters.TryGetValue(CharacterId, out var profile))
        {
            profile = new CharacterProfile();
            config.Characters[CharacterId] = profile;
        }
        if (profile.Name != name || profile.World != world)
        {
            profile.Name = name;
            profile.World = world;
            config.Save();
        }
        return profile;
    }

    private GameContext Snapshot(uint jobId, bool feminine)
    {
        var territory = clientState.TerritoryType;
        var duty = dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(territory)?.ContentFinderCondition.ValueNullable;
        return new GameContext(
            JobId: jobId,
            JobGroups: gameData.GroupsOf(jobId),
            TerritoryId: gameData.Canonical(ConditionKind.Zone, territory),
            DutyId: duty is { } d ? gameData.Canonical(ConditionKind.Duty, d.RowId) : 0,
            DutyTypeId: duty is { } t ? gameData.DutyTypeOf(t) : 0,
            Gearset: CurrentGearset(),
            Feminine: feminine);
    }

    /// <summary>The gearset number shown in game (index + 1), or 0 for none.</summary>
    private static uint CurrentGearset()
    {
        var gearsets = RaptureGearsetModule.Instance();
        if (gearsets == null) return 0;
        var index = gearsets->CurrentGearsetIndex;
        return gearsets->IsValidGearset(index) ? (uint)index + 1 : 0;
    }

    private void Announce(ushort id)
    {
        if (!config.NotifyInChat) return;
        var what = Resolution.Rule is { } rule ? $"rule \"{rule.Name}\"" : "default";
        var why = Resolution.Source == RuleEngine.SharedSource ? what : $"{what}, character-specific";
        chat.Print($"Title set to {TitleName(id)} ({why}).", "AutoTitle");
    }

    private string TitleName(ushort id) => gameData.TitleLabel(id, Context?.Feminine ?? false);
}
