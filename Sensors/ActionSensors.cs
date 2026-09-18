using System;
using System.Collections.Generic;
using System.Linq;
using AliveNpcs.Models;
using AliveSensor.Core;
using AliveSensor.Memory;
using AliveSensor.World;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Utilities;
using StardewValley;
using SObject = StardewValley.Object;

namespace AliveSensor.Sensors;

/// <summary>S2 — passing out away from home: prefix on Farmer.passOutFromTired, the only pass-out path (2am or exhaustion).</summary>
internal sealed class PassOutSensor
{
    private static PassOutSensor? _instance;
    private readonly ModEntry _mod;

    public PassOutSensor(ModEntry mod) => _mod = mod;

    public bool Apply(Harmony harmony)
    {
        _instance = this;
        var target = AccessTools.Method(typeof(Farmer), nameof(Farmer.passOutFromTired), new[] { typeof(Farmer) });
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(PassOutSensor), nameof(Prefix)));
        _mod.Log.Debug("Harmony", "Patched Farmer.passOutFromTired (pass-out sensor).");
        return true;
    }

    private static void Prefix(Farmer who)
    {
        try
        {
            _instance?.OnPassOut(who);
        }
        catch (Exception ex)
        {
            _instance?._mod.Log.Error("Pass-out sensor failed", ex);
        }
    }

    private void OnPassOut(Farmer who)
    {
        if (!who.IsLocalPlayer || !_mod.Config.Sensors.SleepOutside || !_mod.Recorder.CanRecord("PassOut", out _))
            return;

        GameLocation location = who.currentLocation;
        string reason = Game1.timeOfDay >= 2600 ? "2am" : "exhaustion";
        _mod.Log.Debug("Sensor:PassOut", $"{who.Name} is passing out at {location?.NameOrUniqueName} ({who.TilePoint.X},{who.TilePoint.Y}); reason {reason}; stamina {who.Stamina:0}.");
        if (location is null || Homes.IsPlayerOwned(location))
        {
            _mod.Log.Debug("Sensor:PassOut", "At home or on the farm: not recorded.");
            return;
        }

        var draft = new EventDraft(EventTypes.SleepOutside, "PassOut", location, who.TilePoint);
        draft.Payload["reason"] = reason;
        var near = _mod.Landmarks.Nearest(location, who.TilePoint);
        if (near is not null && near.Value.Phrase.StartsWith("near the entrance to", StringComparison.Ordinal) && near.Value.Tiles <= 6 && near.Value.Phrase.Contains(" home)"))
            draft.Aggravators.Add("sleepNearHome");
        _mod.Recorder.Record(draft);
    }
}

/// <summary>S6b — turned away at a bedroom door: postfix on GameLocation.ShowLockedDoorMessage.</summary>
internal sealed class LockedDoorSensor
{
    private static LockedDoorSensor? _instance;
    private readonly ModEntry _mod;
    private readonly Dictionary<string, DateTime> _lastByDoor = new();

    public LockedDoorSensor(ModEntry mod) => _mod = mod;

