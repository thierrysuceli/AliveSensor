using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Memory;
using AliveSensor.World;

namespace AliveSensor.Delivery;

/// <summary>A memory as one NPC holds it, with the score breakdown.</summary>
internal sealed record ScoredMemory(
    MemoryEvent Event,
    WitnessRecord Witness,
    double Score,
    double Recency,
    double Involvement,
    string InvolvementReason,
    int Count,
    int FirstTime,
    int LastTime,
    int Seen = 1,
    double Repeat = 1d,
    double Age = 0d)
{
    /// <summary>Fresh and strong enough that the NPC should bring it up unprompted.</summary>
    public bool StandsOut(ModConfig c) => Score >= c.Scoring.NotableScore && Age <= c.Scoring.NotableMaxAgeDays;
}

/// <summary>
/// Score = grade × recency × involvement × visibility × per-witness bonus × repetition (PRD §7).
/// Pure over the store snapshot: safe on AliveNpcs' background prompt thread.
/// </summary>
internal sealed class MemorySelector
{
    private readonly Func<ModConfig> _config;
    private readonly Relationships _relationships;

    public MemorySelector(Func<ModConfig> config, Relationships relationships)
    {
        _config = config;
        _relationships = relationships;
    }

    /// <summary>Every memory the NPC holds, scored, unfiltered.</summary>
    public List<ScoredMemory> ScoreAll(IReadOnlyList<MemoryEvent> events, string npc, int nowDay, int nowTime)
    {
        ModConfig c = _config();
        var result = new List<ScoredMemory>();
        foreach (MemoryEvent e in events)
        {
            if (!e.Witnesses.TryGetValue(npc, out WitnessRecord? witness))
                continue;

            float halfLife = c.Scoring.HalfLifeDaysByType.TryGetValue(e.Type, out float h) ? h : 3f;
            double age = GameClock.AgeInDays(e.TotalDay, e.Time, nowDay, nowTime);
            double recency = Math.Pow(0.5, age / halfLife);

            (double involvement, string reason) = Involvement(c, e, npc);
            int seen = TimesSeen(e, witness);
            double repeat = RepeatFactor(c, seen);
            double score = e.Grade * recency * involvement * witness.Visibility * witness.Bonus * repeat;
            result.Add(new ScoredMemory(e, witness, score, recency, involvement, reason, 1, e.Time, e.Time, seen, repeat, age));
        }
        return result;
    }

    /// <summary>What goes into the NPC's prompt: filtered, compacted, best first, capped.</summary>
    public List<ScoredMemory> Select(IReadOnlyList<MemoryEvent> events, string npc, int nowDay, int nowTime, out int candidates)
    {
        ModConfig c = _config();
        List<ScoredMemory> all = ScoreAll(events, npc, nowDay, nowTime);
        candidates = all.Count;

        var eligible = all.Where(m => m.Score >= c.Scoring.MinScore).ToList();
        var compacted = new List<ScoredMemory>();
        foreach (var group in eligible.GroupBy(m => CompactionKey(c, m)))
        {
            if (group.Key.Length == 0 || group.Count() == 1)
            {
                compacted.AddRange(group);
                continue;
            }
            ScoredMemory best = group.OrderByDescending(m => m.Score).First();
            int seen = Math.Max(best.Seen, group.Sum(m => m.Seen));
            double repeat = RepeatFactor(c, seen);
            compacted.Add(best with
            {
                Score = best.Score / best.Repeat * repeat,
                Seen = seen,
                Repeat = repeat,
                Count = group.Count(),
                FirstTime = group.Min(m => m.Event.Time),
                LastTime = group.Max(m => m.Event.Time),
            });
        }

        return compacted
            .OrderByDescending(m => m.Score)
            .Take(c.Delivery.MaxPerNpc)
            .ToList();
    }

    /// <summary>How many times this witness saw the thing: their own counter, else the event's merged count.</summary>
    public static int TimesSeen(MemoryEvent e, WitnessRecord witness)
    {
        if (witness.Count > 0)
            return witness.Count;
        return int.TryParse(e.Payload.GetValueOrDefault("count"), out int n) && n > 1 ? n : 1;
    }

    /// <summary>1 + step × (times − 1), capped: doing it again in front of someone weighs more.</summary>
    public static double RepeatFactor(ModConfig c, int seen)
        => Math.Min(1d + c.Scoring.RepeatStep * Math.Max(0, seen - 1), c.Scoring.RepeatMaxMultiplier);

    /// <summary>
    /// How much a day's event is worth as village gossip: grade × repetition × how many people know.
    /// Used by the night cycle, where there is no single witness to score for.
    /// </summary>
    public static double GossipWeight(ModConfig c, MemoryEvent e)
    {
        int seen = Math.Max(TimesSeen(e, new WitnessRecord()), e.Witnesses.Values.Select(w => w.Count).DefaultIfEmpty(0).Max());
        double sureWitnesses = e.Witnesses.Values.Sum(w => w.Level >= 4 ? 1d : 0.4d);
        return e.Grade * RepeatFactor(c, seen) * (1d + Math.Log(1d + sureWitnesses) / 2d);
    }

    private (double Value, string Reason) Involvement(ModConfig c, MemoryEvent e, string npc)
        => Role(e, npc, _relationships) switch
        {
            "vendor" => (c.Scoring.VendorMultiplier, "vendor"),
            "target" => (c.Scoring.VictimMultiplier, "target"),
            "relative" => (c.Scoring.RelativeMultiplier, $"related to {e.Target}"),
            _ => (1d, "bystander"),
        };

    /// <summary>What part an NPC played: "target", "vendor", "relative" or "bystander". Shared with the rules.</summary>
    public static string Role(MemoryEvent e, string npc, Relationships relationships)
    {
        if (string.Equals(e.Target, npc, StringComparison.OrdinalIgnoreCase) || e.Involved.Contains(npc, StringComparer.OrdinalIgnoreCase))
        {
            if (e.Payload.TryGetValue("vendor", out string? vendor) && string.Equals(vendor, npc, StringComparison.OrdinalIgnoreCase))
                return "vendor";
            return "target";
        }
        return relationships.AreRelated(npc, e.Target) ? "relative" : "bystander";
    }

    /// <summary>Low-grade events of the same kind, place and day merge into one line; empty key = never merge.</summary>
    private static string CompactionKey(ModConfig c, ScoredMemory m)
    {
        MemoryEvent e = m.Event;
        if (e.Grade > c.Scoring.CompactMaxGrade)
            return "";
        return $"{e.Type}|{e.Target}|{e.Location}|{e.TotalDay}|{e.Payload.GetValueOrDefault("action")}|{e.Payload.GetValueOrDefault("place")}|{e.Actor}";
    }
}
