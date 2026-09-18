using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Core;
using StardewModdingAPI;

namespace AliveSensor.Config;

/// <summary>All settings. Every design default from the PRD lives here and can be changed in config.json or GMCM.</summary>
public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;

    public DeliveryConfig Delivery { get; set; } = new();
    public SensorConfig Sensors { get; set; } = new();
    public VisibilityConfig Visibility { get; set; } = new();
    public CertaintyConfig Certainty { get; set; } = new();
    public GradeConfig Grade { get; set; } = new();
    public ScoringConfig Scoring { get; set; } = new();
    public TalkConfig Talk { get; set; } = new();
    public CycleConfig Cycle { get; set; } = new();
    public WorldConfig World { get; set; } = new();
    public ConfrontConfig Confront { get; set; } = new();
    public DebugConfig Debug { get; set; } = new();

    /// <summary>
    /// Clamp values to valid ranges and fill in anything missing, using the event catalog for the per-type
    /// defaults so a type added in JSON gets its settings without any code change. Returns the corrections made.
    /// </summary>
    internal int Normalize(EventCatalog catalog)
    {
        int fixes = 0;
        Delivery ??= new(); Sensors ??= new(); Visibility ??= new(); Certainty ??= new();
        Grade ??= new(); Scoring ??= new(); Talk ??= new(); Cycle ??= new(); World ??= new(); Confront ??= new(); Debug ??= new();

        World.TrashOwners ??= WorldConfig.DefaultTrashOwners();
        World.AlcoholItemIds ??= WorldConfig.DefaultAlcohol();
        fixes += Clamp(World.LandmarkMaxTiles, 1, 30, v => World.LandmarkMaxTiles = v);
        fixes += Clamp(World.TalkMergeMinutes, 0, 600, v => World.TalkMergeMinutes = v);
        fixes += Clamp(World.LockedDoorCooldownSeconds, 1, 600, v => World.LockedDoorCooldownSeconds = v);
        fixes += Clamp(World.RoomScanMaxTiles, 50, 5000, v => World.RoomScanMaxTiles = v);

        fixes += Clamp(Delivery.MaxPerNpc, 1, 15, v => Delivery.MaxPerNpc = v);
        fixes += Clamp(Delivery.MaxChars, 200, 1800, v => Delivery.MaxChars = v);
        if (Delivery.PromptLanguage is not ("en" or "pt-BR")) { Delivery.PromptLanguage = "en"; fixes++; }

        fixes += Clamp(Visibility.NightOutdoorFromTime, 1800, 2400, v => Visibility.NightOutdoorFromTime = v);
        fixes += Clamp(Visibility.LateNightFromTime, 2000, 2600, v => Visibility.LateNightFromTime = v);
        fixes += Clamp01(Visibility.NightOutdoorFactor, v => Visibility.NightOutdoorFactor = v);
        fixes += Clamp01(Visibility.LateNightOutdoorFactor, v => Visibility.LateNightOutdoorFactor = v);
        fixes += Clamp01(Visibility.RainFactor, v => Visibility.RainFactor = v);
        fixes += Clamp01(Visibility.StormFactor, v => Visibility.StormFactor = v);
        fixes += Clamp01(Visibility.SnowFactor, v => Visibility.SnowFactor = v);
        fixes += Clamp01(Visibility.WalkingFactor, v => Visibility.WalkingFactor = v);
        fixes += Clamp01(Visibility.BusyFactor, v => Visibility.BusyFactor = v);
        fixes += Clamp01(Visibility.NoticeRollBelow, v => Visibility.NoticeRollBelow = v);
        fixes += Clamp(Visibility.DoorwayViewTiles, 0, 10, v => Visibility.DoorwayViewTiles = v);
        fixes += Clamp(Visibility.AsleepInBedroomFromTime, 1800, 2700, v => Visibility.AsleepInBedroomFromTime = v);
        fixes += Clamp(Visibility.DirectRangeTiles, 1, 60, v => Visibility.DirectRangeTiles = v);
        if (Visibility.DistanceBands is null || Visibility.DistanceBands.Count == 0)
        {
            Visibility.DistanceBands = VisibilityConfig.DefaultBands();
            fixes++;
        }
        Visibility.DistanceBands = Visibility.DistanceBands
            .Select(b => new DistanceBand { MaxTiles = Math.Max(0, b.MaxTiles), Factor = Math.Clamp(b.Factor, 0f, 1f) })
            .OrderBy(b => b.MaxTiles)
            .ToList();
        fixes += Clamp01(Visibility.BeyondBandsFactor, v => Visibility.BeyondBandsFactor = v);
        fixes += FillMissing(catalog, Visibility.RangeTiles, def => def.RangeTiles);
        fixes += FillMissing(catalog, Visibility.HeardFloorByType, def => def.Perception.HeardFloor);

        // Certainty thresholds must stay strictly descending: L5 > L4 > L3 > L2 > 0.
        Certainty.Level5 = Math.Clamp(Certainty.Level5, 0.05f, 1f);
        Certainty.Level4 = Math.Clamp(Certainty.Level4, 0.04f, Certainty.Level5 - 0.01f);
        Certainty.Level3 = Math.Clamp(Certainty.Level3, 0.03f, Certainty.Level4 - 0.01f);
        Certainty.Level2 = Math.Clamp(Certainty.Level2, 0.02f, Certainty.Level3 - 0.01f);
        Certainty.MinLevelByType ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (EventDefinition def in catalog.All)
        {
            if (def.MinCertainty > 1 && !Certainty.MinLevelByType.ContainsKey(def.Id)) { Certainty.MinLevelByType[def.Id] = def.MinCertainty; fixes++; }
        }
        foreach (string key in Certainty.MinLevelByType.Keys.ToList())
            Certainty.MinLevelByType[key] = Math.Clamp(Certainty.MinLevelByType[key], 1, 5);

        fixes += FillMissing(catalog, Grade.BaseByType, def => def.Grade);
        Grade.Aggravators ??= new(GradeConfig.DefaultAggravators(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GradeConfig.DefaultAggravators())
        {
            if (!Grade.Aggravators.ContainsKey(pair.Key)) { Grade.Aggravators[pair.Key] = pair.Value; fixes++; }
        }
        foreach (string key in Grade.Aggravators.Keys.ToList())
            Grade.Aggravators[key] = Math.Clamp(Grade.Aggravators[key], 1f, 5f);
        fixes += Clamp(Grade.ManyDrinksThreshold, 2, 20, v => Grade.ManyDrinksThreshold = v);
        fixes += Clamp(Grade.PartnerNearbyTiles, 1, 20, v => Grade.PartnerNearbyTiles = v);
        fixes += Clamp(Grade.RoomLongStayMinutes, 10, 600, v => Grade.RoomLongStayMinutes = v);

        fixes += FillMissing(catalog, Scoring.HalfLifeDaysByType, def => def.HalfLifeDays);
        foreach (string key in Scoring.HalfLifeDaysByType.Keys.ToList())
            Scoring.HalfLifeDaysByType[key] = Math.Clamp(Scoring.HalfLifeDaysByType[key], 0.25f, 112f);
        Scoring.MinScore = Math.Clamp(Scoring.MinScore, 0f, 10f);
        Scoring.VictimMultiplier = Math.Clamp(Scoring.VictimMultiplier, 1f, 5f);
        Scoring.RelativeMultiplier = Math.Clamp(Scoring.RelativeMultiplier, 1f, 5f);
        Scoring.VendorMultiplier = Math.Clamp(Scoring.VendorMultiplier, 1f, 5f);
        Scoring.CompactMaxGrade = Math.Clamp(Scoring.CompactMaxGrade, 0f, 10f);
        Scoring.RepeatStep = Math.Clamp(Scoring.RepeatStep, 0f, 3f);
        Scoring.RepeatMaxMultiplier = Math.Clamp(Scoring.RepeatMaxMultiplier, 1f, 10f);
        Scoring.NotableScore = Math.Clamp(Scoring.NotableScore, 0f, 100f);
        Scoring.NotableMaxAgeDays = Math.Clamp(Scoring.NotableMaxAgeDays, 0f, 28f);
        Scoring.NotableMaxPerPrompt = Math.Clamp(Scoring.NotableMaxPerPrompt, 0, 5);

        fixes += Clamp(Confront.MaxReactionsPerEvent, 0, 20, v => Confront.MaxReactionsPerEvent = v);
        fixes += Clamp(Confront.TriggerTiles, 1, 30, v => Confront.TriggerTiles = v);
        fixes += Clamp(Confront.CooldownMinutes, 0, 1200, v => Confront.CooldownMinutes = v);
        fixes += Clamp(Confront.PendingExpiresMinutes, 10, 1200, v => Confront.PendingExpiresMinutes = v);
        fixes += Clamp(Confront.ReasonLastsMinutes, 10, 1200, v => Confront.ReasonLastsMinutes = v);
        fixes += Clamp(Confront.DelayTicks, 0, 600, v => Confront.DelayTicks = v);
        fixes += Clamp(Confront.MaxReasonLines, 1, 6, v => Confront.MaxReasonLines = v);
        fixes += Clamp(Confront.EmoteRangeTiles, 1, 60, v => Confront.EmoteRangeTiles = v);
        fixes += Clamp(Confront.EmoteRepeatSeconds, 2, 120, v => Confront.EmoteRepeatSeconds = v);
        fixes += Clamp(Scoring.RetentionSeasons, 1, 16, v => Scoring.RetentionSeasons = v);
        Scoring.ForgetBelowScore = Math.Clamp(Scoring.ForgetBelowScore, 0f, 1f);

        fixes += Clamp(Talk.SnippetFullChars, 20, 300, v => Talk.SnippetFullChars = v);
        fixes += Clamp(Talk.SnippetTopicChars, 10, 120, v => Talk.SnippetTopicChars = v);

        fixes += Clamp(Cycle.MaxEvents, 1, 40, v => Cycle.MaxEvents = v);
        fixes += Clamp(Cycle.MaxChars, 300, 3000, v => Cycle.MaxChars = v);
        fixes += Clamp(Cycle.InjectGossipCount, 0, 15, v => Cycle.InjectGossipCount = v);
        Cycle.InjectGossipMinWeight = Math.Clamp(Cycle.InjectGossipMinWeight, 0f, 100f);

        fixes += Clamp(Debug.OverlaySeconds, 1, 30, v => Debug.OverlaySeconds = v);
        return fixes;
    }

    private static int Clamp(int value, int min, int max, Action<int> set)
    {
        int clamped = Math.Clamp(value, min, max);
        if (clamped == value)
            return 0;
        set(clamped);
        return 1;
    }

    private static int Clamp01(float value, Action<float> set)
    {
        float clamped = Math.Clamp(value, 0f, 1f);
        if (Math.Abs(clamped - value) < 0.0001f)
            return 0;
        set(clamped);
        return 1;
    }

    /// <summary>Give every catalog type an entry, without touching values the player already set.</summary>
    private static int FillMissing(EventCatalog catalog, Dictionary<string, float> map, Func<EventDefinition, float> defaultValue)
    {
        int fixes = 0;
        foreach (EventDefinition def in catalog.All)
        {
            if (map.ContainsKey(def.Id))
                continue;
            map[def.Id] = defaultValue(def);
            fixes++;
        }
        return fixes;
    }
}