    public bool Apply(Harmony harmony)
    {
        _instance = this;
        var target = AccessTools.Method(typeof(GameLocation), nameof(GameLocation.ShowLockedDoorMessage), new[] { typeof(string[]) });
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(LockedDoorSensor), nameof(Postfix)));
        _mod.Log.Debug("Harmony", "Patched GameLocation.ShowLockedDoorMessage (locked bedroom door sensor).");
        return true;
    }

    private static void Postfix(GameLocation __instance, string[] action)
    {
        try
        {
            _instance?.OnLocked(__instance, action);
        }
        catch (Exception ex)
        {
            _instance?._mod.Log.Error("Locked door sensor failed", ex);
        }
    }

    private void OnLocked(GameLocation location, string[] action)
    {
        if (action is null || action.Length < 2 || !_mod.Config.Sensors.LockedDoor || !_mod.Recorder.CanRecord("LockedDoor", out _))
            return;

        var owners = action.Skip(1).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        string key = $"{location.NameOrUniqueName}|{string.Join("+", owners)}";
        if (_lastByDoor.TryGetValue(key, out DateTime last) && (DateTime.UtcNow - last).TotalSeconds < _mod.Config.World.LockedDoorCooldownSeconds)
        {
            _mod.Log.Debug("Sensor:LockedDoor", $"{key}: repeated message within {_mod.Config.World.LockedDoorCooldownSeconds}s (real time); ignored.");
            return;
        }
        _lastByDoor[key] = DateTime.UtcNow;

        Farmer player = Game1.player;
        int attempts = _mod.Store.IncrementCounter(SDate.Now().DaysSinceStart, "_player", "locked:" + key);
        _mod.Log.Debug("Sensor:LockedDoor", $"{player.Name} was turned away at {string.Join("+", owners)}'s bedroom door in {location.NameOrUniqueName}; attempt {attempts} today.");

        var draft = new EventDraft(EventTypes.RoomLocked, "LockedDoor", location, player.TilePoint) { Target = owners.FirstOrDefault() };
        draft.Involved.AddRange(owners.Skip(1));
        draft.MentionedNpcs.AddRange(owners);
        draft.Payload["owners"] = string.Join(",", owners);
        draft.Payload["attempt"] = attempts.ToString();
        foreach (string owner in owners)
            draft.Direct.Add(owner);
        if (attempts >= 2)
            draft.Aggravators.Add("lockedRepeated");

        // Trying the same door again today updates the same memory instead of adding another one.
        int today = SDate.Now().DaysSinceStart;
        string ownersKey = draft.Payload["owners"];
        MemoryEvent? previous = _mod.Store.FindLast(e =>
            e.Type == EventTypes.RoomLocked && e.TotalDay == today && e.Location == location.NameOrUniqueName
            && e.Payload.GetValueOrDefault("owners") == ownersKey);
        if (previous is not null)
        {
            var fresh = _mod.Recorder.ResolveOnly(draft, $"{previous.Id}+{Game1.ticks}", out _, previous);
            float grade = Grading.Compute(_mod.Config, draft.Type, Game1.timeOfDay, draft.Aggravators, out var applied);
            _mod.Store.Update(previous.Id, e =>
            {
                e.Payload["attempt"] = attempts.ToString();
                e.Payload["lastTime"] = Game1.timeOfDay.ToString();
                if (grade > e.Grade)
                {
                    e.Grade = grade;
                    e.Aggravators = applied;
                }
                foreach (var pair in fresh)
                {
                    // Count is how many of today's attempts this witness saw.
                    if (!e.Witnesses.TryGetValue(pair.Key, out var old))
                    {
                        pair.Value.Count = 1;
                        e.Witnesses[pair.Key] = pair.Value;
                        continue;
                    }
                    int seen = Math.Max(old.Count, 1) + 1;
                    if (old.Visibility < pair.Value.Visibility)
                        e.Witnesses[pair.Key] = pair.Value;
                    e.Witnesses[pair.Key].Count = seen;
                }
                _mod.Recorder.CaptureNames(e);
            }, $"attempt #{attempts} at the same door");
            return;
        }

        draft.PerWitness = (_, w) => w.Count = 1;
        _mod.Recorder.Record(draft);
    }
}

/// <summary>S7 — talking to someone: postfix on AliveNpcs' ResponseFlowController.ApplyReaction (main thread).</summary>
internal sealed class TalkSensor
{
    private static TalkSensor? _instance;
    private readonly ModEntry _mod;

    private static readonly string[] RomanticWords =
    {
        "love", "date", "kiss", "crush", "marry", "beautiful", "cute", "flirt", "romantic",
        "amor", "beijo", "beijar", "namor", "casar", "apaixon", "linda", "lindo", "gata", "gato", "fofa", "flert",
    };

    public TalkSensor(ModEntry mod) => _mod = mod;

