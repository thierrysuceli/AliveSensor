using System;
using System.Collections.Generic;
using System.Linq;

namespace AliveSensor.Core;

/// <summary>
/// One witnessed event type, as declared in <c>assets/event-types.json</c>. This is the whole contract a new
/// sensor has to satisfy: fill an <see cref="World.EventDraft"/> with the payload the phrases here mention, and
/// the rest of the mod (perception, grading, scoring, saturation, prompts, gossip) works with no extra code.
///
/// Deserialized by SMAPI's JSON reader and then treated as read-only; <see cref="Compile"/> runs once at load.
/// </summary>
internal sealed class EventDefinition
{
    /// <summary>The type id used by sensors and stored in every memory (e.g. "room_locked").</summary>
    public string Id { get; set; } = "";

    /// <summary>How much this kind of event matters at all (PRD "grade"); the final multiplier on its score.</summary>
    public float Grade { get; set; } = 1f;

    /// <summary>Days after which a memory of this kind is worth half as much.</summary>
    public float HalfLifeDays { get; set; } = 3f;

    /// <summary>How far a witness can be, in tiles; -1 means the whole map.</summary>
    public int RangeTiles { get; set; } = 8;

    /// <summary>Certainty (1–5) nobody who witnessed this can fall below, however poor the view.</summary>
    public int MinCertainty { get; set; } = 1;

    public PerceptionRules Perception { get; set; } = new();

    public ConfrontRules Confront { get; set; } = new();

    /// <summary>What this kind of event does to each feeling: meter id → how much it counts, and for whom.</summary>
    public Dictionary<string, MeterContribution> Meters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exceptions: "when Shane sees it, it's funny instead". Checked once per witness when recorded.</summary>
    public List<EventRule> Rules { get; set; } = new();

    /// <summary>Token whose value picks a variant, e.g. "p:action" for enter/leave.</summary>
    public string? VariantBy { get; set; }

    /// <summary>Phrase sets by variant value; missing slots fall back to <see cref="Phrases"/>.</summary>
    public Dictionary<string, PhraseSet> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The phrases used when there is no variant, and the fallback for partial variants.</summary>
    public PhraseSet Phrases { get; set; } = new();

    /// <summary>Extra tokens this type defines, e.g. turning <c>taste: loved</c> into "Abigail loved it".</summary>
    public Dictionary<string, ComputedToken> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parse every template once. Returns the problems found, so the catalog can report them.</summary>
    public IReadOnlyList<string> Compile()
    {
        var problems = new List<string>();
        Phrases.Compile(Id, "phrases", problems);
        foreach (var (variant, phrases) in Variants)
            phrases.Compile(Id, $"variants.{variant}", problems);
        foreach (var (name, token) in Tokens)
            token.Compile(Id, name, problems);
        for (int i = 0; i < Rules.Count; i++)
            problems.AddRange(Rules[i].Compile(Id, i));

        if (Phrases.IsEmpty && Variants.Count == 0)
            problems.Add($"'{Id}' has no phrases at all: memories of this kind will read as \"did something ({Id})\".");
        return problems;
    }

    /// <summary>The phrases for a memory, honouring <see cref="VariantBy"/> and falling back to the base set.</summary>
    public PhraseSet PhrasesFor(Func<string, string?> resolve)
    {
        if (VariantBy is null || Variants.Count == 0)
            return Phrases;
        string variant = resolve(VariantBy) ?? "";
        if (Variants.TryGetValue(variant, out PhraseSet? match) || Variants.TryGetValue("default", out match))
            return match.WithFallback(Phrases);
        return Phrases;
    }
}

/// <summary>How much one kind of event feeds one feeling, and for which witnesses.</summary>
internal sealed class MeterContribution
{
    /// <summary>Points this event is worth for that meter, before the memory's own score scales it. 0 = never.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Limit it to certain parts: "target", "vendor", "relative", "bystander". Empty means everyone.</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>Only counts when the payload has this key, e.g. "alcohol" so a sandwich bothers nobody.</summary>
    public string? RequiresPayload { get; set; }

    /// <summary>Doesn't count for an owner who already trusts the farmer (the game lets them past the door).</summary>
    public bool OwnerTrustExempt { get; set; }

    public bool AppliesTo(string role)
        => Roles.Count == 0 || Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public bool HasRequiredPayload(Memory.MemoryEvent memory)
        => RequiresPayload is not { Length: > 0 } key || memory.Payload.ContainsKey(key);
}

/// <summary>How well this kind of event can be perceived, beyond plain distance.</summary>
internal sealed class PerceptionRules
{
    /// <summary>Bedroom walls don't hide it (an explosion is heard through them; someone tiptoeing is not).</summary>
    public bool ThroughWalls { get; set; }

    /// <summary>It reaches NPCs asleep in their own room.</summary>
    public bool WakesSleepers { get; set; }

    /// <summary>Visibility nobody on the map falls below — how loud it is, in effect. 0 = no floor.</summary>
    public float HeardFloor { get; set; }
}

