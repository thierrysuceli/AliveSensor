using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Memory;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace AliveSensor.Delivery;

/// <summary>
/// What the village feels about the farmer, and when someone finally says it out loud.
///
/// Every witnessed memory feeds one or more meters for each witness — the weights come from the event catalog and
/// its rules, so the same round of drinks can amuse Shane, please Gus and worry Penny. Each meter fills, fades on
/// its own clock, and passes levels that make its NPC want a word.
///
/// Only one NPC can hold a conversation at a time, so when several are ready the turn goes to the best
/// arbitration score: how close they are × how strongly they feel × how much that feeling weighs. One event can
/// only send a limited number of people over (a token budget as much as a design choice); everyone else who
/// reached a level keeps it as an aside, brought up the next time the farmer talks to them, which costs nothing.
///
/// Bookkeeping runs on the main thread; only <see cref="ReasonFor"/> is read from AliveNpcs' background thread.
/// </summary>
internal sealed class FeelingEngine
{
    /// <summary>One memory's contribution to one meter, at the moment it landed.</summary>
    private sealed class Contribution
    {
        public string EventId = "";
        public double Amount;
        public int Minute;
    }

    /// <summary>One NPC's standing in one feeling.</summary>
    private sealed class MeterState
    {
        public readonly List<Contribution> Contributions = new();
        /// <summary>eventId → score already counted, so a memory that grows only adds the difference.</summary>
        public readonly Dictionary<string, double> Counted = new(StringComparer.Ordinal);
        /// <summary>Highest level this NPC has already acted on today.</summary>
        public int ActedTier;
        /// <summary>Carried into tomorrow when the top level fired.</summary>
        public double Grudge;
        /// <summary>Waiting to be handed over when they speak — a round on the house, say.</summary>
        public RuleReward? Reward;
    }

    /// <summary>An NPC with something to say, waiting for their turn.</summary>
    private sealed class Signal
    {
        public string Npc = "";
        public string Meter = "";
        public int Tier;
        public List<string> EventIds = new();
        public string Reason = "";
        public int SinceMinute;
        public int LastEmoteTick = int.MinValue / 2;
        public string LastWait = "";
        /// <summary>-1 = waiting their turn; ≥0 = the chosen one, counting down to opening.</summary>
        public int TriggerTick = -1;
        public RuleReward? Reward;

        public string Key => $"{Npc}|{Meter}";
    }

