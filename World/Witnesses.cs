using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Memory;
using Microsoft.Xna.Framework;
using StardewValley;

namespace AliveSensor.World;

/// <summary>A map area where witnesses are looked for, measured from an origin tile.</summary>
internal sealed record WitnessScope(GameLocation Location, Vector2 Origin);

/// <summary>Outcome of checking one NPC, kept for logs and the as_witness dry run.</summary>
internal sealed record WitnessCheck(string Npc, string Location, int Distance, float Visibility, int Level, bool Included, string Reason);

/// <summary>
/// Who perceived an event and how well (PRD §4): visibility = distance × weather × light × attention,
/// then converted to a certainty level 1–5 (PRD §5). Runs on the main thread at the moment of the event.
/// </summary>
internal sealed class WitnessResolver
{
    private readonly Func<ModConfig> _config;
    private readonly EventCatalog _catalog;
    private readonly Log _log;

    /// <summary>Bedroom lookup (set by ModEntry); null disables the walls and asleep-in-bedroom checks.</summary>
    public RoomIndex? Rooms { get; set; }

    /// <summary>
    /// Whether a villager may be written about at all (set by ModEntry from AliveNpcs' eligible list, which
    /// honours both the player's own switches and the community opt-out list). Null allows everyone.
    /// </summary>
    public Func<string, bool>? IsEligible { get; set; }

    public WitnessResolver(Func<ModConfig> config, EventCatalog catalog, Log log)
    {
        _config = config;
        _catalog = catalog;
        _log = log;
    }

