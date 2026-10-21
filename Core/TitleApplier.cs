using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace AutoTitle.Core;

/// <summary>
/// Checks the rules whenever something relevant changes in game and sends the title they pick.
/// </summary>
public sealed unsafe class TitleApplier : IDisposable
{
    // Minimum time between sends.
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    // How long to wait before checking whether a send worked.
    private static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(3);

    // There's no event for the character finishing loading, so we retry every frame for up to
    // this long.
    private static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(10);

    private readonly Configuration config;
    private readonly GameData gameData;

    private readonly CancellationTokenSource disposed = new();
    private CancellationTokenSource? scheduled;
    private DateTime lastSend = DateTime.MinValue;
    private bool force = true;
    private ushort? lastDesired;
    private DateTime? waitingSince;
    private CharacterProfile? profile;
    private bool waitingForTitleList;

    // The rule (or default) the current random pick came from.
    private object? pickedFrom;
    private bool repick = true;

    /// <summary>The current job, zone, etc. Null while logged out.</summary>
    public GameContext? Context { get; private set; }

    /// <summary>The logged in character's content ID, 0 while logged out.</summary>
    public ulong CharacterId { get; private set; }
    public Resolution Resolution { get; private set; }

    /// <summary>The title we want to be using, or null to leave it alone.</summary>
    public ushort? DesiredTitleId { get; private set; }
    public ushort? CurrentTitleId { get; private set; }
    public string Status { get; private set; } = "Not logged in.";

    public TitleApplier(Configuration config, GameData gameData)
    {
        this.config = config;
        this.gameData = gameData;

        Plugin.ClientState.Login += Reapply;
        Plugin.ClientState.Logout += OnLogout;
        Plugin.ClientState.ClassJobChanged += OnClassJobChanged;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.Inventory.InventoryChangedRaw += OnInventoryChanged;

        if (Plugin.ClientState.IsLoggedIn)
            Reapply();
    }

    public void Dispose()
    {
        Plugin.ClientState.Login -= Reapply;
        Plugin.ClientState.Logout -= OnLogout;
        Plugin.ClientState.ClassJobChanged -= OnClassJobChanged;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.Condition.ConditionChange -= OnConditionChange;
        Plugin.Inventory.InventoryChangedRaw -= OnInventoryChanged;
        disposed.Cancel();
        scheduled?.Cancel();
    }

    /// <summary>
    /// Sends the title for the current situation even if it hasn't changed, and re-rolls rules
    /// with multiple titles.
    /// </summary>
    public void Reapply()
    {
        Plugin.Framework.RunOnTick(
            () =>
            {
                force = true;
                repick = true;
                waitingSince = null;
                Evaluate();
                Schedule(Apply, TimeSpan.Zero);
            },
            cancellationToken: disposed.Token
        );
    }

    /// <summary>Checks the rules again and sends the title if it changed.</summary>
    public void Refresh()
    {
        Plugin.Framework.RunOnTick(
            () =>
            {
                waitingSince = null;
                Evaluate();
                Schedule(Apply, TimeSpan.Zero);
            },
            cancellationToken: disposed.Token
        );
    }

    /// <summary>Checks if the current character has a title unlocked.</summary>
    /// <param name="id">The title's ID</param>
    /// <returns>Whether the title is unlocked, or null if the title list isn't loaded.</returns>
    public bool? IsUnlocked(ushort id)
    {
        // Everyone can have no title.
        if (id == 0)
            return true;

        var variant = UnlockedVariant(id);
        if (variant == null)
            return null;
        return variant != 0;
    }

    /// <summary>
    /// Requests the unlocked title list if the game doesn't have it yet. This only gets called when
    /// the settings window opens, so we never ask the server for anything on our own.
    /// </summary>
    public void LoadTitleList()
    {
        var ui = UIState.Instance();
        if (!Plugin.ClientState.IsLoggedIn || ui == null)
            return;

        var list = &ui->TitleList;
        if (list->DataReceived || list->DataPending || list->DataRequested)
            return;
        list->RequestTitleList();
        waitingForTitleList = true;
    }

    /// <summary>
    /// Called every frame the settings window is open, refreshes once the title list arrives.
    /// </summary>
    public void CheckTitleList()
    {
        if (!waitingForTitleList)
            return;

        var ui = UIState.Instance();
        if (ui == null || !ui->TitleList.DataReceived)
            return;
        waitingForTitleList = false;
        Refresh();
    }

    // Events

    private void OnClassJobChanged(uint jobId)
    {
        Refresh();
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        Refresh();
    }

