using Dalamud.Configuration;
using AutoTitle.Core;

namespace AutoTitle;

[Serializable]
public sealed class Configuration : IPluginConfiguration, IRuleSet
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool NotifyInChat { get; set; } = true;

    /// <summary>Null leaves the title alone; 0 clears it.</summary>
    public ushort? DefaultTitleId { get; set; }

    public List<TitleRule> Rules { get; set; } = [];

    /// <summary>Keyed by content id. Every character that logs in gets an entry.</summary>
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