    public bool Apply(Harmony harmony)
    {
        _instance = this;
        var target = AccessTools.Method(Integration.AliveNpcsBridge.ApplyReactionMethod);
        if (target is null)
        {
            _mod.Log.Warn("AliveNpcs' ApplyReaction was not found: the conversation sensor is off.");
            return false;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(TalkSensor), nameof(Postfix)));
        _mod.Log.Debug("Harmony", "Patched AliveNpcs ResponseFlowController.ApplyReaction (conversation sensor).");
        return true;
    }

    private static void Postfix(NPC npc, string response, bool keepSecret, NpcReactionResult reaction)
    {
        try
        {
            _instance?.OnTalk(npc, response, keepSecret, reaction);
        }
        catch (Exception ex)
        {
            _instance?._mod.Log.Error("Conversation sensor failed", ex);
        }
    }

    private void OnTalk(NPC npc, string response, bool keepSecret, NpcReactionResult reaction)
    {
        if (npc is null || !_mod.Config.Sensors.Talk || !_mod.Recorder.CanRecord("Talk", out _))
            return;

        Farmer player = Game1.player;
        GameLocation location = npc.currentLocation ?? player.currentLocation;
        var talk = _mod.Config.Talk;
        string said = Text.Clip(response, talk.SnippetFullChars);
        string replied = Text.Clip(reaction?.Reaction, talk.SnippetFullChars);
        string topic = Text.Clip(response, talk.SnippetTopicChars);
        string emotion = reaction?.Emotion ?? "neutral";
        bool romantic = RomanticWords.Any(w => (response ?? "").Contains(w, StringComparison.OrdinalIgnoreCase) || (reaction?.Reaction ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));

        _mod.Log.Debug("Sensor:Talk", $"{player.Name} → {npc.Name} at {location.NameOrUniqueName}: \"{said}\" / \"{replied}\" (emotion {emotion}, secret {keepSecret}, romantic {romantic}).");

        var draft = new EventDraft(EventTypes.Talk, "Talk", location, player.TilePoint) { Target = npc.Name };
        draft.Exclude.Add(npc.Name);
        draft.MentionedNpcs.Add(npc.Name);

        int today = SDate.Now().DaysSinceStart;
        int mergeMinutes = _mod.Config.World.TalkMergeMinutes;
        MemoryEvent? previous = mergeMinutes <= 0 ? null : _mod.Store.FindLast(e =>
            e.Type == EventTypes.Talk && e.Target == npc.Name && e.TotalDay == today && e.Location == location.NameOrUniqueName
            && GameClock.MinutesBetween(e.Time, Game1.timeOfDay) <= mergeMinutes);

        if (previous is not null)
        {
            var fresh = _mod.Recorder.ResolveOnly(draft, previous.Id + "+" + Game1.timeOfDay, out _, previous);
            _mod.Store.Update(previous.Id, e =>
            {
                int turns = int.TryParse(e.Payload.GetValueOrDefault("turns"), out int t) ? t + 1 : 2;
                e.Payload["turns"] = turns.ToString();
                e.Payload["said"] = said;
                e.Payload["replied"] = replied;
                e.Payload["emotion"] = emotion;
                if (keepSecret) e.Payload["secret"] = "true";
                if (romantic && !e.Aggravators.Any(a => a.StartsWith("talkRomantic")))
                    AddAggravator(e, "talkRomantic");
                if ((emotion is "angry" or "sad") && !e.Aggravators.Any(a => a.StartsWith("talkNegativeEmotion")))
                    AddAggravator(e, "talkNegativeEmotion");
                foreach (var pair in fresh)
                {
                    if (!e.Witnesses.TryGetValue(pair.Key, out var old) || old.Visibility < pair.Value.Visibility)
                        e.Witnesses[pair.Key] = pair.Value;
                }
                _mod.Recorder.CaptureNames(e);
            }, $"conversation turn with {npc.Name}");
            return;
        }

        draft.Payload["said"] = said;
        draft.Payload["replied"] = replied;
        draft.Payload["topic"] = topic;
        draft.Payload["emotion"] = emotion;
        draft.Payload["turns"] = "1";
        if (keepSecret)
            draft.Payload["secret"] = "true";
        if (romantic)
            draft.Aggravators.Add("talkRomantic");
        if (emotion is "angry" or "sad")
            draft.Aggravators.Add("talkNegativeEmotion");
        _mod.Recorder.Record(draft);
    }

    private void AddAggravator(MemoryEvent e, string key)
    {
        if (!_mod.Config.Grade.Aggravators.TryGetValue(key, out float multiplier))
            return;
        e.Grade *= multiplier;
        e.Aggravators.Add($"{key}×{multiplier:0.##}");
    }
}

