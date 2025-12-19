using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.EzHookManager;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NecroLens.Model;
using NecroLens.util;
using static NecroLens.util.DeepDungeonUtil;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace NecroLens.Service;

/**
 * Tracks the progress when inside a DeepDungeon.
 */
public class DeepDungeonService : IDisposable
{
    private readonly Configuration conf = Config;
    public readonly Dictionary<int, int> FloorTimes = [];
    public int CurrentContentId;
    public DeepDungeonContentInfo.DeepDungeonFloorSetInfo? FloorSetInfo;
    private readonly TaskManager taskManager;
    public readonly FloorDetails FloorDetails = new();
    public readonly Dictionary<(DeepDungeonItemKind, int), string> ItemNames = [];
#pragma warning disable CS0649
    private unsafe delegate void SystemLogMessageDelegate(uint entityId, uint logMessageId, int* args, byte argCount);
    [EzHook("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 0F B6 47 28", nameof(SystemLogMessageDetour))]
    private readonly EzHook<SystemLogMessageDelegate> systemLogMessageHook;
#pragma warning restore CS0649
    public DeepDungeonService()
    {
        EzSignatureHelper.Initialize(this);
        taskManager = new TaskManager(new TaskManagerConfiguration
        {
            TimeoutSilently = true
        });
        
        foreach (var pomander in Svc.Data.GetExcelSheet<DeepDungeonItem>(Svc.ClientState.ClientLanguage).Skip(1))
        {
            ItemNames[(DeepDungeonItemKind.Pomander, (int)pomander.RowId)] = pomander.Name.ToString();
        }
        foreach (var magicstone in Svc.Data.GetExcelSheet<DeepDungeonMagicStone>(Svc.ClientState.ClientLanguage).Skip(1))
        {
            ItemNames[(DeepDungeonItemKind.MagicStone, (int)magicstone.RowId)] = magicstone.Name.ToString();
        }
        foreach (var demiclone in Svc.Data.GetExcelSheet<DeepDungeonDemiclone>(Svc.ClientState.ClientLanguage).Skip(1))
        {
            ItemNames[(DeepDungeonItemKind.Demiclone, (int)demiclone.RowId)] = demiclone.TitleCase.ToString();
        }
        CheckEnteredNewFloor();
        Svc.Condition.ConditionChange += OnConditionChanged;
    }

    private void EnterDeepDungeon(int contentId, DeepDungeonContentInfo.DeepDungeonFloorSetInfo info, int currentFloor)
    {
        FloorSetInfo = info;
        CurrentContentId = contentId;
        Svc.Log.Debug($"Entering ContentID {CurrentContentId}");

        FloorTimes.Clear();

        MobService.TryReloadIfEmpty();

        for (var i = info.StartFloor; i < info.StartFloor + 10; i++)
            FloorTimes[i] = 0;

        FloorDetails.CurrentFloor = currentFloor - 1; // NextFloor() adds 1
        FloorDetails.RespawnTime = info.RespawnTime;
        FloorDetails.FloorTransfer = true;
        FloorDetails.NextFloor();

        if (Config.AutoOpenOnEnter)
            Plugin.ShowMainWindow();
        Svc.Framework.Update += Update;
    }
    
    private unsafe void CheckEnteredNewFloor()
    {
        var dd = EventFramework.Instance()->GetInstanceContentDeepDungeon();
        if (dd == null || dd->Floor == 0 || !DeepDungeonContentInfo.ContentInfo.TryGetValue((int)dd->ContentId, out var info))
            return;
        if (!InDeepDungeon)
        {
            InPotD = dd->ContentId.InRange(60001, 60020, true);
            InHoH = dd->ContentId.InRange(60021, 60030, true);
            InEO = dd->ContentId.InRange(60031, 60040, true);
            InPT = dd->ContentId.InRange(60041, 60050, true);
            EnterDeepDungeon((int)dd->ContentId, info, dd->Floor);
        }
        else if (FloorDetails.FloorTransfer)
        {
            FloorDetails.NextFloor();
        }
    }

    private void ExitDeepDungeon()
    {
        Svc.Log.Debug($"ContentID {CurrentContentId} - Exiting");
        Svc.Framework.Update -= Update;
        FloorDetails.DumpFloorObjects(CurrentContentId);
        FloorSetInfo = null;
        FloorDetails.Clear();
        InPotD = InHoH = InEO = InPT = false;
        Plugin.CloseMainWindow();
    }

    private void Update(IFramework _)
    {
        if (EzThrottler.Throttle("TimerUpdate", 500))
        {
            FloorTimes[FloorDetails.CurrentFloor] = FloorDetails.UpdateFloorTime();
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.Occupied33)
        {
            if (!value)
                CheckEnteredNewFloor();
        }
        else if (flag == ConditionFlag.InDeepDungeon)
        {
            if (!value)
                ExitDeepDungeon();
        }
    }