public sealed class DeliveryConfig
{
    /// <summary>Channel A: witnessed memories inside each NPC's conversation prompt.</summary>
    public bool NpcBlockEnabled { get; set; } = true;
    public int MaxPerNpc { get; set; } = 5;
    public int MaxChars { get; set; } = 1800;
    /// <summary>Language of the text we inject ("en" recommended: the rest of the AliveNpcs prompt is English).</summary>
    public string PromptLanguage { get; set; } = "en";
}

public sealed class SensorConfig
{
    public bool Bomb { get; set; } = true;
    public bool SleepOutside { get; set; } = true;
    public bool Gift { get; set; } = true;
    public bool Trash { get; set; } = true;
    public bool MapAndPlace { get; set; } = true;
    public bool Room { get; set; } = true;
    public bool LockedDoor { get; set; } = true;
    public bool Talk { get; set; } = true;
    public bool Purchase { get; set; } = true;
    public bool Consume { get; set; } = true;
    public bool NpcMove { get; set; } = false;
}

public sealed class DistanceBand
{
    public int MaxTiles { get; set; }
    public float Factor { get; set; }
}

public sealed class VisibilityConfig
{
    public List<DistanceBand> DistanceBands { get; set; } = DefaultBands();
    public float BeyondBandsFactor { get; set; } = 0.25f;