/// <summary>S1 bomb, S3 gift, S9 consume — driven by SpaceCore events.</summary>
internal sealed class SpaceCoreSensors
{
    private readonly ModEntry _mod;

    public SpaceCoreSensors(ModEntry mod) => _mod = mod;

    public void OnGift(Farmer farmer, NPC npc, SObject gift)
    {
        if (farmer is null || !farmer.IsLocalPlayer || npc is null || gift is null || !_mod.Config.Sensors.Gift || !_mod.Recorder.CanRecord("Gift", out _))
            return;

        GameLocation location = npc.currentLocation ?? farmer.currentLocation;
        int tasteCode = npc.getGiftTasteForThisItem(gift);
        string taste = tasteCode switch
        {
            NPC.gift_taste_love or NPC.gift_taste_stardroptea => "loved",
            NPC.gift_taste_like => "liked",
            NPC.gift_taste_dislike => "disliked",
            NPC.gift_taste_hate => "hated",
            _ => "neutral",
        };
        bool birthday = npc.isBirthday();

        string? partner = null;
        foreach (var pair in farmer.friendshipData.Pairs)
        {
            if (pair.Key == npc.Name || !(pair.Value.IsDating() || pair.Value.IsMarried() || pair.Key == farmer.spouse))
                continue;
            NPC? candidate = location.characters.FirstOrDefault(n => n.Name == pair.Key);
            if (candidate is not null && Vector2.Distance(candidate.Tile, farmer.Tile) <= _mod.Config.Grade.PartnerNearbyTiles)
            {
                partner = pair.Key;
                break;
            }
        }

        _mod.Log.Debug("Sensor:Gift", $"{farmer.Name} gave {npc.Name} {gift.DisplayName} ({gift.QualifiedItemId}) at {location.NameOrUniqueName}: {taste}{(birthday ? ", birthday" : "")}{(partner is null ? "" : $", partner {partner} nearby")}.");

        var draft = new EventDraft(EventTypes.Gift, "Gift", location, farmer.TilePoint) { Target = npc.Name };
        draft.Direct.Add(npc.Name);
        draft.MentionedNpcs.Add(npc.Name);
        draft.Payload["item"] = gift.DisplayName;
        draft.Payload["itemId"] = gift.QualifiedItemId;
        draft.Payload["taste"] = taste;
        if (birthday)
        {
            draft.Payload["birthday"] = "true";
            draft.Aggravators.Add("giftBirthday");
        }
        if (taste is "loved" or "hated")
            draft.Aggravators.Add("giftLovedOrHated");
        if (partner is not null)
        {
            draft.Payload["partner"] = partner;
            draft.Involved.Add(partner);
            draft.MentionedNpcs.Add(partner);
            draft.Aggravators.Add("giftPartnerNearby");
        }
        _mod.Recorder.Record(draft);
    }

