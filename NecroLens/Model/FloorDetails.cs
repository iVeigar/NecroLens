using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using NecroLens.util;
using Newtonsoft.Json;
using static NecroLens.util.DeepDungeonUtil;

namespace NecroLens.Model;

public partial class FloorDetails
{
    public Dictionary<uint, (DeepDungeonItemKind, int)> DoubleChests { get; private set; } = [];
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
        mobIdleStatusTracker.Clear();
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

    public void OnDemicloneUsed(Demiclone demiclone)
    {
        PluginLog.Debug($"Demiclone ID: {demiclone}");
        if (demiclone == Demiclone.MazerootIncense)
        {
            FloorEffects.Add(Pomander.Sight);
            usedPomanders.Add(Pomander.Sight);
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
    #region Mob Idle Status Tracker

    public class MobIdleStatus(Vector3 position, long time)
    {
        public Vector3 LastSeenPosition = position; // 上次看到的位置
        public long LastSeenTime = time; // 上次看到的时刻
        public long Elasped = -1; // 静止状态已经持续的时间，负数表示未知，0表示移动中
    }

    private readonly Dictionary<uint, MobIdleStatus> mobIdleStatusTracker = [];
    // 怪物的移动时间一般小于5秒，脱离视野的时间超过这个值后，再次进入视野时若为静止状态且位置变化，无法得知静止了多久
    private const long IdleRevalidationThreshold = 5000; 
    public unsafe void TickIdleStatus(IGameObject obj)
    {
        if (!obj.IsValid() || obj is not IBattleChara) return;
        var now = Environment.TickCount64;
        var position = obj.Position;
        // 怪物模型timelineId，0为刚进入视野时的值, 3为静止 13为走路
        var timelineId = ((BattleChara*)obj.Address)->Timeline.TimelineSequencer.TimelineIds[0];
        
        // 注: 怪物被其他怪物挤到而被动移动时，position会变，timelineId会从3变为13，rotation也有概率会变，因此无法简单地区分出主动的随机游走和受碰撞产生的被动移动
        if (mobIdleStatusTracker.TryGetValue(obj.EntityId, out var status))
        {
            if (Vector3.Distance(position, status.LastSeenPosition) < 0.01f)
            {
                if (status.Elasped >= 0)
                    status.Elasped += now - status.LastSeenTime;
            }
            else
            {
                if (now - status.LastSeenTime < IdleRevalidationThreshold) // 脱离视野的时间未超过阈值，或者一直在视野内
                {
                    if (timelineId == 0) // 刚进入视野，延迟到下一帧再判定
                        return;

                    if (timelineId == 3) // 期间有移动过，但当前为静止，静止时长取值区间为 (0 ~ now-status.LastSeenTime)，保守起见取最大值
                        status.Elasped = now - status.LastSeenTime;
                    else // 非静止状态
                        status.Elasped = 0;
                }
                else // 超时，设为未知。如果它正在移动那么下一帧就会变为0
                    status.Elasped = -1;
                status.LastSeenPosition = position;
            }
            status.LastSeenTime = now;
        }
        else
        {
            // 第一次看到
            mobIdleStatusTracker.Add(obj.EntityId, new(position, now));
        }
    }

    public long GetIdleTimeElapsed(IGameObject obj)
    {
        return mobIdleStatusTracker.GetOrDefault(obj.EntityId)?.Elasped ?? -1;
    }

    public void PruneIdleStatusTracker()
    {
        if (!EzThrottler.Throttle("PruneIdleStatusTracker", 1000))
            return;
        var now = Environment.TickCount64;
        var toRemove = new List<uint>();
        foreach (var (entityId, status) in mobIdleStatusTracker)
        {
            if (now - status.LastSeenTime > 300000) // 5 minutes no see
            {
                toRemove.Add(entityId);
            }
        }
        foreach (var entityId in toRemove)
        {
            mobIdleStatusTracker.Remove(entityId);
        }
    }
    #endregion
}
