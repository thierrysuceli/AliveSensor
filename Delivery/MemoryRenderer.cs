using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Memory;

namespace AliveSensor.Delivery;

/// <summary>
/// Turns stored memories into English sentences for the AI (PRD §5, §8). The wording itself lives in the event
/// catalog, not here: this only picks the right phrase for the witness's certainty, resolves its tokens and adds
/// the time and place. Relative time ("two hours ago") is worked out at render time, never stored.
///
/// Reads only data captured in the event, so it is safe on AliveNpcs' background prompt thread.
/// </summary>
internal sealed class MemoryRenderer
{
    private readonly EventCatalog _catalog;

    public MemoryRenderer(EventCatalog catalog) => _catalog = catalog;

    // ── Channel A: one NPC's own memory ──

    /// <summary>One line of an NPC's memory list: when, where, and what they can say they perceived.</summary>
    public string NpcLine(ScoredMemory memory, string witness, int nowDay, int nowTime, int currentYear, ModConfig config, bool standsOut = false)
    {
        MemoryEvent e = memory.Event;
        string when = GameClock.Relative(e.TotalDay, memory.LastTime, nowDay, nowTime);
        string absolute = GameClock.Absolute(e.Day, e.Season, e.Year, memory.LastTime, currentYear);
        string sentence = Sentence(e, memory.Witness, witness, config);
        if (memory.Count > 1)
            sentence += $" ({memory.Count} times, between {GameClock.Clock12(memory.FirstTime)} and {GameClock.Clock12(memory.LastTime)})";
        string mark = standsOut ? "[stands out] " : "";
        return $"- {mark}{Text.Capitalize(when)} ({absolute}), at {e.LocationDisplay}: {sentence}.";
    }

    /// <summary>The certainty phrase plus what the witness could make out, for their level.</summary>
    public string Sentence(MemoryEvent memory, WitnessRecord witness, string witnessName, ModConfig config)
    {
        EventDefinition definition = _catalog.GetOrDefault(memory.Type);
        var tokens = new MemoryTokens(memory, witness, witnessName, config, definition);
        PhraseSet phrases = definition.PhrasesFor(tokens.Resolve);
        int level = Math.Clamp(witness.Level, 1, 5);

        // A type can replace the whole wrapper when "saw" is the wrong verb — an explosion is heard.
        if (phrases.LevelTemplate(level) is { } own)
            return own.Render(tokens.Resolve);

        string actor = tokens.Resolve("actor") ?? "someone";
        return level switch
        {
            5 => $"You clearly saw {Render(phrases.FullTemplate, tokens, memory)}",
            4 => $"You saw {Render(phrases.FullTemplate, tokens, memory)}",
            3 => $"You think you saw {Render(phrases.ReducedTemplate ?? phrases.FullTemplate, tokens, memory)}",
            2 => $"You think you saw {Render(phrases.ReducedTemplate ?? phrases.FullTemplate, tokens, memory)}, but you're not sure",
            _ => $"You noticed someone who looked like {actor} {Render(phrases.GlimpseTemplate ?? phrases.ReducedTemplate, tokens, memory)}, but you're not sure",
        };
    }

    // ── Channel B/C: the day's events for gossip (past tense, third person, with witnesses) ──

    /// <summary>One line of the night's gossip: when, where, what happened and who saw it.</summary>
    public string CycleLine(MemoryEvent memory, ModConfig config)
    {
        string witnesses = string.Join(", ", memory.Witnesses
            .OrderByDescending(pair => pair.Value.Level)
            .Select(pair => $"{DisplayName(memory, pair.Key)} ({LevelWords(pair.Value.Level)})"));
        return $"- {GameClock.Clock12(memory.Time)}, at {memory.LocationDisplay}: {PastSummary(memory, config)}. Witnessed by: {witnesses}.";
    }

    /// <summary>The event in the third person, as the village would retell it.</summary>
    public string PastSummary(MemoryEvent memory, ModConfig config)
    {
        EventDefinition definition = _catalog.GetOrDefault(memory.Type);
        var tokens = new MemoryTokens(memory, record: null, witness: null, config, definition);
        PhraseSet phrases = definition.PhrasesFor(tokens.Resolve);
        return Render(phrases.PastTemplate, tokens, memory, past: true);
    }

    public static string LevelWords(int level) => level switch
    {
        5 => "saw it clearly",
        4 => "saw it",
        3 => "thinks they saw it",
        2 => "not sure",
        _ => "barely noticed",
    };

    /// <summary>Render a phrase, or say plainly that this type has none rather than inventing something.</summary>
    private static string Render(PhraseTemplate? template, MemoryTokens tokens, MemoryEvent memory, bool past = false)
    {
        if (template is not null)
            return template.Render(tokens.Resolve);
        string actor = tokens.Resolve("actor") ?? "someone";
        return past ? $"{actor} did something ({memory.Type})" : $"{actor} do something ({memory.Type})";
    }

    private static string DisplayName(MemoryEvent memory, string npc)
        => memory.Names.TryGetValue(npc, out string? display) ? display : npc;
}