/// <summary>How this kind of event feeds the saturation meter that makes NPCs walk up to the farmer.</summary>
internal sealed class ConfrontRules
{
    /// <summary>Multiplier on the memory's score when it fills a witness's meter. 0 = never counts.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Only counts when the payload has this key — e.g. "alcohol", so a sandwich bothers nobody.</summary>
    public string? RequiresPayload { get; set; }

    /// <summary>Doesn't count for an owner who already trusts the farmer (the game lets them past the door).</summary>
    public bool OwnerTrustExempt { get; set; }
}

/// <summary>A payload value turned into a phrase fragment, e.g. <c>taste: loved</c> → "Abigail loved it".</summary>
internal sealed class ComputedToken
{
    /// <summary>Token to read, usually a payload key like "taste" (bare names are read from the payload).</summary>
    public string From { get; set; } = "";

    /// <summary>Value → fragment. Fragments may themselves use tokens.</summary>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Used when the value isn't in <see cref="Map"/>; empty by default, which drops optional parts.</summary>
    public string Default { get; set; } = "";

    private Dictionary<string, PhraseTemplate>? _compiled;
    private PhraseTemplate? _compiledDefault;

    public void Compile(string typeId, string name, IList<string> problems)
    {
        if (From.Length == 0)
            problems.Add($"'{typeId}' token '{name}' has no \"From\".");
        _compiled = new Dictionary<string, PhraseTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (value, fragment) in Map)
            _compiled[value] = PhraseTemplate.Parse(fragment);
        _compiledDefault = PhraseTemplate.Parse(Default);
    }

    /// <summary>Resolve this token for one memory. <paramref name="resolve"/> reads the source and any nested tokens.</summary>
    public string Render(Func<string, string?> resolve)
    {
        string source = resolve(From) ?? "";
        PhraseTemplate? template = _compiled is not null && _compiled.TryGetValue(source, out PhraseTemplate? match)
            ? match
            : _compiledDefault;
        return template?.Render(resolve) ?? "";
    }
}

/// <summary>
/// What a witness can put into words at each level of certainty. <see cref="Full"/> is what someone who saw it
/// plainly can describe, <see cref="Reduced"/> what an unsure witness makes out, <see cref="Glimpse"/> the barest
/// impression, and <see cref="Past"/> the third-person line used for the night's gossip.
/// <see cref="Levels"/> replaces the whole "You clearly saw …" wrapper when a type needs its own wording
/// (an explosion is heard, not seen).
/// </summary>
internal sealed class PhraseSet
{
    public string? Full { get; set; }
    public string? Reduced { get; set; }
    public string? Glimpse { get; set; }
    public string? Past { get; set; }
    public Dictionary<string, string> Levels { get; set; } = new();

    private PhraseTemplate? _full;
    private PhraseTemplate? _reduced;
    private PhraseTemplate? _glimpse;
    private PhraseTemplate? _past;
    private Dictionary<int, PhraseTemplate>? _levels;

    public bool IsEmpty => Full is null && Reduced is null && Glimpse is null && Past is null && Levels.Count == 0;

    public PhraseTemplate? FullTemplate => _full;
    public PhraseTemplate? ReducedTemplate => _reduced;
    public PhraseTemplate? GlimpseTemplate => _glimpse;
    public PhraseTemplate? PastTemplate => _past;

    public PhraseTemplate? LevelTemplate(int level)
        => _levels is not null && _levels.TryGetValue(level, out PhraseTemplate? template) ? template : null;

    public void Compile(string typeId, string where, IList<string> problems)
    {
        _full = Compile(Full);
        _reduced = Compile(Reduced);
        _glimpse = Compile(Glimpse);
        _past = Compile(Past);
        _levels = new Dictionary<int, PhraseTemplate>();
        foreach (var (key, text) in Levels)
        {
            if (!int.TryParse(key, out int level) || level is < 1 or > 5)
            {
                problems.Add($"'{typeId}' {where}.levels has key '{key}'; expected a certainty level 1–5.");
                continue;
            }
            _levels[level] = PhraseTemplate.Parse(text);
        }
        static PhraseTemplate? Compile(string? text) => text is null ? null : PhraseTemplate.Parse(text);
    }

    /// <summary>A variant set, backed by the type's base phrases for anything it doesn't define.</summary>
    public PhraseSet WithFallback(PhraseSet fallback)
    {
        if (fallback.IsEmpty)
            return this;
        return new PhraseSet
        {
            Full = Full ?? fallback.Full,
            Reduced = Reduced ?? fallback.Reduced,
            Glimpse = Glimpse ?? fallback.Glimpse,
            Past = Past ?? fallback.Past,
            Levels = Levels.Count > 0 ? Levels : fallback.Levels,
            _full = _full ?? fallback._full,
            _reduced = _reduced ?? fallback._reduced,
            _glimpse = _glimpse ?? fallback._glimpse,
            _past = _past ?? fallback._past,
            _levels = _levels is { Count: > 0 } ? _levels : fallback._levels,
        };
    }
}