    public void OnEaten(Farmer farmer)
    {
        if (farmer is null || !farmer.IsLocalPlayer || farmer.itemToEat is not SObject item || !_mod.Config.Sensors.Consume || !_mod.Recorder.CanRecord("Consume", out _))
            return;

        GameLocation location = farmer.currentLocation;
        bool drink = Game1.objectData.TryGetValue(item.ItemId, out var data) && data.IsDrink;
        bool alcohol = ConsumeSensor.IsAlcohol(_mod, item);
        bool outsideSaloon = alcohol && location.Name != "Saloon";
        _mod.Log.Debug("Sensor:Consume", $"{farmer.Name} {(drink ? "drank" : "ate")} {item.DisplayName} ({item.QualifiedItemId}) at {location.NameOrUniqueName}{(alcohol ? " [alcohol]" : "")}{(outsideSaloon ? " outside the Saloon" : "")}.");

        var draft = new EventDraft(EventTypes.Consume, "Consume", location, farmer.TilePoint);
        draft.Payload["item"] = item.DisplayName;
        draft.Payload["itemId"] = item.QualifiedItemId;
        draft.Payload["drink"] = drink ? "true" : "false";
        draft.Payload["kind"] = alcohol ? "alcohol" : drink ? "drink" : "food";
        if (alcohol)
            draft.Payload["alcohol"] = "true";
        if (outsideSaloon)
            draft.Aggravators.Add("drinkOutsideSaloon");

        draft.Payload["count"] = "1";

        int today = SDate.Now().DaysSinceStart;
        int threshold = _mod.Config.Grade.ManyDrinksThreshold;
        void CountDrink(string npc, WitnessRecord w)
        {
            if (!alcohol)
            {
                w.Count = Math.Max(w.Count, 1);
                return;
            }
            w.Count = _mod.Store.IncrementCounter(today, npc, "alcoholSeen");
            if (w.Count >= threshold && !w.BonusReasons.Any(r => r.StartsWith("manyDrinksSeen")))
                Grading.ApplyBonus(_mod.Config, w, "manyDrinksSeen");
        }

        // Repeating the same item in the same place becomes one memory with a count.
        int mergeMinutes = _mod.Config.World.ConsumeMergeMinutes;
        MemoryEvent? previous = mergeMinutes <= 0 ? null : _mod.Store.FindLast(e =>
            e.Type == EventTypes.Consume && e.TotalDay == today && e.Location == location.NameOrUniqueName
            && e.Payload.GetValueOrDefault("itemId") == item.QualifiedItemId
            && GameClock.MinutesBetween(e.Time, Game1.timeOfDay) <= mergeMinutes);
        if (previous is not null)
        {
            var fresh = _mod.Recorder.ResolveOnly(draft, $"{previous.Id}+{Game1.ticks}", out _, previous);
            foreach (var pair in fresh)
            {
                if (previous.Witnesses.TryGetValue(pair.Key, out var old))
                {
                    pair.Value.Bonus = old.Bonus;
                    pair.Value.BonusReasons = new List<string>(old.BonusReasons);
                }
                if (alcohol)
                    CountDrink(pair.Key, pair.Value);
                else
                    pair.Value.Count = previous.Witnesses.TryGetValue(pair.Key, out var seenBefore) ? Math.Max(seenBefore.Count, 1) + 1 : 1;
            }
            _mod.Store.Update(previous.Id, e =>
            {
                int count = int.TryParse(e.Payload.GetValueOrDefault("count"), out int c) ? c + 1 : 2;
                e.Payload["count"] = count.ToString();
                e.Payload["lastTime"] = Game1.timeOfDay.ToString();
                foreach (var pair in fresh)
                {
                    if (!e.Witnesses.TryGetValue(pair.Key, out var old) || old.Visibility <= pair.Value.Visibility || old.Count < pair.Value.Count)
                    {
                        if (old is not null && old.Visibility > pair.Value.Visibility)
                        {
                            pair.Value.Visibility = old.Visibility;
                            pair.Value.Level = old.Level;
                            pair.Value.Distance = old.Distance;
                        }
                        e.Witnesses[pair.Key] = pair.Value;
                    }
                }
                _mod.Recorder.CaptureNames(e);
            }, $"{item.DisplayName} #{int.Parse(previous.Payload.GetValueOrDefault("count", "1")) + 1} in a row");
            return;
        }

        draft.PerWitness = CountDrink;
        _mod.Recorder.Record(draft);
    }