    private readonly ModEntry _mod;
    private readonly Dictionary<string, Dictionary<string, MeterState>> _state = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>npc → event type → the one meter that gets to react to that kind of event for the rest of today.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _typeLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Signal> _signals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Text, int ExpiresMinute, IReadOnlyList<string> EventIds)> _reasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Text, int ExpiresMinute, IReadOnlyList<string> EventIds)> _asides = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The one signal actually mid-approach right now.</summary>
    private string? _openingKey;
    private int _lastReactionMinute = int.MinValue / 2;
    private volatile int _nowMinute;

    public FeelingEngine(ModEntry mod) => _mod = mod;

    private ModConfig C => _mod.Config;
    private Log Log => _mod.Log;
    private EventCatalog Catalog => _mod.Catalog;

    public static int NowMinute() => SDate.Now().DaysSinceStart * 1440 + GameClock.ToMinutes(Game1.timeOfDay);

    /// <summary>The NPC approaching right now, if any; otherwise anyone still waiting (for as_status).</summary>
    public string? PendingNpc => _openingKey is not null && _signals.TryGetValue(_openingKey, out Signal? opening)
        ? opening.Npc
        : _signals.Values.FirstOrDefault()?.Npc;

    // ── lifecycle ──

    public void Reset(string why)
    {
        _state.Clear();
        _typeLocks.Clear();
        _signals.Clear();
        _reasons.Clear();
        _asides.Clear();
        _openingKey = null;
        _lastReactionMinute = int.MinValue / 2;
        Log.Debug("Feelings", $"All meters reset ({why}).");
    }

    /// <summary>A new day: meters start over, except what each feeling says it carries overnight.</summary>
    public void OnDayStarted()
    {
        var carry = new List<(string Npc, string Meter, double Amount)>();
        foreach (var (npc, meters) in _state)
        {
            foreach (var (meterId, state) in meters)
            {
                if (state.Grudge >= 0.5)
                    carry.Add((npc, meterId, state.Grudge));
            }
        }

        Reset("new day");
        if (carry.Count == 0)
            return;

        int now = NowMinute();
        foreach (var (npc, meterId, amount) in carry)
        {
            For(npc, meterId).Contributions.Add(new Contribution
            {
                EventId = $"grudge-day{SDate.Now().DaysSinceStart}",
                Amount = amount,
                Minute = now,
            });
            Log.Debug("Feelings", $"{npc} wakes up still feeling it: {meterId} +{amount:0.0} carried over → {Reading(npc, meterId, now):0.0}.");
        }
    }

    // ── filling the meters ──

    /// <summary>Store hook: a memory was recorded or grew. Adds each witness's feelings about it.</summary>
    public void OnMemoryChanged(MemoryEvent memory)
    {
        try
        {
            if (!C.Enabled || !C.Confront.Enabled || !Context.IsWorldReady)
                return;

            int now = NowMinute();
            SDate today = SDate.Now();
            var reached = new List<Signal>();

            foreach (var (npc, witness) in memory.Witnesses)
            {
                if (witness.Meters.Count == 0)
                    continue;

                ScoredMemory? scored = _mod.Selector.ScoreAll(new[] { memory }, npc, today.DaysSinceStart, Game1.timeOfDay).FirstOrDefault();
                if (scored is null)
                    continue;

                // Once this event type has made them feel something today, that's the feeling it keeps giving
                // them — a joke doesn't turn into a grudge three drinks later. Whichever meter gets there first
                // (today, for this event type) is the one that gets to grow; the rest sit this kind of event out.
                string? typeLock = TypeLock(npc, memory.Type);

                foreach (var (meterId, weight) in witness.Meters)
                {
                    if (weight <= 0 || !Catalog.TryGetMeter(meterId, out MeterDefinition meter))
                        continue;
                    if (typeLock is not null && !string.Equals(typeLock, meterId, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Verbose("Feelings", $"{npc}: {memory.Type} already settled as {typeLock} for today; {meterId} sits this one out.");
                        continue;
                    }

                    MeterState state = For(npc, meterId);
                    double score = scored.Score * weight;
                    double delta = score - state.Counted.GetValueOrDefault(memory.Id);
                    if (delta < 0.05)
                        continue;

                    state.Counted[memory.Id] = score;
                    state.Contributions.Add(new Contribution { EventId = memory.Id, Amount = delta, Minute = now });
                    if (RewardFor(memory, npc) is { } reward)
                        state.Reward = reward;

                    double reading = Reading(npc, meterId, now);
                    Log.Debug("Feelings", $"{npc} +{delta:0.0} {meterId} from {memory.Type} {memory.Id} (L{witness.Level}, weight {weight:0.##}) → {reading:0.0} (levels {string.Join("/", meter.Thresholds)}).");

                    if (typeLock is null && meter.TierFor(reading) >= 1)
                    {
                        typeLock = meterId;
                        SetTypeLock(npc, memory.Type, meterId);
                        Log.Debug("Feelings", $"{npc}: {memory.Type} is a {meterId} thing for them today — other feelings from this kind of event sit out the rest of the day.");
                    }

                    if (witness.Silent)
                        continue;

                    Signal? signal = Consider(npc, meter, witness, reading, now);
                    if (signal is not null)
                        reached.Add(signal);
                }
            }

            if (reached.Count > 0)
                Admit(reached, now);
        }
        catch (Exception ex)
        {
            Log.Error("Updating the feeling meters failed", ex);
        }
    }

    /// <summary>Whether this NPC has just reached a level of this feeling they haven't acted on yet.</summary>
    private Signal? Consider(string npc, MeterDefinition meter, WitnessRecord witness, double reading, int now)
    {
        MeterState state = For(npc, meter.Id);
        int tier = meter.TierFor(reading);
        if (tier <= state.ActedTier)
            return null;
        if (_signals.TryGetValue($"{npc}|{meter.Id}", out Signal? existing) && existing.Tier >= tier)
            return null;

        string blocked = DayBlocker(npc, meter, state);
        if (blocked.Length > 0)
        {
            Log.Debug("Feelings", $"{npc} reached {meter.Id} level {tier} ({reading:0.0}) but won't act on it today: {blocked}.");
            return null;
        }
        if (witness.Level < meter.MinCertainty)
        {
            // Too unsure to speak up about it, but it still sits with them.
            MaybeAside(npc, meter, tier, now, "not sure enough to raise it");
            return null;
        }

        return BuildSignal(npc, meter, tier, now, state);
    }

    /// <summary>
    /// Decide who actually gets to walk over. One event can only send a few people (each is an AI call); the rest
    /// keep what they felt as an aside for the next time the farmer talks to them.
    /// </summary>
    private void Admit(List<Signal> reached, int now)
    {
        int cap = Math.Max(0, C.Confront.MaxReactionsPerEvent);
        List<Signal> ranked = reached
            .OrderByDescending(signal => Arbitration(signal, now))
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
        {
            Signal signal = ranked[i];
            if (i < cap)
            {
                _signals[signal.Key] = signal;
                Log.Debug("Feelings", $"{signal.Npc} reached {signal.Meter} level {signal.Tier}/{Levels(signal.Meter)} ({Reading(signal.Npc, signal.Meter, now):0.0}): will show '{EmoteName(signal.Meter)}' and speak when it's their turn. Because of: {string.Join(", ", signal.EventIds)}.");
                continue;
            }

            // Over the cap: they felt it, but they'll mention it later instead of coming over now.
            if (Catalog.TryGetMeter(signal.Meter, out MeterDefinition meter))
                MaybeAside(signal.Npc, meter, signal.Tier, now, $"more than {cap} reacted to this");
        }
    }

    /// <summary>How much this NPC deserves the turn: how close × how strongly they feel × what the feeling weighs.</summary>
    private double Arbitration(Signal signal, int now)
    {
        if (!Catalog.TryGetMeter(signal.Meter, out MeterDefinition meter))
            return 0;
        double intensity = meter.Intensity(Reading(signal.Npc, signal.Meter, now), signal.Tier);
        return Proximity(signal.Npc) * intensity * meter.Priority;
    }

    /// <summary>1 next to the farmer, falling with distance, with a floor so someone relevant is never ruled out.</summary>
    private double Proximity(string npc)
    {
        NPC? character = Game1.getCharacterFromName(npc);
        Farmer player = Game1.player;
        if (character?.currentLocation is null || player?.currentLocation is null || character.currentLocation != player.currentLocation)
            return 0.1;
        double range = Math.Max(1, C.Confront.EmoteRangeTiles);
        double distance = Distance(character, player);
        return Math.Clamp(1 - (distance / range), 0.15, 1);
    }

    private Signal BuildSignal(string npc, MeterDefinition meter, int tier, int now, MeterState state)
    {
        List<string> ids = RankedEvents(npc, meter.Id, now);
        return new Signal
        {
            Npc = npc,
            Meter = meter.Id,
            Tier = tier,
            EventIds = ids,
            SinceMinute = now,
            Reason = BuildReason(npc, meter, tier, ids),
            Reward = state.Reward,
        };
    }

    // ── speaking up ──

    /// <summary>Main-thread tick: keep the waiting NPCs signalling, and drive whoever is approaching.</summary>
    public void Tick()
    {
        if (!C.Enabled || !C.Confront.Enabled || !Context.IsWorldReady)
            return;
        int now = NowMinute();
        _nowMinute = now;
        Farmer player = Game1.player;

        PruneAndSignal(now, player);

        if (_openingKey is null || _signals[_openingKey].TriggerTick < 0)
        {
            string? best = BestReady(now, player);
            if (best is not null && best != _openingKey)
            {
                if (_openingKey is not null && _signals.TryGetValue(_openingKey, out Signal? previous))
                    Log.Debug("Feelings", $"{_signals[best].Npc} takes the turn from {previous.Npc}: better claim to it right now.");
                _openingKey = best;
            }
        }
        if (_openingKey is null)
            return;

        Signal signal = _signals[_openingKey];
        NPC? npc = Game1.getCharacterFromName(signal.Npc);
        if (npc is null)
        {
            Drop(signal.Key);
            return;
        }

        if (signal.TriggerTick < 0)
        {
            npc.Halt();
            npc.movementPause = Math.Max(npc.movementPause, 5000);
            npc.faceTowardFarmerForPeriod(5000, 4, false, player);
            signal.TriggerTick = Game1.ticks + C.Confront.DelayTicks;
            Log.Debug("Feelings", $"{signal.Npc}: noticed the farmer ({Distance(npc, player):0.0} tiles) — opening about {signal.Meter} in {C.Confront.DelayTicks} ticks.");
            return;
        }
        if (Game1.ticks < signal.TriggerTick)
            return;

        string wait = WaitReason(npc, player, committed: true);
        if (wait.Length > 0)
        {
            LogWaitIfChanged(signal, wait);
            signal.TriggerTick = -1;
            return;
        }

        Start(signal, npc, player, now);
    }

    /// <summary>Drop what's gone stale, and keep every waiting NPC's bubble alive regardless of whose turn it is.</summary>
    private void PruneAndSignal(int now, Farmer player)
    {
        foreach (string key in _signals.Keys.ToList())
        {
            Signal signal = _signals[key];
            if (now - signal.SinceMinute > C.Confront.PendingExpiresMinutes)
            {
                Log.Debug("Feelings", $"{signal.Npc}: let go of it ({signal.Meter}) — the farmer never came close.");
                if (Catalog.TryGetMeter(signal.Meter, out MeterDefinition expired))
                    MaybeAside(signal.Npc, expired, signal.Tier, now, "gave up waiting");
                Drop(key);
                continue;
            }

            NPC? npc = Game1.getCharacterFromName(signal.Npc);
            if (npc is null)
            {
                Drop(key);
                continue;
            }
            if (!C.Confront.EmoteWhileWaiting || npc.currentLocation is null || npc.currentLocation != player.currentLocation
                || Game1.eventUp || Distance(npc, player) > C.Confront.EmoteRangeTiles
                || Game1.ticks - signal.LastEmoteTick < C.Confront.EmoteRepeatSeconds * 60)
                continue;

            bool first = signal.LastEmoteTick < 0;
            npc.doEmote(EmoteFor(signal.Meter));
            signal.LastEmoteTick = Game1.ticks;
            if (first)
                Log.Debug("Feelings", $"{signal.Npc}: showing '{EmoteName(signal.Meter)}' ({signal.Meter} level {signal.Tier}); repeats every {C.Confront.EmoteRepeatSeconds}s until it's their turn.");
        }
    }

    /// <summary>Among everyone waiting, the one who may speak now and has the best claim to the turn.</summary>
    private string? BestReady(int now, Farmer player)
    {
        string? best = null;
        double bestScore = -1;
        foreach (var (key, signal) in _signals)
        {
            if (!Catalog.TryGetMeter(signal.Meter, out MeterDefinition meter))
                continue;
            if (DayBlocker(signal.Npc, meter, For(signal.Npc, signal.Meter)).Length > 0)
                continue;
            if (CooldownBlocker(now).Length > 0)
                continue;
            NPC? npc = Game1.getCharacterFromName(signal.Npc);
            if (npc is null || WaitReason(npc, player, committed: false).Length > 0)
                continue;

            double score = Arbitration(signal, now);
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = key;
        }
        return best;
    }

    private void Start(Signal signal, NPC npc, Farmer player, int now)
    {
        if (!Catalog.TryGetMeter(signal.Meter, out MeterDefinition meter))
        {
            Drop(signal.Key);
            return;
        }

        MeterState state = For(signal.Npc, signal.Meter);
        double reading = Reading(signal.Npc, signal.Meter, now);
        signal.Tier = Math.Max(signal.Tier, meter.TierFor(reading));
        signal.EventIds = RankedEvents(signal.Npc, signal.Meter, now);
        signal.Reason = BuildReason(signal.Npc, meter, signal.Tier, signal.EventIds);

        if (!_mod.AliveNpcs.InvalidateDialogueCache(npc.Name))
        {
            Log.Warn($"{npc.Name} couldn't start a conversation: AliveNpcs' dialogue cache could not be cleared.");
            return;
        }

        _reasons[npc.Name] = (signal.Reason, now + C.Confront.ReasonLastsMinutes, signal.EventIds);
        state.ActedTier = Math.Max(state.ActedTier, signal.Tier);
        if (signal.Tier >= meter.TierCount && meter.GrudgeCarryover > 0)
        {
            state.Grudge = reading * meter.GrudgeCarryover;
            Log.Debug("Feelings", $"{npc.Name}: {meter.Id} at its strongest — {state.Grudge:0.0} will still be there tomorrow.");
        }
        _lastReactionMinute = now;
        state.Contributions.Clear(); // the meter empties; what was already counted stays, so old memories don't refill it
        Drop(signal.Key);

        Log.Debug("Feelings", $"{npc.Name}: opening an AliveNpcs conversation about {meter.Id} at level {signal.Tier}/{meter.TierCount} ({signal.EventIds.Count} memory(ies)).");
        Log.Verbose("Feelings", $"{npc.Name} reason block:\n{signal.Reason}");

        npc.Halt();
        npc.faceTowardFarmerForPeriod(5000, 4, false, player);
        GiveReward(signal, npc, player);

        // AliveNpcs hands a click to the base game (a gift) when the farmer holds an object, so for this one call
        // select a tool or empty slot, then put the farmer's own selection back.
        int heldSlot = player.CurrentToolIndex;
        int freeSlot = player.ActiveObject is null ? -1 : HandsFreeSlot(player);
        try
        {
            if (freeSlot >= 0)
            {
                player.CurrentToolIndex = freeSlot;
                Log.Debug("Feelings", $"{npc.Name}: farmer was holding an item; using slot {freeSlot} for the interaction, restoring slot {heldSlot} after.");
            }
            npc.checkAction(player, npc.currentLocation);
        }
        finally
        {
            if (freeSlot >= 0)
                player.CurrentToolIndex = heldSlot;
        }
    }

    /// <summary>Hand over whatever a rule promised — a round on the house, say — and note it in the prompt.</summary>
    private void GiveReward(Signal signal, NPC npc, Farmer player)
    {
        if (signal.Reward is not { } reward || reward.Item.Length == 0)
            return;
        int count = reward.CountFor(signal.Tier);
        if (count <= 0)
            return;

        try
        {
            Item item = ItemRegistry.Create(reward.Item, count);
            player.addItemByMenuIfNecessary(item);
            _reasons[npc.Name] = (
                $"{signal.Reason}\nYou are handing them {(count == 1 ? "one" : count.ToString())} {item.DisplayName} as you say it — mention it naturally.",
                NowMinute() + C.Confront.ReasonLastsMinutes,
                signal.EventIds);
            Log.Debug("Feelings", $"{npc.Name} hands over {count}× {item.DisplayName} ({reward.Item}).");
        }
        catch (Exception ex)
        {
            Log.Error($"{npc.Name} couldn't hand over '{reward.Item}'", ex);
        }
        finally
        {
            For(signal.Npc, signal.Meter).Reward = null;
        }
    }

    // ── asides: felt it, will mention it next time ──

    /// <summary>Keep it for the next natural conversation instead of interrupting. Costs no extra AI call.</summary>
    private void MaybeAside(string npc, MeterDefinition meter, int tier, int now, string why)
    {
        List<string> ids = RankedEvents(npc, meter.Id, now);
        if (ids.Count == 0)
            return;

        MeterState state = For(npc, meter.Id);
        state.ActedTier = Math.Max(state.ActedTier, tier);
        _asides[npc] = (BuildAside(npc, meter, tier, ids), now + C.Confront.ReasonLastsMinutes, ids);
        Log.Debug("Feelings", $"{npc} keeps {meter.Id} level {tier} to themselves for now ({why}); they'll bring it up next time you talk.");
    }

    /// <summary>Extra prompt content for an NPC right now: a reason they came over, or something on their mind.</summary>
    public string? ReasonFor(string npc)
    {
        int now = _nowMinute;
        if (TryTake(_reasons, npc, now, out string? reason, out _))
            return reason;
        if (TryTake(_asides, npc, now, out string? aside, out _))
            return aside;
        return null;
    }

    /// <summary>
    /// The memory id(s) behind whatever <see cref="ReasonFor"/> is about to hand over, so the prompt can
    /// guarantee that exact memory keeps its "[stands out]" mark instead of losing to the character budget.
    /// </summary>
    public IReadOnlyList<string> ReasonEventIds(string npc)
    {
        int now = _nowMinute;
        if (TryTake(_reasons, npc, now, out _, out IReadOnlyList<string>? ids))
            return ids;
        if (TryTake(_asides, npc, now, out _, out ids))
            return ids;
        return Array.Empty<string>();
    }

    private static bool TryTake(ConcurrentDictionary<string, (string Text, int ExpiresMinute, IReadOnlyList<string> EventIds)> store, string npc, int now, out string? text, out IReadOnlyList<string> eventIds)
    {
        if (!store.TryGetValue(npc, out var entry))
        {
            text = null;
            eventIds = Array.Empty<string>();
            return false;
        }
        if (now > 0 && now > entry.ExpiresMinute)
        {
            store.TryRemove(npc, out _);
            text = null;
            eventIds = Array.Empty<string>();
            return false;
        }
        text = entry.Text;
        eventIds = entry.EventIds;
        return true;
    }

    // ── the words handed to the AI ──

    private string BuildReason(string npc, MeterDefinition meter, int tier, IReadOnlyList<string> ids)
    {
        string farmer = Game1.player?.Name ?? "the farmer";
        string tone = meter.ToneFor(tier);
        Func<string, string?> resolve = token => token switch
        {
            "farmer" => farmer,
            "tone" => tone,
            "feeling" => meter.Label,
            _ => null,
        };

        var text = new StringBuilder();
        string header = meter.HeaderTemplate.Render(resolve);
        if (header.Length > 0)
            text.AppendLine(header);
        string opening = meter.OpeningTemplate.Render(resolve);
        if (opening.Length > 0)
            text.AppendLine(opening);
        AppendMemories(text, npc, ids);
        text.Append(meter.ClosingTemplate.Render(resolve));
        return text.ToString();
    }

    private string BuildAside(string npc, MeterDefinition meter, int tier, IReadOnlyList<string> ids)
    {
        string tone = meter.ToneFor(tier);
        var text = new StringBuilder("[Something on your mind — you haven't said it out loud]\n");
        AppendMemories(text, npc, ids);
        text.Append($"You feel {tone}. Bring it up yourself if it fits naturally, in your own voice and at your own certainty — or let it go, if that's more like you. Don't force it into an unrelated topic.");
        return text.ToString();
    }

    private void AppendMemories(StringBuilder text, string npc, IReadOnlyList<string> ids)
    {
        SDate today = SDate.Now();
        var events = _mod.Store.Snapshot.Where(e => ids.Contains(e.Id)).ToList();
        foreach (ScoredMemory memory in _mod.Selector.ScoreAll(events, npc, today.DaysSinceStart, Game1.timeOfDay).OrderByDescending(m => m.Score))
            text.AppendLine(_mod.Renderer.NpcLine(memory, npc, today.DaysSinceStart, Game1.timeOfDay, today.Year, C));
    }

    // ── console ──

    /// <summary>Force a reaction now (as_confront), at the next level that NPC hasn't acted on.</summary>
    public string Force(string npc, string? meterId = null)
    {
        int now = NowMinute();
        MeterDefinition? meter = meterId is { Length: > 0 }
            ? (Catalog.TryGetMeter(meterId, out MeterDefinition named) ? named : null)
            : Catalog.Meters.OrderByDescending(m => Reading(npc, m.Id, now)).FirstOrDefault();
        if (meter is null)
            return $"Unknown meter '{meterId}'. Known: {string.Join(", ", Catalog.Meters.Select(m => m.Id))}.";

        MeterState state = For(npc, meter.Id);
        int tier = Math.Min(state.ActedTier + 1, meter.TierCount);
        List<string> ids = state.Contributions.Count > 0
            ? RankedEvents(npc, meter.Id, now)
            : _mod.Store.Snapshot.Where(e => e.Witnesses.ContainsKey(npc)).OrderByDescending(e => e.TotalDay).ThenByDescending(e => e.Time).Select(e => e.Id).Take(C.Confront.MaxReasonLines).ToList();
        if (ids.Count == 0)
            return $"{npc} hasn't witnessed anything; nothing to bring up.";

        var signal = new Signal
        {
            Npc = npc,
            Meter = meter.Id,
            Tier = tier,
            EventIds = ids,
            SinceMinute = now,
            Reason = BuildReason(npc, meter, tier, ids),
            Reward = state.Reward,
        };
        _signals[signal.Key] = signal;
        return $"{npc} will bring up {meter.Id} (level {tier}/{meter.TierCount}, {ids.Count} memory(ies)) as soon as you're within {C.Confront.TriggerTiles} tiles on the same map.";
    }

    /// <summary>Readable meters for as_saturation.</summary>
    public string Describe(string? onlyNpc)
    {
        int now = NowMinute();
        var text = new StringBuilder($"Feelings ({Catalog.Meters.Count} meter(s), up to {C.Confront.MaxReactionsPerEvent} reaction(s) per event, turn goes to closeness × intensity × priority){(C.Confront.Enabled ? "" : " — REACTIONS DISABLED")}:");
        foreach (MeterDefinition meter in Catalog.Meters)
            text.Append($"\n  {meter.Id,-11} levels {string.Join("/", meter.Thresholds),-14} half-life {meter.HalfLifeHours:0.##}h  priority {meter.Priority:0.##}  {meter.Delivery}  '{meter.Emote}'");

        var npcs = _state.Keys.Where(n => onlyNpc is null || n.Equals(onlyNpc, StringComparison.OrdinalIgnoreCase)).ToList();
        if (npcs.Count == 0)
            text.Append("\n  (nobody feels anything worth tracking today)");

        foreach (string npc in npcs.OrderByDescending(n => Catalog.Meters.Max(m => Reading(n, m.Id, now))))
        {
            var readings = Catalog.Meters
                .Select(meter => (Meter: meter, Value: Reading(npc, meter.Id, now)))
                .Where(pair => pair.Value > 0.05)
                .OrderByDescending(pair => pair.Value)
                .ToList();
            if (readings.Count == 0)
                continue;

            text.Append($"\n  {npc}");
            foreach (var (meter, value) in readings)
            {
                MeterState state = For(npc, meter.Id);
                int tier = meter.TierFor(value);
                string next = tier < meter.TierCount ? $"next at {meter.Thresholds[tier]:0.#}" : "at its strongest";
                string waiting = _signals.ContainsKey($"{npc}|{meter.Id}") ? (_openingKey == $"{npc}|{meter.Id}" ? " · APPROACHING" : " · waiting their turn") : "";
                text.Append($"\n      {meter.Id,-11} {value,6:0.0}  level {tier}/{meter.TierCount} (acted {state.ActedTier})  {next}{waiting}{(state.Reward is not null ? " · has something to hand over" : "")}");
                foreach (var group in state.Contributions.GroupBy(x => x.EventId))
                    text.Append($"\n          {group.Sum(x => Decay(x, now, meter)),5:0.0}  {group.Key}");
            }
        }

        text.Append($"\nCooldown: {Math.Max(0, _lastReactionMinute + C.Confront.CooldownMinutes - now)} min left.");
        return text.ToString();
    }

    // ── helpers ──

    private MeterState For(string npc, string meterId)
    {
        if (!_state.TryGetValue(npc, out Dictionary<string, MeterState>? meters))
            _state[npc] = meters = new Dictionary<string, MeterState>(StringComparer.OrdinalIgnoreCase);
        if (!meters.TryGetValue(meterId, out MeterState? state))
            meters[meterId] = state = new MeterState();
        return state;
    }

    /// <summary>The meter this NPC has already settled on for this event type today, if any.</summary>
    private string? TypeLock(string npc, string eventType)
        => _typeLocks.TryGetValue(npc, out Dictionary<string, string>? byType) && byType.TryGetValue(eventType, out string? meterId)
            ? meterId
            : null;

    private void SetTypeLock(string npc, string eventType, string meterId)
    {
        if (!_typeLocks.TryGetValue(npc, out Dictionary<string, string>? byType))
            _typeLocks[npc] = byType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byType[eventType] = meterId;
    }

    private double Reading(string npc, string meterId, int now)
    {
        if (!_state.TryGetValue(npc, out Dictionary<string, MeterState>? meters) || !meters.TryGetValue(meterId, out MeterState? state))
            return 0;
        if (!Catalog.TryGetMeter(meterId, out MeterDefinition meter))
            return 0;
        return state.Contributions.Sum(contribution => Decay(contribution, now, meter));
    }

    private static double Decay(Contribution contribution, int now, MeterDefinition meter)
        => contribution.Amount * Math.Pow(0.5, Math.Max(0, now - contribution.Minute) / (Math.Max(0.1f, meter.HalfLifeHours) * 60d));

    private List<string> RankedEvents(string npc, string meterId, int now)
    {
        if (!Catalog.TryGetMeter(meterId, out MeterDefinition meter))
            return new List<string>();
        return For(npc, meterId).Contributions
            .GroupBy(contribution => contribution.EventId)
            .OrderByDescending(group => group.Sum(contribution => Decay(contribution, now, meter)))
            .Select(group => group.Key)
            .Take(C.Confront.MaxReasonLines)
            .ToList();
    }

    /// <summary>Why this NPC can't act on this feeling today at all, whatever the timing.</summary>
    private string DayBlocker(string npc, MeterDefinition meter, MeterState state)
        => state.ActedTier >= Math.Min(meter.MaxPerNpcPerDay, meter.TierCount) ? $"already spoke about {meter.Id} {state.ActedTier}× today"
            : meter.IsAside ? $"{meter.Id} never interrupts; it only colours the next conversation"
            : _mod.AliveNpcs.IsNpcDisabled(npc) ? "disabled in AliveNpcs"
            : !_mod.AliveNpcs.HasDialogueStart ? "AliveNpcs dialogue-start hook missing"
            : "";

    private string CooldownBlocker(int now)
        => now - _lastReactionMinute < C.Confront.CooldownMinutes ? $"cooldown ({_lastReactionMinute + C.Confront.CooldownMinutes - now} min left)" : "";

    /// <summary>Why this NPC can't open a conversation this moment ("" = they can).</summary>
    private string WaitReason(NPC npc, Farmer player, bool committed)
    {
        if (npc.currentLocation is null || player.currentLocation is null || npc.currentLocation != player.currentLocation)
            return "not on the farmer's map";
        int reach = committed ? Math.Max(C.Confront.TriggerTiles, C.Confront.EmoteRangeTiles) : C.Confront.TriggerTiles;
        if (Distance(npc, player) > reach)
            return $"farmer too far ({Distance(npc, player):0} tiles)";
        if (Game1.eventUp || npc.currentLocation.currentEvent is not null)
            return "cutscene or festival running";
        if (Game1.activeClickableMenu is not null || Game1.dialogueUp)
            return "a menu or dialogue is open";
        if (!Context.CanPlayerMove)
            return "farmer busy";
        if (player.ActiveObject is not null && HandsFreeSlot(player) < 0)
            return "farmer is holding an item and has no tool or empty slot to switch to (it would count as a gift)";
        if (_mod.AliveNpcs.IsGeneratingDialogue())
            return "AliveNpcs is generating another line";
        if (npc.IsInvisible)
            return "NPC invisible";
        return "";
    }

    private void LogWaitIfChanged(Signal signal, string wait)
    {
        static string Kind(string reason) => reason.Split('(')[0];
        if (Kind(wait) != Kind(signal.LastWait))
            Log.Debug("Feelings", $"{signal.Npc}: waiting — {wait}.");
        signal.LastWait = wait;
    }

    private void Drop(string key)
    {
        _signals.Remove(key);
        if (_openingKey == key)
            _openingKey = null;
    }

    /// <summary>The first reward any rule that matched this witness promised, if any.</summary>
    private static RuleReward? RewardFor(MemoryEvent memory, string npc)
    {
        if (!memory.Witnesses.TryGetValue(npc, out WitnessRecord? witness))
            return null;
        foreach (string rule in witness.Rules)
        {
            if (RewardCache.TryGetValue(memory.Type + "|" + rule, out RuleReward? reward))
                return reward;
        }
        return null;
    }

    /// <summary>Rewards resolved at load, keyed by type and matched rules (filled by <see cref="Prime"/>).</summary>
    private static readonly Dictionary<string, RuleReward> RewardCache = new(StringComparer.Ordinal);

    /// <summary>Index the rewards declared in the catalog so a stored memory can find them again.</summary>
    public void Prime()
    {
        RewardCache.Clear();
        foreach (EventDefinition definition in Catalog.All)
        {
            foreach (EventRule rule in definition.Rules.Where(rule => rule.Reward is not null))
                RewardCache[definition.Id + "|" + rule.Name] = rule.Reward!;
        }
        if (RewardCache.Count > 0)
            Log.Debug("Feelings", $"{RewardCache.Count} rule(s) can hand something over: {string.Join(", ", RewardCache.Keys)}.");
    }

    private int EmoteFor(string meterId)
        => Catalog.TryGetMeter(meterId, out MeterDefinition meter) ? Emotes.Resolve(meter.Emote) ?? Character.exclamationEmote : Character.exclamationEmote;

    private string EmoteName(string meterId)
        => Catalog.TryGetMeter(meterId, out MeterDefinition meter) ? meter.Emote : "exclamation";

    private int Levels(string meterId)
        => Catalog.TryGetMeter(meterId, out MeterDefinition meter) ? meter.TierCount : 3;

    /// <summary>A slot holding a tool or nothing, so ActiveObject is null while it's selected.</summary>
    private static int HandsFreeSlot(Farmer player)
    {
        for (int i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i] is null or Tool)
                return i;
        }
        return -1;
    }

    private static double Distance(NPC npc, Farmer player) => Vector2.Distance(npc.Tile, player.Tile);
}
