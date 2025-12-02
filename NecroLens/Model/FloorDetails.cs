using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using NecroLens.util;
using Newtonsoft.Json;
using static NecroLens.util.DeepDungeonUtil;

namespace NecroLens.Model;

public partial class FloorDetails
{
    public Dictionary<uint, Pomander> DoubleChests { get; private set; } = [];
    public List<Pomander> FloorEffects { get; private set; } = [];
    public Dictionary<uint, FloorObject> FloorObjects { get; private set; } = [];
    public List<uint> InteractionList { get; private set; } = [];

    private readonly List<Pomander> usedPomanders = [];

    public int CurrentFloor { get; set; }
    public long FloorStartTime { get; private set; } = Environment.TickCount64;
    public bool FloorTransfer { get; set; }
    public bool AccursedHoardOpened { get; set; }
    public long NextRespawn { get; private set; }
    public int RespawnTime { get; set; }

    public void Clear()
    {
        usedPomanders.Clear();
        FloorEffects.Clear();
        InteractionList.Clear();
        FloorObjects.Clear();
        DoubleChests.Clear();
        CurrentFloor = 0;
        FloorTransfer = false;
        mobMovement.Clear();
    }

    public void NextFloor()
    {
        if (FloorTransfer)
        {
            PluginLog.Debug($"NextFloor: {CurrentFloor + 1}");

            // Reset
            InteractionList.Clear();
            FloorObjects.Clear();
            DoubleChests.Clear();

            // Apply effects
            FloorEffects.Clear();
            if (usedPomanders.ContainsAny(Pomander.Affluence, Pomander.AffluenceProtomander))
                FloorEffects.Add(Pomander.Affluence);

            if (usedPomanders.ContainsAny(Pomander.Alteration, Pomander.AlterationProtomander))
                FloorEffects.Add(Pomander.Alteration);

            if (usedPomanders.ContainsAny(Pomander.Flight, Pomander.FlightProtomander))
                FloorEffects.Add(Pomander.Flight);

            usedPomanders.Clear();
            AccursedHoardOpened = false;
            CurrentFloor++;
            FloorStartTime = Environment.TickCount64;
            NextRespawn = FloorStartTime + RespawnTime * 1000;
            FloorTransfer = false;
        }
    }

    public unsafe int PassageProgress()
    {
        var dd = EventFramework.Instance()->GetInstanceContentDeepDungeon();
        return dd == null ? 0 : Math.Max(dd->PassageProgress - 1, 0) * 10;
    }

    public void OnPomanderUsed(Pomander pomander)
    {
        PluginLog.Debug($"Pomander ID: {pomander}");

        if (InEO)
        {
            if (pomander is >= Pomander.SafetyProtomander and <= Pomander.SerenityProtomander) pomander -= 22;

            if (pomander is Pomander.IntuitionProtomander or Pomander.RaisingProtomander) pomander -= 20;
        }

        if (pomander is Pomander.Affluence or Pomander.Flight or Pomander.Alteration)
            usedPomanders.Add(pomander);
        else
        {
            FloorEffects.Add(pomander);
            usedPomanders.Add(pomander);
        }
    }

    public DeepDungeonTrapStatus TrapStatus()
    {
        if (FloorEffects.ContainsAny(Pomander.Safety, Pomander.SafetyProtomander))
            return DeepDungeonTrapStatus.Inactive;

        if (FloorEffects.ContainsAny(Pomander.Sight, Pomander.SightProtomander)) return DeepDungeonTrapStatus.Visible;

        return DeepDungeonTrapStatus.Active;
    }

    public bool HasRespawn()
    {
        return !(CurrentFloor % 10 == 0 || ((InEO || InPT) && CurrentFloor == 99));
    }

    public int TimeTillRespawn()
    {
        return (int)((Environment.TickCount64 - NextRespawn) / 1000);
    }

    public int UpdateFloorTime() // return seconds
    {
        var now = Environment.TickCount64;
        var time = (int)((now - FloorStartTime) / 1000);
        if (now > NextRespawn) NextRespawn = NextRespawn + RespawnTime * 1000;
        return time;
    }

    public void TrackFloorObjects(ESPObject espObj, int currentContentId)
    {
        if (FloorTransfer
            || IsIgnored(espObj.GameObject.BaseId)
            || FloorObjects.ContainsKey(espObj.GameObject.EntityId)) return;

        var obj = new FloorObject();
        obj.DataId = espObj.GameObject.BaseId;
        if (espObj.GameObject is IBattleNpc npcObj)
        {
            obj.NameId = npcObj.NameId;
            obj.Name = npcObj.Name.TextValue;
        }

        obj.ContentId = currentContentId;
        obj.Floor = CurrentFloor;
        obj.HitboxRadius = espObj.GameObject.HitboxRadius;
        FloorObjects[espObj.GameObject.EntityId] = obj;
    }

