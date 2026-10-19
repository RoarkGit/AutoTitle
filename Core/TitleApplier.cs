using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace AutoTitle.Core;

/// <summary>
/// Polls the game state, resolves the title it calls for, and sends it to the server. A title is
/// only sent when the resolved title changes or on login, so a title picked by hand sticks until
/// something relevant changes.
/// </summary>
public sealed unsafe class TitleApplier : IDisposable
{
    private const float EvaluateInterval = 0.25f;
    private const float RetryDelay = 3f;
    private const int MaxAttempts = 3;

    private readonly Configuration config;
    private readonly GameData gameData;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private float evaluateTimer;
    private float sinceSend = RetryDelay;
    private bool force = true;
    private ushort? lastDesired;
    private ushort? pending;
    private int attempts;
    private bool requestedTitleList;

    /// <summary>Null while logged out.</summary>
    public GameContext? Context { get; private set; }

    /// <summary>Content id of the logged-in character, 0 while logged out.</summary>
    public ulong CharacterId { get; private set; }

    public Resolution Resolution { get; private set; }
    public ushort? CurrentTitleId { get; private set; }
    public string Status { get; private set; } = "Waiting for login";

    public TitleApplier(Configuration config, GameData gameData, IClientState clientState, IObjectTable objects,
        IPlayerState playerState, ICondition condition, IDataManager dataManager, IChatGui chat, IPluginLog log)
    {
        this.config = config;
        this.gameData = gameData;
        this.clientState = clientState;
        this.objects = objects;
        this.playerState = playerState;
        this.condition = condition;
        this.dataManager = dataManager;
        this.chat = chat;
        this.log = log;

        clientState.Login += Reapply;
        clientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        clientState.Login -= Reapply;
        clientState.Logout -= OnLogout;
    }

    /// <summary>Sends the resolved title on the next evaluation even if it hasn't changed.</summary>
    public void Reapply()
    {
        force = true;
        evaluateTimer = EvaluateInterval;
    }

    /// <summary>Null until the game has received the unlocked title list from the server.</summary>
    public bool? IsUnlocked(ushort id)
    {
        if (id == 0) return true;
        var ui = UIState.Instance();
        if (ui == null || CharacterId == 0 || !ui->TitleList.DataReceived) return null;
        return ui->TitleList.IsTitleUnlocked(id);
    }

    public void Update(float delta)
    {
        sinceSend += delta;
        evaluateTimer += delta;
        if (evaluateTimer < EvaluateInterval) return;
        evaluateTimer = 0;

        if (!clientState.IsLoggedIn || !playerState.IsLoaded || objects.LocalPlayer is not { } player) return;

        var profile = RecordCharacter();
        CurrentTitleId = ((Character*)player.Address)->TitleId;
        Context = Snapshot(player.ClassJob.RowId, player.Customize[(int)CustomizeIndex.Gender] == 1);
        Resolution = RuleEngine.Resolve(config, profile, Context.Value);

        if (!config.Enabled)
        {
            Status = "Disabled";
            pending = null;
            force = true;
            return;
        }

        var desired = Resolution.TitleId;
        if (force || desired != lastDesired)
        {
            force = false;
            lastDesired = desired;
            pending = desired;
            attempts = 0;
            if (desired == null) Status = "No rule matches and no default title; leaving title alone";
        }

        if (pending is { } id) TryApply(id);
    }

    private void OnLogout(int type, int code)
    {
        Context = null;
        CharacterId = 0;
        CurrentTitleId = null;
        pending = null;
        lastDesired = null;
        requestedTitleList = false;
        force = true;
        Status = "Waiting for login";
    }

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
            TerritoryId: territory,
            MapId: clientState.MapId,
            DutyId: duty?.RowId ?? 0,
            DutyTypeId: duty?.ContentType.RowId ?? 0,
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

    private void TryApply(ushort id)
    {
        if (CurrentTitleId == id)
        {
            if (attempts > 0) Announce(id);
            pending = null;
            Status = $"Title set to {TitleName(id)}";
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene78])
        {
            Status = "Waiting for zoning or cutscene to finish";
            return;
        }

        if (sinceSend < RetryDelay) return;

        var ui = UIState.Instance();
        if (ui == null) return;

        switch (IsUnlocked(id))
        {
            case null:
                // The game only fetches the unlocked list when something asks for it, such as
                // opening the title window, so ask for it once ourselves.
                if (!requestedTitleList && !ui->TitleList.DataPending && !ui->TitleList.DataRequested)
                {
                    ui->TitleList.RequestTitleList();
                    requestedTitleList = true;
                }
                Status = "Loading unlocked titles";
                return;
            case false:
                pending = null;
                Status = $"{TitleName(id)} is not unlocked on this character";
                log.Warning($"Title {id} is not unlocked, skipping.");
                return;
        }

        if (attempts >= MaxAttempts)
        {
            pending = null;
            Status = $"Server did not accept {TitleName(id)}";
            log.Warning($"Title {id} still not applied after {MaxAttempts} attempts, giving up.");
            return;
        }

        ui->TitleController.SendTitleIdUpdate(id);
        attempts++;
        sinceSend = 0;
        Status = "Setting title";
        log.Debug($"Sent title {id} (attempt {attempts}).");
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