    // We don't send while zoning or in a cutscene, so try again once those end.
    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!value && IsBusyFlag(flag))
            Refresh();
    }

    // There's no gearset event, but switching gearsets changes equipped items.
    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        foreach (var e in events)
        {
            if (e.Item.ContainerType == GameInventoryType.EquippedItems)
            {
                Refresh();
                return;
            }
        }
    }

    private void OnLogout(int type, int code)
    {
        scheduled?.Cancel();
        Context = null;
        CharacterId = 0;
        CurrentTitleId = null;
        DesiredTitleId = null;
        lastDesired = null;
        profile = null;
        waitingForTitleList = false;
        pickedFrom = null;
        repick = true;
        force = true;
        Status = "Not logged in.";
    }

    // Applying titles

    // Runs an action after a delay, cancelling anything that was already scheduled.
    private void Schedule(Action action, TimeSpan delay, int delayTicks = 0)
    {
        scheduled?.Cancel();
        scheduled = CancellationTokenSource.CreateLinkedTokenSource(disposed.Token);
        Plugin.Framework.RunOnTick(action, delay, delayTicks, scheduled.Token);
    }

    // Tries Apply again next frame, or returns false if we've been waiting too long.
    private bool RetryNextFrame()
    {
        if (waitingSince == null)
            waitingSince = DateTime.UtcNow;

        if (DateTime.UtcNow - waitingSince > LoadingTimeout)
        {
            waitingSince = null;
            return false;
        }
        Schedule(Apply, TimeSpan.Zero, 1);
        return true;
    }

    // Reads the current game state and checks the rules. Returns false if the character isn't
    // loaded.
    private bool Evaluate()
    {
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            return false;
        var player = Plugin.Objects.LocalPlayer;
        if (player == null)
            return false;

        profile = RecordCharacter();
        SaveTitleList(profile);

        var character = (Character*)player.Address;
        CurrentTitleId = gameData.CanonicalTitle(character->TitleId);
        var feminine = player.Customize[(int)CustomizeIndex.Gender] == 1;
        Context = Snapshot(player.ClassJob.RowId, feminine);
        Resolution = RuleEngine.Resolve(config, profile, Context.Value);
        DesiredTitleId = PickTitle();
        return true;
    }

    // Picks a random title from the resolved ones, keeping the same pick while the same rule is
    // active.
    private ushort? PickTitle()
    {
        var titles = Resolution.Titles;
        if (titles == null)
            return null;

        object from = Resolution.Source;
        if (Resolution.Rule != null)
            from = Resolution.Rule;

        var usable = new List<ushort>();
        foreach (var title in titles)
        {
            if (IsUnlocked(title) == true)
                usable.Add(title);
        }
        // If nothing is unlocked, pick anyway so Apply can show why it isn't sending.
        if (usable.Count == 0)
            usable.AddRange(titles);

        // Keep the last pick if the same rule is still active.
        var keep = !repick && Equals(pickedFrom, from) && DesiredTitleId != null;
        if (keep && usable.Contains(DesiredTitleId!.Value))
            return DesiredTitleId;

        repick = false;
        pickedFrom = from;
        return usable[Random.Shared.Next(usable.Count)];
    }

    private void Apply()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return;

        if (!Evaluate())
        {
            if (RetryNextFrame())
                Status = "Waiting for character to load.";
            else
                Status = "Couldn't load character, try Apply now.";
            return;
        }

        if (!config.Enabled)
        {
            Status = "Disabled.";
            force = true;
            return;
        }

        // Only send when the title we want changes, unless forced.
        var desired = DesiredTitleId;
        if (!force && desired == lastDesired)
            return;

        if (desired == null)
        {
            Finish(null, "No rule matches, leaving title alone.");
            return;
        }
        var id = desired.Value;
        if (CurrentTitleId == id)
        {
            Finish(id, $"Title set to {TitleName(id)}.");
            return;
        }

        if (IsBusy())
        {
            Status = "Waiting for zoning or a cutscene to finish.";
            return;
        }

        // Check cooldown.
        var wait = lastSend + Cooldown - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            Status = "Waiting for cooldown.";
            Schedule(Apply, wait);
            return;
        }

        var ui = UIState.Instance();
        if (ui == null)
            return;

        // Duplicate titles are shown as one, so send whichever copy we actually have.
        ushort send = 0;
        if (id != 0)
        {
            var variant = UnlockedVariant(id);
            if (variant == null || variant == 0)
            {
                // Leave lastDesired unset so we try again on the next event.
                waitingSince = null;
                force = false;
                lastDesired = null;
                if (variant == null)
                {
                    Status = "Waiting for title list.";
                    Plugin.Log.Warning("Unlocked titles not loaded yet, skipping.");
                }
                else
                {
                    Status = $"{TitleName(id)} isn't unlocked on this character.";
                    Plugin.Log.Warning($"Title {id} is not unlocked, skipping.");
                }
                return;
            }
            send = variant.Value;
        }

        ui->TitleController.SendTitleIdUpdate(send);
        lastSend = DateTime.UtcNow;
        Finish(id, "Setting title...");
        Plugin.Log.Debug($"Sent title {send}.");
        Plugin.Framework.RunOnTick(
            () => Confirm(id),
            ConfirmDelay,
            cancellationToken: disposed.Token
        );
    }

    private void Finish(ushort? desired, string status)
    {
        waitingSince = null;
        force = false;
        lastDesired = desired;
        Status = status;
    }

    // Checks that a sent title actually got set.
    private void Confirm(ushort id)
    {
        // Something else changed since, so don't bother.
        if (lastDesired != id || !Evaluate())
            return;

        if (CurrentTitleId == id)
        {
            Status = $"Title set to {TitleName(id)}.";
            Announce(id);
            return;
        }
        Status = $"Server didn't accept {TitleName(id)}.";
        Plugin.Log.Warning($"Title {id} was not applied.");
    }

    // Title list

    // Finds the copy of a title we have unlocked, 0 if none, or null if there's no title list yet.
    // The game's list is used if it has one, otherwise the one we saved.
    private ushort? UnlockedVariant(ushort id)
    {
        var ui = UIState.Instance();
        if (ui != null && CharacterId != 0 && ui->TitleList.DataReceived)
        {
            foreach (var variant in gameData.TitleVariants(id))
            {
                if (ui->TitleList.IsTitleUnlocked(variant))
                    return variant;
            }
            return 0;
        }

        var saved = profile?.UnlockedTitles;
        if (saved == null)
            return null;
        foreach (var variant in gameData.TitleVariants(id))
        {
            // The saved list is a bitmask, one bit per title ID.
            var index = variant / 8;
            var bit = 1 << (variant % 8);
            if (index < saved.Count && (saved[index] & bit) != 0)
                return variant;
        }
        return 0;
    }

    // Saves the game's title list so we don't need to request it again next session.
    private void SaveTitleList(CharacterProfile character)
    {
        var ui = UIState.Instance();
        if (ui == null || !ui->TitleList.DataReceived)
            return;

        var unlocked = ui->TitleList.UnlockedTitles.ToArray();
        if (character.UnlockedTitles != null && unlocked.SequenceEqual(character.UnlockedTitles))
            return;
        character.UnlockedTitles = new List<byte>(unlocked);
        config.Save();
    }

    // Game state

    private bool IsBusy()
    {
        return Plugin.Condition[ConditionFlag.BetweenAreas]
            || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Plugin.Condition[ConditionFlag.WatchingCutscene78];
    }

    private static bool IsBusyFlag(ConditionFlag flag)
    {
        return flag == ConditionFlag.BetweenAreas
            || flag == ConditionFlag.BetweenAreas51
            || flag == ConditionFlag.OccupiedInCutSceneEvent
            || flag == ConditionFlag.WatchingCutscene78;
    }

    // Creates a profile the first time a character logs in and keeps its name/world up to date.
    private CharacterProfile RecordCharacter()
    {
        CharacterId = Plugin.PlayerState.ContentId;
        var name = Plugin.PlayerState.CharacterName;
        var world = "";
        var homeWorld = Plugin.PlayerState.HomeWorld.ValueNullable;
        if (homeWorld != null)
            world = homeWorld.Value.Name.ExtractText();

        if (!config.Characters.TryGetValue(CharacterId, out var character))
        {
            character = new CharacterProfile();
            config.Characters[CharacterId] = character;
        }
        if (character.Name != name || character.World != world)
        {
            character.Name = name;
            character.World = world;
            config.Save();
        }
        return character;
    }

    private GameContext Snapshot(uint jobId, bool feminine)
    {
        var terr = Plugin.ClientState.TerritoryType;
        var zone = gameData.Canonical(ConditionKind.Zone, terr);

        uint dutyId = 0;
        uint dutyType = 0;
        var row = Plugin
            .DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(terr);
        var duty = row?.ContentFinderCondition.ValueNullable;
        if (duty != null)
        {
            dutyId = gameData.Canonical(ConditionKind.Duty, duty.Value.RowId);
            dutyType = gameData.DutyTypeOf(duty.Value);
        }

        var groups = gameData.GroupsOf(jobId);
        return new GameContext(jobId, groups, zone, dutyId, dutyType, CurrentGearset(), feminine);
    }

    // Gearset number as shown in game (starting at 1), or 0 if none.
    private static uint CurrentGearset()
    {
        var gs = RaptureGearsetModule.Instance();
        if (gs == null)
            return 0;

        var idx = gs->CurrentGearsetIndex;
        if (!gs->IsValidGearset(idx))
            return 0;
        return (uint)idx + 1;
    }

    private void Announce(ushort id)
    {
        if (!config.NotifyInChat)
            return;

        var why = "default";
        if (Resolution.Rule != null)
            why = $"rule \"{Resolution.Rule.Name}\"";
        if (Resolution.Source != RuleEngine.SharedSource)
            why += ", character-specific";
        Plugin.Chat.Print($"Title set to {TitleName(id)} ({why}).", "AutoTitle");
    }

    private string TitleName(ushort id)
    {
        var feminine = Context != null && Context.Value.Feminine;
        return gameData.TitleLabel(id, feminine);
    }
}