    private bool IsIgnored(uint dataId)
    {
        return DataIds.ReturnIDs.Contains(dataId)
               || DataIds.PassageIDs.Contains(dataId)
               || DataIds.TrapIDs.ContainsKey(dataId)
               || DataIds.GoldChest == dataId
               || DataIds.SilverChest == dataId
               || DataIds.MimicChest == dataId
               || DataIds.BronzeChestIDs.Contains(dataId)
               || DataIds.AccursedHoard == dataId
               || DataIds.AccursedHoardCoffer == dataId;
    }

    public void DumpFloorObjects(int currentContentId)
    {
        if (Config.OptInDataCollection)
        {
            var result = new Dictionary<uint, DataCollector.MobData>();

            foreach (var keyValuePair in FloorObjects)
            {
                DataCollector.MobData data = new()
                {
                    DataId = keyValuePair.Value.DataId,
                    NameId = keyValuePair.Value.NameId,
                    ContentId = currentContentId,
                    Floor = CurrentFloor,
                    HitboxRadius = keyValuePair.Value.HitboxRadius,
                    MoveTimes = [],     // TODO
                    AggroDistances = [] // TODO
                };
                result.TryAdd(data.DataId, data);
            }

            var collector = new DataCollector
            {
                Sender = Config.UniqueId!,
                Party = PartyList.PartyId.ToString(),
                Data = new Collection<DataCollector.MobData>(result.Values.ToList())
            };

            var json = JsonConvert.SerializeObject(collector,
                                                   Formatting.Indented,
                                                   new JsonSerializerSettings
                                                   {
                                                       NullValueHandling = NullValueHandling.Ignore
                                                   });
            PluginLog.Debug("Sending Data: \n" + json);

            Task.Factory.StartNew(async () =>
            {
                using var client = new HttpClient();
                try
                {
                    await client.PostAsync("https://necrolens.jusrv.de/api/import2",
                                           new StringContent(json, Encoding.UTF8, "application/json"));
                }
                catch (Exception e)
                {
                    PluginLog.Debug(e, "Failed to send data to server");
                }
            });
        }
    }

    public bool IsNextFloorWith(Pomander pomander)
    {
        return usedPomanders.Contains(pomander);
    }

    // 怪物静止计时器
    #region MobTimer
    public class MovementInfo(Vector3 Position, long Time, long Elasped)
    {
        public Vector3 Position = Position;
        public long LastSeen = Time;
        public long Elasped = Elasped;
    }

    private const long ThresholdMs = 5000; // 怪物变换到新位置所用时间一般小于5s
    private readonly Dictionary<uint, MovementInfo> mobMovement = [];
    public long GetTimeElapsedFromMovement(IGameObject obj)
    {
        var now = Environment.TickCount64;
        var position = obj.Position;
        if (mobMovement.TryGetValue(obj.EntityId, out var info))
        {
            if (Vector3.Distance(position, info.Position) <= 0.001)
            {
                // 没动，更新计时（若为未知则仍保持未知）
                if (info.Elasped >= 0)
                    info.Elasped += now - info.LastSeen;
            }
            else if (now - info.LastSeen < ThresholdMs)
            {
                // 一直在视野里/脱离视野的时间没超过阈值，位置发生改变，重置计时
                info.Position = position;
                info.Elasped = 0;
            }
            else
            {
                // 脱离视野的时间超过阈值，视作第一次看到它，不知道它已经保持静止了多久，计时用负数表示未知
                info.Position = position;
                info.Elasped = -1;
            }
            info.LastSeen = now;
        }
        else
        {
            // 第一次看到它，不知道它已经保持静止了多久，计时用负数表示未知
            info = new(position, now, -1);
            mobMovement.Add(obj.EntityId, info);
        }
        return info.Elasped;
    }

    public void RemoveTimedOutMob()
    {
        var now = Environment.TickCount64;
        var toRemove = new List<uint>();
        foreach (var mobMovement in mobMovement)
        {
            if (now - mobMovement.Value.LastSeen > 300000) // 5分钟
            {
                toRemove.Add(mobMovement.Key);
            }
        }
        foreach (var key in toRemove)
        {
            mobMovement.Remove(key);
        }
    }
    #endregion
}
