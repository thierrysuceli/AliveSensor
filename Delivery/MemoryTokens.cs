using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Memory;

namespace AliveSensor.Delivery;

/// <summary>
/// Resolves the tokens an event type's phrases use, for one memory seen by one witness. Everything is read from
/// the stored event, so this is safe on AliveNpcs' background prompt thread.
///
/// Names are rendered from the witness's point of view: their own name becomes "you", and a room they own
/// becomes "your". With no witness (the night's gossip) everything stays in the third person.
/// </summary>
internal sealed class MemoryTokens
{
    private const int MaxDepth = 4;

    private readonly MemoryEvent _event;
    private readonly WitnessRecord? _record;
    private readonly string? _witness;
    private readonly ModConfig _config;
    private readonly EventDefinition _definition;
    private readonly List<string> _owners;
    private int _depth;

    public MemoryTokens(MemoryEvent memory, WitnessRecord? record, string? witness, ModConfig config, EventDefinition definition)
    {
        _event = memory;
        _record = record;
        _witness = witness;
        _config = config;
        _definition = definition;
        _owners = Text.SplitList(memory.Payload.GetValueOrDefault("owners"));
    }

    /// <summary>A token's value, or null when this memory has nothing to put there.</summary>
    public string? Resolve(string name)
    {
        if (name.Length == 0)
            return null;

        int separator = name.IndexOf(':');
        if (separator > 0)
        {
            string prefix = name.Substring(0, separator);
            string key = name.Substring(separator + 1);
            return prefix switch
            {
                "p" => Payload(key),
                "a" => Payload(key) is { Length: > 0 } value ? Text.Article(value) : null,
                "n" => Payload(key) is { Length: > 0 } npc ? Name(npc) : null,
                "you" => Payload(key) is { Length: > 0 } who && IsWitness(who) ? "you" : null,
                _ => null,
            };
        }

        return name switch
        {
            "actor" => Name(_event.Actor),
            "target" => _event.Target is { Length: > 0 } ? Name(_event.Target) : null,
            "targetPossessive" => _event.Target is { Length: > 0 } target ? Possessive(new[] { target }) : null,
            "owners" => _owners.Count > 0 ? Possessive(_owners) : null,
            "ownersPlain" => _owners.Count > 0 ? Text.JoinAnd(_owners.Select(Display).ToList()) : null,
            "place" => Place(),
            "location" => _event.LocationDisplay,
            "nearPhrase" => _event.Near,
            "time" => GameClock.Clock12(_event.Time),
            "count" => Payload("count"),
            "seen" => _record is { Count: > 0 } ? _record.Count.ToString() : null,
            "attempt" => Payload("attempt"),
            "secretMode" => _config.Talk.RespectKeepSecret && _event.Payload.ContainsKey("secret") ? "secret" : "open",
            "witnessName" => _witness is { Length: > 0 } ? Display(_witness) : null,
            _ => Computed(name),
        };
    }

    private string? Payload(string key)
        => _event.Payload.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    /// <summary>A token the event type declared itself, e.g. turning <c>taste: loved</c> into a phrase.</summary>
    private string? Computed(string name)
    {
        if (!_definition.Tokens.TryGetValue(name, out ComputedToken? token) || _depth >= MaxDepth)
            return null;
        _depth++;
        try
        {
            string value = token.Render(Resolve);
            return value.Length > 0 ? value : null;
        }
        finally
        {
            _depth--;
        }
    }

    private bool IsWitness(string npc) => _witness is not null && string.Equals(npc, _witness, StringComparison.OrdinalIgnoreCase);

    /// <summary>The display name stored with the event, so a translated name survives into the prompt.</summary>
    private string Display(string npc) => _event.Names.TryGetValue(npc, out string? display) ? display : npc;

    private string Name(string? npc)
    {
        if (string.IsNullOrWhiteSpace(npc))
            return "someone";
        return IsWitness(npc) ? "you" : Display(npc);
    }

    private string Possessive(IReadOnlyList<string> npcs)
    {
        if (npcs.Count == 0)
            return "someone's";
        if (_witness is not null && npcs.Contains(_witness, StringComparer.OrdinalIgnoreCase))
            return npcs.Count == 1 ? "your" : "your family's";
        string joined = Text.JoinAnd(npcs.Select(Display).ToList());
        return joined.EndsWith("s", StringComparison.Ordinal) ? joined + "'" : joined + "'s";
    }

    /// <summary>The place's name, or "your home" when the witness lives there.</summary>
    private string Place()
    {
        if (_witness is not null && _owners.Contains(_witness, StringComparer.OrdinalIgnoreCase))
            return "your home";
        return Payload("placeDisplay") ?? _event.LocationDisplay;
    }
}