    public void OnBomb(Farmer? who, Vector2 tile, int radius)
    {
        if (who is null || !who.IsLocalPlayer || !_mod.Config.Sensors.Bomb || !_mod.Recorder.CanRecord("Bomb", out _))
            return;

        GameLocation location = who.currentLocation;
        var point = new Point((int)tile.X, (int)tile.Y);
        _mod.Log.Debug("Sensor:Bomb", $"Explosion by {who.Name} at {location.NameOrUniqueName} ({point.X},{point.Y}), radius {radius}.");

        var draft = new EventDraft(EventTypes.Bomb, "Bomb", location, point);
        draft.Payload["radius"] = radius.ToString();
        draft.Payload["count"] = "1";

        // A burst of explosions in the same area becomes one memory with a count.
        int today = SDate.Now().DaysSinceStart;
        int mergeMinutes = _mod.Config.World.BombMergeMinutes;
        MemoryEvent? previous = mergeMinutes <= 0 ? null : _mod.Store.FindLast(e =>
            e.Type == EventTypes.Bomb && e.TotalDay == today && e.Location == location.NameOrUniqueName
            && GameClock.MinutesBetween(e.Time, Game1.timeOfDay) <= mergeMinutes
            && Vector2.Distance(new Vector2(e.TileX, e.TileY), tile) <= _mod.Config.World.BombMergeTiles);
        if (previous is not null)
        {
            var fresh = _mod.Recorder.ResolveOnly(draft, $"{previous.Id}+{Game1.ticks}", out _, previous);
            _mod.Store.Update(previous.Id, e =>
            {
                int count = int.TryParse(e.Payload.GetValueOrDefault("count"), out int c) ? c + 1 : 2;
                e.Payload["count"] = count.ToString();
                e.Payload["lastTime"] = Game1.timeOfDay.ToString();
                foreach (var pair in fresh)
                {
                    // Count is how many explosions of this burst this witness noticed.
                    if (!e.Witnesses.TryGetValue(pair.Key, out var old))
                    {
                        pair.Value.Count = 1;
                        e.Witnesses[pair.Key] = pair.Value;
                        continue;
                    }
                    int seen = Math.Max(old.Count, 1) + 1;
                    if (old.Visibility < pair.Value.Visibility)
                    {
                        pair.Value.Bonus = old.Bonus;
                        pair.Value.BonusReasons = new List<string>(old.BonusReasons);
                        e.Witnesses[pair.Key] = pair.Value;
                    }
                    e.Witnesses[pair.Key].Count = seen;
                }
                _mod.Recorder.CaptureNames(e);
            }, $"explosion #{int.Parse(previous.Payload.GetValueOrDefault("count", "1")) + 1} in the same burst");
            return;
        }
        var near = _mod.Landmarks.Nearest(location, point);
        if (near is not null && near.Value.Tiles <= 10 && near.Value.Phrase.Contains(" home)"))
            draft.Aggravators.Add("bombNearHome");
        draft.PerWitness = (_, w) => w.Count = 1;
        _mod.Recorder.Record(draft);
    }
}

internal static class ConsumeSensor
{
    public static bool IsAlcohol(ModEntry mod, Item item)
    {
        var ids = mod.Config.World.AlcoholItemIds;
        return ids.Contains(item.ItemId) || ids.Contains(item.QualifiedItemId);
    }
}

/// <summary>
/// S14 — dating and marriage proposals: a bouquet ("(O)458") or mermaid pendant ("(O)460") handed to an NPC.
/// Prefix+postfix on NPC.tryToReceiveActiveObject, the one method that decides both — vanilla never raises an
/// event for either, so this is the only way to know an answer was given, let alone what it was.
/// </summary>
internal sealed class ProposalSensor
{
    private const string BouquetId = "(O)458";
    private const string PendantId = "(O)460";

    private static ProposalSensor? _instance;
    private readonly ModEntry _mod;

    public ProposalSensor(ModEntry mod) => _mod = mod;

    /// <summary>What the prefix saw before the vanilla method ran, so the postfix can tell what changed.</summary>
    private sealed class Capture
    {
        public string ItemId = "";
        public FriendshipStatus? Before;
    }

