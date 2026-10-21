using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AutoTitle.Core;
using AutoTitle.Windows;

namespace AutoTitle;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IGameInventory Inventory { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;

    private const string CommandName = "/autotitle";

    public readonly WindowSystem WindowSystem = new("AutoTitle");
    private readonly MainWindow mainWindow;

    internal Configuration Config { get; }
    internal GameData GameData { get; }
    internal TitleApplier Applier { get; }

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GameData = new GameData(DataManager);
        Applier = new TitleApplier(Config, GameData, Framework, ClientState, Objects, PlayerState, Condition, Inventory,
            DataManager, Chat, Log);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the AutoTitle settings window.\n" +
                          "/autotitle on: turn automatic titles on.\n" +
                          "/autotitle off: turn automatic titles off.\n" +
                          "/autotitle toggle: switch automatic titles on or off.\n" +
                          "/autotitle apply: re-apply the title for your current situation.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;

        WindowSystem.RemoveAllWindows();
        Applier.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                SetEnabled(true);
                break;
            case "off":
                SetEnabled(false);
                break;
            case "toggle":
                SetEnabled(!Config.Enabled);
                break;
            case "apply":
                Applier.Reapply();
                break;
            default:
                ToggleMain();
                break;
        }
    }

    internal void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save();
        Applier.Refresh();
        Chat.Print(enabled ? "Enabled." : "Disabled.", "AutoTitle");
    }

    internal void ToggleMain() => mainWindow.Toggle();
}