    private unsafe void SystemLogMessageDetour(uint entityId, uint logId, int* args, byte argCount)
    {
        systemLogMessageHook!.Original(entityId, logId, args, argCount);
        
        if (InDeepDungeon)
        {
            switch (logId)
            {
                case 7248:
                    FloorDetails.FloorTransfer = true;
                    FloorDetails.DumpFloorObjects(CurrentContentId);
                    FloorDetails.FloorObjects.Clear();
                    break;
                case 7254:
                    FloorDetails.OnPomanderUsed((Pomander)args[1]);
                    break;
                case 11251:
                    FloorDetails.OnDemicloneUsed((Demiclone)args[1]);
                    break;
                case 7275:
                case 7276:
                    FloorDetails.AccursedHoardOpened = true;
                    break;
                case 7222: // DeepDungeonItem (Pomander and Protomander)
                    var chestPomander = Svc.Objects.Where(o => o.BaseId == DataIds.GoldChest).FirstOrDefault(o =>  o.Position.Distance2D(Player.Position) <= 4.6f);
                    if (chestPomander != null)
                    {
                        FloorDetails.DoubleChests[chestPomander.EntityId] = (DeepDungeonItemKind.Pomander, args[0]);
                    }
                    break;
                case 9208: // DeepDungeonMagicStone
                    var chestMagicStone = Svc.Objects.Where(o => o.BaseId == DataIds.SilverChest).FirstOrDefault(o => o.Position.Distance2D(Player.Position) <= 4.6f);
                    if (chestMagicStone != null)
                    {
                        FloorDetails.DoubleChests[chestMagicStone.EntityId] = (DeepDungeonItemKind.MagicStone, args[0]);
                    }
                    break;
                case 10287: // DeepDungeonDemiclone
                    var chestDemiclone = Svc.Objects.Where(o => o.BaseId == DataIds.SilverChest).FirstOrDefault(o => o.Position.Distance2D(Player.Position) <= 4.6f);
                    if (chestDemiclone != null)
                    {
                        FloorDetails.DoubleChests[chestDemiclone.EntityId] = (DeepDungeonItemKind.Demiclone, args[0]);
                    }
                    break;
            }
        }
    }

    private bool CheckChestOpenSafe(ESPObject.ESPType type)
    {
        var info = DungeonService.FloorSetInfo;
        var unsafeChest = false;
        if (info != null)
        {
            unsafeChest = (info.MimicChests == DeepDungeonContentInfo.MimicChests.Silver &&
                           type == ESPObject.ESPType.SilverChest) ||
                          (info.MimicChests == DeepDungeonContentInfo.MimicChests.Gold &&
                           type == ESPObject.ESPType.GoldChest);
        }

        return !unsafeChest || (unsafeChest && conf.OpenUnsafeChests);
    }

    internal unsafe void TryInteract(ESPObject espObj)
    {
        var player = Svc.Objects.LocalPlayer!;
        if ((player.StatusFlags & StatusFlags.InCombat) == 0 && conf.OpenChests && espObj.IsChest())
        {
            var type = espObj.Type;

            if (!conf.OpenBronzeCoffers && type == ESPObject.ESPType.BronzeChest) return;
            if (!conf.OpenSilverCoffers && type == ESPObject.ESPType.SilverChest) return;
            if (!conf.OpenGoldCoffers && type == ESPObject.ESPType.GoldChest) return;
            if (!conf.OpenHoards && type == ESPObject.ESPType.AccursedHoardCoffer) return;

            // We dont want to kill the player
            if (type == ESPObject.ESPType.SilverChest && player.CurrentHp <= player.MaxHp * 0.77) return;

            if (CheckChestOpenSafe(type) && espObj.Distance() <= espObj.InteractionDistance()
                                         && !FloorDetails.InteractionList.Contains(espObj.GameObject.EntityId))
            {
                TargetSystem.Instance()->InteractWithObject((GameObject*)espObj.GameObject.Address);
                FloorDetails.InteractionList.Add(espObj.GameObject.EntityId);
            }
        }
    }

    public unsafe void TryNearestOpenChest()
    {
        // Checks every object to be a chest and try to open the  
        foreach (var obj in Svc.Objects)
            if (obj.IsValid())
            {
                var dataId = obj.BaseId;
                if (DataIds.BronzeChestIDs.Contains(dataId) || DataIds.SilverChest == dataId ||
                    DataIds.GoldChest == dataId || DataIds.AccursedHoardCoffer == dataId)
                {
                    var espObj = new ESPObject(obj);
                    if (CheckChestOpenSafe(espObj.Type) && espObj.Distance() <= espObj.InteractionDistance())
                    {
                        TargetSystem.Instance()->InteractWithObject((GameObject*)espObj.GameObject.Address);
                        break;
                    }
                }
            }
    }

    public unsafe void OnPomanderCommand(string pomanderName)
    {
        if (TryFindPomanderByName(pomanderName, out var pomander) && IsPomanderUsable(pomander))
        {
            PrintChatMessage($"Using found pomander: {pomander}");
            if (!TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out _))
            {
                AgentDeepDungeonStatus.Instance()->AgentInterface.Show();
            }

            taskManager.Enqueue(() => TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var addon) &&
                                      IsAddonReady(addon));
            taskManager.Enqueue(() =>
            {
                TryGetAddonByName<AtkUnitBase>("DeepDungeonStatus", out var addon);
                Callback.Fire(addon, true, 11, (int)pomander);
            });
        }
    }

    public void TrackFloorObjects(ESPObject espObj)
    {
        FloorDetails.TrackFloorObjects(espObj, CurrentContentId);
    }

    public void Dispose()
    {
        Svc.Framework.Update -= Update;
        Svc.Condition.ConditionChange -= OnConditionChanged;
    }
}