    public Dictionary<string, WitnessRecord> Resolve(
        string eventId, string type, IEnumerable<WitnessScope> scopes, ISet<string> direct, ISet<string> exclude,
        out List<WitnessCheck> checks)
    {
        ModConfig c = _config();
        var result = new Dictionary<string, WitnessRecord>(StringComparer.OrdinalIgnoreCase);
        checks = new List<WitnessCheck>();
        EventDefinition definition = _catalog.GetOrDefault(type);
        float range = c.Visibility.RangeTiles.TryGetValue(type, out float r) ? r : definition.RangeTiles;
        float heardFloor = c.Visibility.HeardFloorByType.TryGetValue(type, out float floor) ? floor : definition.Perception.HeardFloor;
        int time = Game1.timeOfDay;
        string save = Game1.uniqueIDForThisGame.ToString();

        foreach (WitnessScope scope in scopes)
        {
            GameLocation location = scope.Location;
            float weather = WeatherFactor(c, location);
            float light = LightFactor(c, location, time);

            foreach (NPC npc in location.characters.ToList())
            {
                string name = npc.Name;
                if (!npc.IsVillager || string.IsNullOrWhiteSpace(name))
                    continue;

                int distance = (int)Math.Round(Vector2.Distance(npc.Tile, scope.Origin));
                bool isDirect = direct.Contains(name);

                if (exclude.Contains(name))
                {
                    checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, "excluded (actor or interlocutor)"));
                    continue;
                }
                if (IsEligible is not null && !IsEligible(name))
                {
                    // Switched off by the player, or on the community opt-out list: their author asked for no
                    // AI content. Nothing is recorded for them, so they cannot surface in any prompt later.
                    checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, "not eligible for AI content in AliveNpcs"));
                    continue;
                }
                if (npc.IsInvisible)
                {
                    checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, "invisible"));
                    continue;
                }
                if (npc.isSleeping.Value)
                {
                    checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, "sleeping"));
                    continue;
                }
                Room? npcRoom = Rooms is null || location.IsOutdoors ? null : Rooms.Find(location, npc.TilePoint);
                if (npcRoom is not null && !definition.Perception.WakesSleepers && time >= c.Visibility.AsleepInBedroomFromTime
                    && npcRoom.Owners.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, $"asleep in their bedroom (after {GameClock.Clock12(c.Visibility.AsleepInBedroomFromTime)})"));
                    continue;
                }
                if (!isDirect && c.Visibility.BedroomWalls && Rooms is not null && !location.IsOutdoors && !definition.Perception.ThroughWalls)
                {
                    Room? eventRoom = Rooms.Find(location, new Point((int)scope.Origin.X, (int)scope.Origin.Y));
                    if (npcRoom != eventRoom)
                    {
                        // Different sides of a bedroom wall: only what happens right at the doorway is visible.
                        Room wallRoom = (npcRoom ?? eventRoom)!;
                        bool atDoorway = (npcRoom is null || eventRoom is null)
                            && Vector2.Distance(new Vector2(wallRoom.Door.X, wallRoom.Door.Y), scope.Origin) <= c.Visibility.DoorwayViewTiles;
                        if (!atDoorway)
                        {
                            string side = npcRoom is not null ? $"inside {string.Join("+", npcRoom.Owners)}'s bedroom" : $"outside {string.Join("+", eventRoom!.Owners)}'s bedroom";
                            checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, $"behind a wall ({side})"));
                            continue;
                        }
                    }
                }
                if (!isDirect && range >= 0 && distance > range)
                {
                    checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, 0, 0, false, $"too far ({distance} > {range:0} tiles)"));
                    continue;
                }

                float distanceFactor = DistanceFactor(c, distance);
                float attention = AttentionFactor(c, npc);
                float visibility;
                string how;
                if (isDirect && c.Visibility.DirectAlwaysKnows && distance <= c.Visibility.DirectRangeTiles)
                {
                    visibility = 1f;
                    how = "direct";
                }
                else
                {
                    visibility = distanceFactor * weather * light * attention;
                    how = $"dist {distanceFactor:0.00} × weather {weather:0.00} × light {light:0.00} × attention {attention:0.00}";
                    if (heardFloor > 0 && visibility < heardFloor)
                    {
                        visibility = heardFloor;
                        how += $" → heard (floor {heardFloor:0.00})";
                    }
                }
                visibility = Math.Clamp(visibility, 0f, 1f);

                if (!isDirect && visibility < c.Visibility.NoticeRollBelow)
                {
                    double roll = StableRandom.Roll(save, eventId, name);
                    if (roll > visibility)
                    {
                        checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, visibility, 0, false, $"didn't notice (roll {roll:0.00} > vis {visibility:0.00}; {how})"));
                        continue;
                    }
                    how += $"; noticed (roll {roll:0.00} ≤ {visibility:0.00})";
                }

                int level = Level(c, type, visibility, isDirect);
                var record = new WitnessRecord { Visibility = visibility, Distance = distance, Direct = isDirect, Level = level };

                // An NPC can appear in two scopes (e.g. leaving a building): keep the better view.
                if (!result.TryGetValue(name, out var existing) || existing.Visibility < visibility)
                    result[name] = record;

                checks.Add(new WitnessCheck(name, location.NameOrUniqueName, distance, visibility, level, true, how));
            }
        }

        if (_log.DebugEnabled)
            _log.Debug("Witness", Describe(eventId, type, checks));
        return result;
    }

    public static string Describe(string eventId, string type, List<WitnessCheck> checks)
    {
        var text = new StringBuilder($"{eventId} ({type}): {checks.Count(c => c.Included)} witness(es) of {checks.Count} NPC(s) checked");
        foreach (WitnessCheck check in checks.OrderByDescending(c => c.Included).ThenBy(c => c.Distance))
        {
            text.Append(check.Included
                ? $"\n    ✔ {check.Npc} @{check.Location} dist {check.Distance} → vis {check.Visibility:0.00} → L{check.Level} ({check.Reason})"
                : $"\n    ✘ {check.Npc} @{check.Location} dist {check.Distance}: {check.Reason}");
        }
        return text.ToString();
    }

    public static int Level(ModConfig c, string type, float visibility, bool direct)
    {
        int level = direct ? 5
            : visibility >= c.Certainty.Level5 ? 5
            : visibility >= c.Certainty.Level4 ? 4
            : visibility >= c.Certainty.Level3 ? 3
            : visibility >= c.Certainty.Level2 ? 2
            : 1;
        if (c.Certainty.MinLevelByType.TryGetValue(type, out int floor))
            level = Math.Max(level, floor);
        return level;
    }

    public static float DistanceFactor(ModConfig c, int distance)
    {
        foreach (DistanceBand band in c.Visibility.DistanceBands)
        {
            if (distance <= band.MaxTiles)
                return band.Factor;
        }
        return c.Visibility.BeyondBandsFactor;
    }

    public static float WeatherFactor(ModConfig c, GameLocation location)
    {
        if (!location.IsOutdoors)
            return 1f;
        string? weather = location.GetWeather()?.Weather;
        return weather switch
        {
            "Rain" => c.Visibility.RainFactor,
            "Storm" or "GreenRain" => c.Visibility.StormFactor,
            "Snow" => c.Visibility.SnowFactor,
            _ => 1f,
        };
    }

    public static float LightFactor(ModConfig c, GameLocation location, int time)
    {
        if (!location.IsOutdoors)
            return 1f;
        if (time >= c.Visibility.LateNightFromTime)
            return c.Visibility.LateNightOutdoorFactor;
        if (time >= c.Visibility.NightOutdoorFromTime)
            return c.Visibility.NightOutdoorFactor;
        return 1f;
    }

    public static float AttentionFactor(ModConfig c, NPC npc)
    {
        if (npc.isMoving())
            return c.Visibility.WalkingFactor;
        if (npc.Sprite?.CurrentAnimation is not null)
            return c.Visibility.BusyFactor;
        return 1f;
    }
}

/// <summary>Relevance ("grau") of an event: type base × event-level aggravators (PRD §6).</summary>
internal static class Grading
{
    public static float Compute(ModConfig c, string type, int time, IEnumerable<string> aggravators, out List<string> applied)
    {
        applied = new List<string>();
        float grade = c.Grade.BaseByType.TryGetValue(type, out float baseGrade) ? baseGrade : 1f;
        var keys = aggravators.ToList();
        if (GameClock.IsLateNight(time))
            keys.Add("lateNight");
        foreach (string key in keys.Distinct())
        {
            if (c.Grade.Aggravators.TryGetValue(key, out float multiplier))
            {
                grade *= multiplier;
                applied.Add($"{key}×{multiplier:0.##}");
            }
        }
        return grade;
    }

    /// <summary>Apply a per-witness aggravator (e.g. it was their own trash can).</summary>
    public static void ApplyBonus(ModConfig c, WitnessRecord witness, string key)
    {
        if (!c.Grade.Aggravators.TryGetValue(key, out float multiplier))
            return;
        witness.Bonus *= multiplier;
        witness.BonusReasons.Add($"{key}×{multiplier:0.##}");
    }
}
