using System;
using System.Collections.Generic;
using System.Linq;

namespace AliveSensor.Core;

/// <summary>
/// "When this happens in front of that person, feel this instead." A rule is checked once per witness at the
/// moment the event is recorded, so everything it needs (who is nearby, the weather, the friendship) is read
/// while the game is still on the main thread and then stored with the memory.
///
/// Every matching rule applies its multipliers, in the order written. Wording and rewards come from the first
/// matching rule that declares them, so put your most specific rules first.
/// </summary>
internal sealed class EventRule
{
    /// <summary>Shown in logs and stored on the witness, so you can see which rule shaped a memory.</summary>
    public string Name { get; set; } = "";

    public RuleConditions When { get; set; } = new();

    /// <summary>Meter id → factor applied to that meter's weight for this witness. 0 silences a meter entirely.</summary>
    public Dictionary<string, float> Multiply { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Meter id → weight that replaces the type's own, for witnesses this rule matches.</summary>
    public Dictionary<string, float> Set { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wording this witness uses for the memory, replacing the type's own.</summary>
    public PhraseSet? Phrases { get; set; }

    /// <summary>This witness keeps score but never brings it up — useful for a feeling that only matters later.</summary>
    public bool Silent { get; set; }

    /// <summary>Something the NPC hands over when they act on it, like a round on the house.</summary>
    public RuleReward? Reward { get; set; }

    public IReadOnlyList<string> Compile(string typeId, int index)
    {
        var problems = new List<string>();
        if (Name.Length == 0)
            Name = $"{typeId}#{index + 1}";
        Phrases?.Compile(typeId, $"rules.{Name}", (IList<string>)problems);
        if (Reward is not null)
            problems.AddRange(Reward.Validate(typeId, Name));
        if (Multiply.Count == 0 && Set.Count == 0 && Phrases is null && Reward is null && !Silent)
            problems.Add($"'{typeId}' rule '{Name}' does nothing.");
        return problems;
    }
}

/// <summary>
/// What has to be true for a rule to apply. Every condition written must hold; conditions left out are ignored.
/// All of them are read at the moment the event happens.
/// </summary>
internal sealed class RuleConditions
{
    /// <summary>Only these NPCs feel it this way.</summary>
    public List<string> Witness { get; set; } = new();

    /// <summary>Only these NPCs are excluded.</summary>
    public List<string> NotWitness { get; set; } = new();

    /// <summary>The witness's part in it: "target", "vendor", "relative" or "bystander".</summary>
    public List<string> Role { get; set; } = new();

    /// <summary>Payload keys that must match, e.g. <c>{ "kind": "alcohol" }</c>. An empty value only checks the key exists.</summary>
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Someone of this age has to be close by: "child", "teen" or "adult".</summary>
    public NearbyAgeCondition? NearbyAge { get; set; }

    /// <summary>Weather at the time: Sun, Rain, Storm, Snow, Wind, Festival.</summary>
    public List<string> Weather { get; set; } = new();

    public List<string> Season { get; set; } = new();

    /// <summary>Day of the week: Monday..Sunday. Stardew's calendar keeps a fixed 7-day cycle, so this is stable across years.</summary>
    public List<string> Weekday { get; set; } = new();

    /// <summary>Location name, e.g. "Saloon".</summary>
    public List<string> Location { get; set; } = new();

    public bool? Outdoors { get; set; }

    /// <summary>In-game clock, inclusive, e.g. 1800–2600 for the evening.</summary>
    public int? TimeFrom { get; set; }
    public int? TimeTo { get; set; }

    /// <summary>Hearts between the farmer and this witness.</summary>
    public int? MinHearts { get; set; }
    public int? MaxHearts { get; set; }

    /// <summary>Certainty of this witness, 1–5.</summary>
    public int? MinCertainty { get; set; }

    public bool IsEmpty =>
        Witness.Count == 0 && NotWitness.Count == 0 && Role.Count == 0 && Payload.Count == 0 && NearbyAge is null
        && Weather.Count == 0 && Season.Count == 0 && Weekday.Count == 0 && Location.Count == 0 && Outdoors is null
        && TimeFrom is null && TimeTo is null && MinHearts is null && MaxHearts is null && MinCertainty is null;
}

/// <summary>"Is there a child within eight tiles?" — the kind of thing that changes how an adult reacts.</summary>
internal sealed class NearbyAgeCondition
{
    /// <summary>"child", "teen" or "adult".</summary>
    public string Age { get; set; } = "child";

    public int Tiles { get; set; } = 8;

    /// <summary>Set false to require that nobody of that age is around.</summary>
    public bool Present { get; set; } = true;

    /// <summary>The game's age number for this name, or null when the name is unknown.</summary>
    public int? AgeValue => Age?.ToLowerInvariant() switch
    {
        "adult" => 0,
        "teen" => 1,
        "child" => 2,
        _ => null,
    };
}

/// <summary>Something handed to the farmer when the NPC acts — Gus buying a round, say.</summary>
internal sealed class RuleReward
{
    /// <summary>Qualified item id, e.g. "(O)346" for beer.</summary>
    public string Item { get; set; } = "";

    /// <summary>How many, per meter level. One entry means the same amount at every level.</summary>
    public List<int> CountByTier { get; set; } = new() { 1 };

    /// <summary>Which meter's level decides the amount; defaults to the meter that made the NPC speak.</summary>
    public string? Meter { get; set; }

    public int CountFor(int tier)
    {
        if (CountByTier.Count == 0)
            return 0;
        return CountByTier[Math.Clamp(tier, 1, CountByTier.Count) - 1];
    }

    public IReadOnlyList<string> Validate(string typeId, string ruleName)
    {
        var problems = new List<string>();
        if (Item.Length == 0)
            problems.Add($"'{typeId}' rule '{ruleName}' has a reward with no item id.");
        if (CountByTier.Any(count => count < 0))
            problems.Add($"'{typeId}' rule '{ruleName}' has a negative reward count.");
        return problems;
    }
}

/// <summary>What the rules decided for one witness: the weights that count, the wording, and any reward.</summary>
internal sealed record RuleOutcome(
    IReadOnlyDictionary<string, float> Meters,
    IReadOnlyList<string> MatchedRules,
    PhraseSet? Phrases,
    RuleReward? Reward,
    bool Silent)
{
    public static readonly RuleOutcome Empty = new(
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>(), null, null, false);

    /// <summary>The feeling this event left strongest in that witness — what the memory is coloured by.</summary>
    public string? Dominant
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
}