    public bool Apply(Harmony harmony)
    {
        _instance = this;
        var target = AccessTools.Method(typeof(NPC), nameof(NPC.tryToReceiveActiveObject), new[] { typeof(Farmer), typeof(bool) });
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(ProposalSensor), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(ProposalSensor), nameof(Postfix)));
        _mod.Log.Debug("Harmony", "Patched NPC.tryToReceiveActiveObject (dating/marriage proposal sensor).");
        return true;
    }

    private static void Prefix(NPC __instance, Farmer who, bool probe, out Capture? __state)
    {
        __state = null;
        try
        {
            // probe=true is just the game asking "would this do anything" (e.g. cursor hover); nothing happens.
            string? itemId = who?.ActiveObject?.QualifiedItemId;
            if (probe || itemId is not (BouquetId or PendantId))
                return;
            FriendshipStatus? before = who!.friendshipData.TryGetValue(__instance.Name, out Friendship? f) ? f.Status : (FriendshipStatus?)null;
            __state = new Capture { ItemId = itemId, Before = before };
        }
        catch (Exception ex)
        {
            _instance?._mod.Log.Error("Proposal sensor prefix failed", ex);
        }
    }

    private static void Postfix(NPC __instance, Farmer who, bool __result, Capture? __state)
    {
        // __result is vanilla's own "did tryToReceiveActiveObject handle this at all" — false means the item
        // wasn't a bouquet/pendant after all (shouldn't happen given the prefix's check, but cheap to guard).
        if (__state is null || !__result)
            return;
        try
        {
            _instance?.OnProposal(__instance, who, __state);
        }
        catch (Exception ex)
        {
            _instance?._mod.Log.Error("Proposal sensor failed", ex);
        }
    }

    private void OnProposal(NPC npc, Farmer who, Capture capture)
    {
        if (!who.IsLocalPlayer || !_mod.Config.Sensors.Proposal || !_mod.Recorder.CanRecord("Proposal", out _))
            return;

        bool isMarriage = capture.ItemId == PendantId;
        FriendshipStatus wants = isMarriage ? FriendshipStatus.Engaged : FriendshipStatus.Dating;
        FriendshipStatus? after = who.friendshipData.TryGetValue(npc.Name, out Friendship? f) ? f.Status : (FriendshipStatus?)null;
        bool accepted = after == wants && capture.Before != wants;

        string type = isMarriage ? EventTypes.MarriageProposal : EventTypes.DatingProposal;
        var draft = new EventDraft(type, "Proposal", npc.currentLocation, npc.TilePoint) { Target = npc.Name };
        draft.Direct.Add(npc.Name);
        draft.Payload["result"] = accepted ? "accepted" : "rejected";
        if (!accepted)
            draft.Payload["reason"] = RejectionReason(npc, who, isMarriage, capture.Before);

        _mod.Log.Debug("Sensor:Proposal", $"{who.Name} {(isMarriage ? "proposed marriage to" : "asked to date")} {npc.Name} @ {npc.currentLocation?.NameOrUniqueName}: "
            + $"{draft.Payload["result"]}{(draft.Payload.TryGetValue("reason", out string? r) ? $" ({r})" : "")}.");
        _mod.Recorder.Record(draft);
    }

    /// <summary>
    /// A best-effort read of why it was turned down, from the same public state vanilla's own rejection
    /// dialogue branches on (NPC.tryToReceiveActiveObject). Good enough to colour the wording; not meant to
    /// reproduce every one of vanilla's dialogue variants.
    /// </summary>
    private static string RejectionReason(NPC npc, Farmer who, bool isMarriage, FriendshipStatus? before)
    {
        if (!npc.datable.Value)
            return "not_datable";
        if (npc.isMarriedOrEngaged())
            return "already_committed";
        if (before is FriendshipStatus.Divorced)
            return "divorced";
        if (isMarriage && before != FriendshipStatus.Dating)
            return "not_dating_yet";
        if (before is FriendshipStatus.Dating or FriendshipStatus.Engaged)
            return "already_together";
        if (isMarriage && who.HouseUpgradeLevel < 1)
            return "house_too_small";
        return "too_soon";
    }
}