    /// <summary>Max witness distance per event type, in tiles (-1 = whole map).</summary>
    public Dictionary<string, float> RangeTiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public float RainFactor { get; set; } = 0.8f;
    public float StormFactor { get; set; } = 0.7f;
    public float SnowFactor { get; set; } = 0.9f;

    public int NightOutdoorFromTime { get; set; } = 2000;
    public float NightOutdoorFactor { get; set; } = 0.7f;
    public int LateNightFromTime { get; set; } = 2400;
    public float LateNightOutdoorFactor { get; set; } = 0.55f;

    public float WalkingFactor { get; set; } = 0.9f;
    public float BusyFactor { get; set; } = 0.95f;

    /// <summary>Victim, receiver, vendor and room owner always perceive with full visibility.</summary>
    public bool DirectAlwaysKnows { get; set; } = true;
    /// <summary>How far "always knows" reaches (tiles). Beyond this, even a room owner falls back to normal distance-based visibility — a shopkeeper at the counter doesn't automatically know someone rattled their bedroom door across the building.</summary>
    public int DirectRangeTiles { get; set; } = 15;
    /// <summary>Below this visibility, a seeded roll decides whether the NPC noticed at all.</summary>
    public float NoticeRollBelow { get; set; } = 0.45f;
    /// <summary>Minimum visibility for anyone on the map when a bomb goes off (they hear it).</summary>
    public Dictionary<string, float> HeardFloorByType { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bedroom walls block sight: someone inside a bedroom can't see the hallway and vice versa (explosions are still heard).</summary>
    public bool BedroomWalls { get; set; } = true;
    /// <summary>Through a bedroom's doorway you still see events this close to the door.</summary>
    public int DoorwayViewTiles { get; set; } = 2;
    /// <summary>From this time, an NPC in their own bedroom counts as asleep (the game doesn't always flag it).</summary>
    public int AsleepInBedroomFromTime { get; set; } = 2300;

    internal static List<DistanceBand> DefaultBands() => new()
    {
        new DistanceBand { MaxTiles = 2, Factor = 1f },
        new DistanceBand { MaxTiles = 5, Factor = 0.85f },
        new DistanceBand { MaxTiles = 8, Factor = 0.65f },
        new DistanceBand { MaxTiles = 12, Factor = 0.45f },
    };
}

public sealed class CertaintyConfig
{
    public float Level5 { get; set; } = 0.85f;
    public float Level4 { get; set; } = 0.65f;
    public float Level3 { get; set; } = 0.45f;
    public float Level2 { get; set; } = 0.25f;

