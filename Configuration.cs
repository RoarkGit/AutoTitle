using AutoTitle.Core;
using Dalamud.Configuration;

namespace AutoTitle;

[Serializable]
public sealed class Configuration : IPluginConfiguration, IRuleSet
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool NotifyInChat { get; set; } = true;

    // Null leaves the title alone, 0 removes it.
    public ushort? DefaultTitleId { get; set; }

    public List<TitleRule> Rules { get; set; } = new();

    // Keyed by content ID.
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
