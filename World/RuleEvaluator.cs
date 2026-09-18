using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Core;
using AliveSensor.Memory;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace AliveSensor.World;

/// <summary>
/// Works out what each witness feels about an event, by running the type's rules against the world as it is at
/// that instant. This is the only place rules are evaluated: the result is stored on the witness, so scoring,
/// saturation and prompts never have to touch live game state again (and stay safe on background threads).
/// </summary>
internal sealed class RuleEvaluator
{
    private readonly EventCatalog _catalog;
    private readonly Log _log;

    public RuleEvaluator(EventCatalog catalog, Log log)
    {
        _catalog = catalog;
        _log = log;
    }

    /// <summary>
    /// The meter weights, wording and reward for one witness of one event.
    /// </summary>
    /// <param name="memory">The event being recorded (payload, location and time already filled in).</param>
    /// <param name="npc">The witness.</param>
    /// <param name="witness">How well they perceived it.</param>
    /// <param name="role">Their part in it, from <see cref="Delivery.MemorySelector"/>'s vocabulary.</param>
    /// <param name="origin">Where it happened, for "is anyone nearby" checks.</param>
    /// <param name="location">The map it happened on.</param>
    public RuleOutcome Evaluate(MemoryEvent memory, string npc, WitnessRecord witness, string role, Vector2 origin, GameLocation? location)
    {
        EventDefinition definition = _catalog.GetOrDefault(memory.Type);
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // Start from what the type says this kind of event is worth, for a witness in this part.
        foreach (var (meterId, contribution) in definition.Meters)
        {
            if (!contribution.AppliesTo(role) || !contribution.HasRequiredPayload(memory))
                continue;
            if (contribution.Weight > 0)
                weights[meterId] = contribution.Weight;
        }

        var matched = new List<string>();
        PhraseSet? phrases = null;
        RuleReward? reward = null;
        bool silent = false;

        // Type-specific rules first (most specific wins the wording/reward), then rules that apply to every type.
        foreach (EventRule rule in definition.Rules.Concat(_catalog.GlobalRules))
        {
            if (!Matches(rule.When, memory, npc, witness, role, origin, location))
                continue;
            matched.Add(rule.Name);

            foreach (var (meterId, weight) in rule.Set)
            {
                if (weight > 0)
                    weights[meterId] = weight;
                else
                    weights.Remove(meterId);
            }
            foreach (var (meterId, factor) in rule.Multiply)
            {
                if (!weights.TryGetValue(meterId, out float current))
                    continue;
                float scaled = current * factor;
                if (scaled > 0)
                    weights[meterId] = scaled;
                else
                    weights.Remove(meterId);
            }

            // Wording and rewards come from the first rule that offers them.
            phrases ??= rule.Phrases;
            reward ??= rule.Reward;
            silent |= rule.Silent;
        }

        return new RuleOutcome(weights, matched, phrases, reward, silent);
    }

    private bool Matches(RuleConditions when, MemoryEvent memory, string npc, WitnessRecord witness, string role, Vector2 origin, GameLocation? location)
    {
        if (when.IsEmpty)
            return true;

        if (when.Witness.Count > 0 && !when.Witness.Contains(npc, StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.NotWitness.Contains(npc, StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.Role.Count > 0 && !when.Role.Contains(role, StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.MinCertainty is int minLevel && witness.Level < minLevel)
            return false;

        foreach (var (key, expected) in when.Payload)
        {
            if (!memory.Payload.TryGetValue(key, out string? actual))
                return false;
            if (expected.Length > 0 && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (when.Weather.Count > 0 && !when.Weather.Contains(memory.Weather, StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.Season.Count > 0 && !when.Season.Contains(memory.Season, StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.Weekday.Count > 0 && !when.Weekday.Contains(SDate.Now().DayOfWeek.ToString(), StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.Location.Count > 0 && !when.Location.Contains(memory.Location, StringComparer.OrdinalIgnoreCase))
            return false;
        if (when.Outdoors is bool outdoors && memory.Outdoors != outdoors)
            return false;
        if (when.TimeFrom is int from && memory.Time < from)
            return false;
        if (when.TimeTo is int to && memory.Time > to)
            return false;

        if (when.MinHearts is not null || when.MaxHearts is not null)
        {
            int hearts = Hearts(npc);
            if (when.MinHearts is int min && hearts < min)
                return false;
            if (when.MaxHearts is int max && hearts > max)
                return false;
        }

        if (when.NearbyAge is { } nearby)
        {
            List<string> present = NearbyOfAge(nearby, origin, location, npc);
            if ((present.Count > 0) != nearby.Present)
                return false;
            if (present.Count > 0)
            {
                // So a phrase can say exactly who/how many, e.g. "with 2 children nearby" instead of just "a child".
                memory.Payload[$"nearby{nearby.Age}Count"] = present.Count.ToString();
                memory.Payload[$"nearby{nearby.Age}Names"] = string.Join(", ", present);
            }
        }

        return true;
    }

    /// <summary>Everyone of the given age standing near where it happened (the witness doesn't count themselves).</summary>
    private List<string> NearbyOfAge(NearbyAgeCondition condition, Vector2 origin, GameLocation? location, string witness)
    {
        var found = new List<string>();
        if (location is null)
            return found;
        if (condition.AgeValue is not int age)
        {
            _log.WarnOnce($"rule-age-{condition.Age}", $"A rule asks for nearby '{condition.Age}'; expected child, teen or adult.");
            return found;
        }

        foreach (NPC npc in location.characters)
        {
            if (!npc.IsVillager || npc.IsInvisible || npc.Age != age)
                continue;
            if (string.Equals(npc.Name, witness, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Vector2.Distance(npc.Tile, origin) <= condition.Tiles)
                found.Add(npc.Name);
        }
        return found;
    }

    private static int Hearts(string npc)
    {
        try
        {
            return Game1.player?.getFriendshipHeartLevelForNPC(npc) ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
