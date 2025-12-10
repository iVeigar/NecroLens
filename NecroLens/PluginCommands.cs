using System;
using Dalamud.Game.Command;
using ECommons.DalamudServices;
using NecroLens.Data;

namespace NecroLens;

public class PluginCommands : IDisposable
{
    public PluginCommands()
    {
        Svc.Commands.AddHandler("/necrolens",
            new CommandInfo((_, _) => Plugin.ShowMainWindow())
            {
                HelpMessage = Strings.PluginCommands_OpenOverlay_Help,
                ShowInHelp = true
            });

        Svc.Commands.AddHandler("/necrolenscfg",
            new CommandInfo((_, _) => Plugin.ShowConfigWindow())
            {
                HelpMessage = Strings.PluginCommands_OpenConfig_Help,
                ShowInHelp = true
            });

        Svc.Commands.AddHandler("/openchest",
            new CommandInfo((_, _) => DungeonService.TryNearestOpenChest())
            {
                HelpMessage = Strings.PluginCommands_OpenChest_Help,
                ShowInHelp = true
            });

        Svc.Commands.AddHandler("/pomander",
            new CommandInfo((_, args) => DungeonService.OnPomanderCommand(args))
            {
                HelpMessage = "Try to use the pomander with given name",
                ShowInHelp = true
            });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler("/necrolens");
        Svc.Commands.RemoveHandler("/necrolenscfg");
        Svc.Commands.RemoveHandler("/openchest");
        Svc.Commands.RemoveHandler("/pomander");
    }
}