    /// <summary>Lowest certainty level a witness can have for a given event type (e.g. everyone hears a bomb).</summary>
    public Dictionary<string, int> MinLevelByType { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GradeConfig
{
    /// <summary>Base relevance ("grau") per event type.</summary>
    public Dictionary<string, float> BaseByType { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Relevance multipliers applied when a condition holds.</summary>
    public Dictionary<string, float> Aggravators { get; set; } = new(DefaultAggravators(), StringComparer.OrdinalIgnoreCase);

    public int ManyDrinksThreshold { get; set; } = 3;
    public int PartnerNearbyTiles { get; set; } = 5;
    public int RoomLongStayMinutes { get; set; } = 30;

    internal static Dictionary<string, float> DefaultAggravators() => new()
    {
        ["lateNight"] = 1.2f,
        ["bombNearHome"] = 1.3f,
        ["roomAtNight"] = 1.3f,
        ["roomOwnerPresent"] = 1.5f,
        ["roomLongStay"] = 1.2f,
        ["sleepNearHome"] = 1.3f,
        ["trashOwnerWitness"] = 1.5f,
        ["giftLovedOrHated"] = 1.3f,
        ["giftPartnerNearby"] = 2.0f,
        ["giftBirthday"] = 1.2f,
        ["lockedRepeated"] = 1.5f,
        ["talkNegativeEmotion"] = 1.3f,
        ["talkRomantic"] = 1.3f,
        ["purchaseDrink"] = 1.3f,
        ["purchaseManyDrinks"] = 1.5f,
        ["drinkOutsideSaloon"] = 1.5f,
        ["manyDrinksSeen"] = 2.0f,
        ["placeNpcHome"] = 1.5f,
    };
}

public sealed class ScoringConfig
{
    public Dictionary<string, float> HalfLifeDaysByType { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public float MinScore { get; set; } = 0.3f;
    public float VictimMultiplier { get; set; } = 2.0f;
    public float RelativeMultiplier { get; set; } = 1.5f;
    public float VendorMultiplier { get; set; } = 1.3f;
    /// <summary>Same-day events with grade at or below this are merged into one line.</summary>
    public float CompactMaxGrade { get; set; } = 2f;
    /// <summary>Each extra time a witness saw the same thing adds this much to the score multiplier (1 + step × (times − 1)).</summary>
    public float RepeatStep { get; set; } = 0.5f;
    /// <summary>Cap on the repetition multiplier.</summary>
    public float RepeatMaxMultiplier { get; set; } = 3f;
    /// <summary>Recent memories at or above this score are marked as standing out, so the NPC brings them up unprompted.</summary>
    public float NotableScore { get; set; } = 4f;
    /// <summary>Only memories younger than this (in days) can stand out.</summary>
    public float NotableMaxAgeDays { get; set; } = 1f;
    /// <summary>How many memories per prompt can be marked as standing out (the strongest first).</summary>
    public int NotableMaxPerPrompt { get; set; } = 1;
    public int RetentionSeasons { get; set; } = 2;
    public float ForgetBelowScore { get; set; } = 0.05f;
}

public sealed class TalkConfig
{
    public bool RespectKeepSecret { get; set; } = true;
    public int SnippetFullChars { get; set; } = 80;
    public int SnippetTopicChars { get; set; } = 30;
}

public sealed class CycleConfig
{
    /// <summary>Channel B: the day's witnessed events inside the nightly gossip/diary prompt.</summary>
    public bool CycleBlockEnabled { get; set; } = true;
    public int MaxEvents { get; set; } = 12;
    public int MaxChars { get; set; } = 1800;
    /// <summary>Channel C: reinforce the most relevant events through AliveNpcs' InjectGossip.</summary>
    public bool InjectGossip { get; set; } = true;
    public int InjectGossipCount { get; set; } = 3;
    /// <summary>Minimum gossip weight (grade × repetition × witnesses) for an event to be reinforced as gossip.</summary>
    public float InjectGossipMinWeight { get; set; } = 6f;
}

/// <summary>World knowledge used by sensors. Lists are edited in config.json.</summary>
public sealed class WorldConfig
{
    /// <summary>Trash can id (the "Garbage &lt;id&gt;" tile action) → NPCs who live there.</summary>
    public Dictionary<string, List<string>> TrashOwners { get; set; } = DefaultTrashOwners();

    /// <summary>Item ids treated as alcohol (qualified "(O)346" or unqualified "346").</summary>
    public List<string> AlcoholItemIds { get; set; } = DefaultAlcohol();

    /// <summary>NPC coming/going sensor only records indoor places.</summary>
    public bool NpcMoveIndoorsOnly { get; set; } = true;

    /// <summary>How far to look for a nearby reference point ("near the Saloon entrance").</summary>
    public int LandmarkMaxTiles { get; set; } = 8;

    /// <summary>Consecutive replies to the same NPC within this many game minutes are one conversation.</summary>
    public int TalkMergeMinutes { get; set; } = 60;

    /// <summary>Explosions within this many game minutes and tiles of a recorded one merge into it (with a count).</summary>
    public int BombMergeMinutes { get; set; } = 30;
    public int BombMergeTiles { get; set; } = 15;

    /// <summary>Eating/drinking the same item on the same map within this many game minutes merges into one memory.</summary>
    public int ConsumeMergeMinutes { get; set; } = 60;

    /// <summary>Ignore repeated locked-door messages for the same door within this many real seconds.</summary>
    public int LockedDoorCooldownSeconds { get; set; } = 3;

    /// <summary>Largest area (tiles) a bedroom can have when discovering rooms from door tiles.</summary>
    public int RoomScanMaxTiles { get; set; } = 900;

    internal static Dictionary<string, List<string>> DefaultTrashOwners() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["JodiAndKent"] = new() { "Jodi", "Kent", "Sam", "Vincent" },
        ["EmilyAndHaley"] = new() { "Emily", "Haley" },
        ["Mayor"] = new() { "Lewis" },
        ["Museum"] = new() { "Gunther" },
        ["Blacksmith"] = new() { "Clint" },
        ["Saloon"] = new() { "Gus" },
        ["Evelyn"] = new() { "Evelyn", "George", "Alex" },
        ["JojaMart"] = new() { "Morris" },
    };

    /// <summary>Beer, Wine, Pale Ale, Mead, Piña Colada.</summary>
    internal static List<string> DefaultAlcohol() => new() { "346", "348", "303", "459", "873" };
}

/// <summary>NPCs walking up to the farmer when what they witnessed piles up (saturation).</summary>
/// <summary>
/// How NPCs act on what they feel. The feelings themselves — their levels, decay, bubble and words — live in
/// <c>assets/meters.json</c>, so this page only holds the knobs that apply to all of them.
/// </summary>
public sealed class ConfrontConfig
{
    /// <summary>NPCs act on what they feel: showing a bubble, walking over, opening a conversation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many NPCs one event may send over. Each one is an AI call, so this is the token budget as much as a
    /// design choice: everyone else who felt it keeps it for the next conversation, which costs nothing extra.
    /// </summary>
    public int MaxReactionsPerEvent { get; set; } = 3;

    /// <summary>The NPC acts when the farmer is on the same map within this many tiles.</summary>
    public int TriggerTiles { get; set; } = 8;

    /// <summary>In-game minutes between two reactions, whoever they come from.</summary>
    public int CooldownMinutes { get; set; } = 60;

    /// <summary>Someone waiting for the farmer to come close gives up after this many in-game minutes.</summary>
    public int PendingExpiresMinutes { get; set; } = 180;

    /// <summary>How long the reason stays in that NPC's prompts, covering the reply turns.</summary>
    public int ReasonLastsMinutes { get; set; } = 120;

    /// <summary>Ticks between the bubble and the conversation opening (60 ≈ 1 second).</summary>
    public int DelayTicks { get; set; } = 60;

    /// <summary>Memories quoted as the reason.</summary>
    public int MaxReasonLines { get; set; } = 3;

    /// <summary>Show the bubble while an NPC waits their turn, so the farmer knows someone wants a word.</summary>
    public bool EmoteWhileWaiting { get; set; } = true;

    public int EmoteRangeTiles { get; set; } = 12;

    public int EmoteRepeatSeconds { get; set; } = 8;
}

public sealed class DebugConfig
{
    /// <summary>Detailed logs in the SMAPI console (categories, in-game clock, thread marker).</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Also log high-frequency sampling (log file only).</summary>
    public bool Verbose { get; set; } = false;
    public bool Overlay { get; set; } = false;
    public SButton OverlayKey { get; set; } = SButton.F11;
    public int OverlaySeconds { get; set; } = 3;
}
