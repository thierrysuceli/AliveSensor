using System;
using System.Collections.Generic;

namespace AliveSensor.Core;

/// <summary>
/// One feeling an NPC can build up about the farmer, declared in <c>assets/meters.json</c>. A witnessed event
/// can feed several at once — being caught drinking might amuse one neighbour, worry another and mean nothing
/// to a third — and each meter fills, fades, and eventually speaks up in its own way.
///
/// Everything a meter needs is here, so adding a fifth feeling is a JSON edit, not a code change.
/// </summary>
internal sealed class MeterDefinition
{
    /// <summary>Id used by event types and in the config (e.g. "confront").</summary>
    public string Id { get; set; } = "";

    /// <summary>Short human name for logs and the config menu.</summary>
    public string Label { get; set; } = "";

    /// <summary>The three levels this feeling passes through; each fires once per NPC per day.</summary>
    public List<float> Thresholds { get; set; } = new() { 15f, 30f, 50f };

    /// <summary>In-game hours after which the meter has drained by half.</summary>
    public float HalfLifeHours { get; set; } = 3f;

    /// <summary>Emote bubble shown while the NPC waits to speak: happy, sad, heart, exclamation, note, sleep, game, question, x, pause, angry, blush.</summary>
    public string Emote { get; set; } = "exclamation";

    /// <summary>
    /// "walkUp" — the NPC comes over and opens a conversation about it.
    /// "aside" — no emote and no interruption; it simply colours their next normal conversation.
    /// </summary>
    public string Delivery { get; set; } = "walkUp";

    /// <summary>Weight of this feeling when two NPCs want to speak at once (part of the arbitration score).</summary>
    public float Priority { get; set; } = 1f;

    /// <summary>How sure a witness must be (1–5) before this feeling makes them speak up.</summary>
    public int MinCertainty { get; set; } = 4;

    /// <summary>How many of this meter's levels one NPC can act on per day.</summary>
    public int MaxPerNpcPerDay { get; set; } = 3;

    /// <summary>Fraction of the meter that survives the night after its harshest level fires. 0 = forget by morning.</summary>
    public float GrudgeCarryover { get; set; }

    /// <summary>Line that opens the block handed to the AI, e.g. "[Why you are talking to the farmer right now]".</summary>
    public string Header { get; set; } = "";

    /// <summary>Sentence introducing the memories, before the list.</summary>
    public string Opening { get; set; } = "";

    /// <summary>How the NPC feels at each level, from first nudge to strongest. Used as <c>{tone}</c> in <see cref="Closing"/>.</summary>
    public List<string> Tones { get; set; } = new();

    /// <summary>Instruction after the list; <c>{tone}</c> becomes the level's tone.</summary>
    public string Closing { get; set; } = "";

    private PhraseTemplate? _header;
    private PhraseTemplate? _opening;
    private PhraseTemplate? _closing;

    /// <summary>True when this feeling only colours the next natural conversation instead of interrupting.</summary>
    public bool IsAside => string.Equals(Delivery, "aside", StringComparison.OrdinalIgnoreCase);

    public int TierCount => Thresholds.Count;

    /// <summary>The level (0–<see cref="TierCount"/>) a meter reading has reached.</summary>
    public int TierFor(double value)
    {
        int tier = 0;
        for (int i = 0; i < Thresholds.Count; i++)
        {
            if (value >= Thresholds[i])
                tier = i + 1;
        }
        return tier;
    }

    /// <summary>
    /// How strongly this is felt, as a multiple of the level it passed: 1.0 is just over the line, 2.0 is twice
    /// what it took. Normalising this way lets meters with very different scales be compared fairly.
    /// </summary>
    public double Intensity(double value, int tier)
    {
        if (Thresholds.Count == 0 || tier <= 0)
            return 0;
        float crossed = Thresholds[Math.Clamp(tier, 1, Thresholds.Count) - 1];
        return crossed <= 0 ? 1 : Math.Clamp(value / crossed, 0, 5);
    }

    public string ToneFor(int tier)
    {
        if (Tones.Count == 0)
            return "";
        return Tones[Math.Clamp(tier, 1, Tones.Count) - 1];
    }

    public IReadOnlyList<string> Compile()
    {
        var problems = new List<string>();
        if (Thresholds.Count == 0)
            problems.Add($"meter '{Id}' has no thresholds.");
        for (int i = 1; i < Thresholds.Count; i++)
        {
            if (Thresholds[i] <= Thresholds[i - 1])
                problems.Add($"meter '{Id}' thresholds must climb: {Thresholds[i - 1]} → {Thresholds[i]}.");
        }
        if (!IsAside && Tones.Count < Thresholds.Count)
            problems.Add($"meter '{Id}' has {Thresholds.Count} level(s) but only {Tones.Count} tone(s).");
        if (Emotes.Resolve(Emote) is null)
            problems.Add($"meter '{Id}' uses emote '{Emote}', which NPCs can't show. Known: {string.Join(", ", Emotes.Names)}.");

        _header = PhraseTemplate.Parse(Header);
        _opening = PhraseTemplate.Parse(Opening);
        _closing = PhraseTemplate.Parse(Closing);
        return problems;
    }

    public PhraseTemplate HeaderTemplate => _header ??= PhraseTemplate.Parse(Header);
    public PhraseTemplate OpeningTemplate => _opening ??= PhraseTemplate.Parse(Opening);
    public PhraseTemplate ClosingTemplate => _closing ??= PhraseTemplate.Parse(Closing);
}

/// <summary>
/// The emote bubbles an NPC can actually show. The player's emote wheel lists more, but most of those are body
/// animations that reuse these same bubbles — an NPC only gets the bubble, so this is the real palette.
/// </summary>
internal static class Emotes
{
    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["can"] = 4,
        ["question"] = 8,
        ["angry"] = 12,
        ["exclamation"] = 16,
        ["heart"] = 20,
        ["sleep"] = 24,
        ["sad"] = 28,
        ["happy"] = 32,
        ["x"] = 36,
        ["pause"] = 40,
        ["game"] = 52,
        ["note"] = 56,
        ["blush"] = 60,
    };

    public static IEnumerable<string> Names => ByName.Keys;

    /// <summary>The sprite index for a name, or null when NPCs have no such bubble.</summary>
    public static int? Resolve(string? name)
        => name is not null && ByName.TryGetValue(name, out int index) ? index : null;
}
