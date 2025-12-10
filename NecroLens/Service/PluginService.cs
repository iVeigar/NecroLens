using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NecroLens.Model;

namespace NecroLens.Service;

public class PluginService
{
    public static NecroLens Plugin = null!;

    public static MobInfoService MobService { get; set; } = null!;
    public static Configuration Config { get; set; } = null!;
    public static DeepDungeonService DungeonService { get; set; } = null!;
}
