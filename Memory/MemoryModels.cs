using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AliveSensor.Memory;

/// <summary>
/// One thing someone did that NPCs may have witnessed (PRD §11). Recorded by script, never by AI.
/// Absolute when/where is stored; relative time ("2 hours ago") is computed when rendering.
/// Treat instances as immutable once added to the store: updates replace the object (copy-on-write),
/// because prompt providers read them from a background thread.
/// </summary>
public sealed class MemoryEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>Sensor that produced it (or "simulate").</summary>
    public string Source { get; set; } = "";

    // ── when (absolute) ──
    public int Day { get; set; }
    public string Season { get; set; } = "";
    public int Year { get; set; }
    /// <summary>1-based day count since the save started (SMAPI SDate.DaysSinceStart).</summary>
    public int TotalDay { get; set; }
    /// <summary>Game time, e.g. 1430.</summary>
    public int Time { get; set; }

    // ── where (absolute, tile precision) ──
    public string Location { get; set; } = "";
    public string LocationDisplay { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
    public bool Outdoors { get; set; }
    public string Weather { get; set; } = "";
    /// <summary>Closest reference point, e.g. "near the entrance to Pierre's General Store".</summary>
    public string? Near { get; set; }
    public int NearTiles { get; set; } = -1;

    // ── who ──
    public string Actor { get; set; } = "";
    public string? Target { get; set; }
    public List<string> Involved { get; set; } = new();

    /// <summary>Internal NPC name → display name, captured on the main thread for background rendering.</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    /// <summary>Type-specific details (item, taste, shop, quotes, room owners...).</summary>
    public Dictionary<string, string> Payload { get; set; } = new();

    // ── how much it matters ──
    /// <summary>Relevance ("grau"): type base × event-level aggravators.</summary>
    public float Grade { get; set; }
    public List<string> Aggravators { get; set; } = new();

    /// <summary>NPC internal name → how that NPC perceived the event.</summary>
    public Dictionary<string, WitnessRecord> Witnesses { get; set; } = new();

    public MemoryEvent Clone()
    {
        var copy = (MemoryEvent)MemberwiseClone();
        copy.Involved = new List<string>(Involved);
        copy.Names = new Dictionary<string, string>(Names);
        copy.Payload = new Dictionary<string, string>(Payload);
        copy.Aggravators = new List<string>(Aggravators);
        copy.Witnesses = new Dictionary<string, WitnessRecord>();
        foreach (var pair in Witnesses)
            copy.Witnesses[pair.Key] = pair.Value.Clone();
        return copy;
    }
}

public sealed class WitnessRecord
{
    /// <summary>0–1: distance × weather × light × attention (1 for people directly involved).</summary>
    public float Visibility { get; set; }
    public int Distance { get; set; }
    /// <summary>Victim, receiver, vendor or room owner.</summary>
    public bool Direct { get; set; }
    /// <summary>Certainty level 1–5, rendered as a phrase for the AI.</summary>
    public int Level { get; set; }
    /// <summary>Per-witness relevance multiplier (e.g. it was their own trash can).</summary>
    public float Bonus { get; set; } = 1f;
    public List<string> BonusReasons { get; set; } = new();
    /// <summary>Per-witness counter shown in text (e.g. 3rd drink they saw today); 0 = none.</summary>
    public int Count { get; set; }

    /// <summary>
    /// What this witness feels about it: meter id → weight, decided by the event type and its rules at the moment
    /// it happened. Stored rather than recomputed, so scoring and prompts never need live game state.
    /// </summary>
    public Dictionary<string, float> Meters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of the rules that shaped this witness's reading — readable in the save, handy in logs.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>They keep score but never bring it up.</summary>
    public bool Silent { get; set; }

    /// <summary>The feeling this left strongest in them, which colours how the memory is worded.</summary>
    public string? Feeling
    {
        get
        {
            string? best = null;
            float bestWeight = 0;
            foreach (var (meter, weight) in Meters)
            {
                if (weight <= bestWeight)
                    continue;
                bestWeight = weight;
                best = meter;
            }
            return best;
        }
    }

    public WitnessRecord Clone()
    {
        var copy = (WitnessRecord)MemberwiseClone();
        copy.BonusReasons = new List<string>(BonusReasons);
        copy.Meters = new Dictionary<string, float>(Meters, StringComparer.OrdinalIgnoreCase);
        copy.Rules = new List<string>(Rules);
        return copy;
    }
}

public sealed class MemoryFile
{
    public List<MemoryEvent> Events { get; set; } = new();
    public int NextSequence { get; set; } = 1;
}

/// <summary>Per day, per witness counters used by aggravators (drinks seen, door attempts...).</summary>
public sealed class DailyCountersFile
{
    /// <summary>"totalDay" → witness (or "_player") → counter name → value.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> Days { get; set; } = new();
}

/// <summary>Same envelope AliveNpcs uses for its data files.</summary>
public sealed class DataEnvelope<T>
{
    [JsonProperty("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonProperty("data")]
    public T? Data { get; set; }
}
