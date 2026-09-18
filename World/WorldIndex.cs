using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Integration;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using xTile.Layers;

namespace AliveSensor.World;

/// <summary>Who lives where (from Data/Characters → Home). Built on the main thread.</summary>
internal sealed class Homes
{
    private readonly Log _log;
    private Dictionary<string, List<string>> _ownersByLocation = new(StringComparer.OrdinalIgnoreCase);

    public Homes(Log log) => _log = log;

    public void Build()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pair in Game1.characterData)
            {
                if (pair.Value?.Home is null)
                    continue;
                foreach (var home in pair.Value.Home)
                {
                    if (string.IsNullOrWhiteSpace(home?.Location))
                        continue;
                    if (!map.TryGetValue(home.Location, out var owners))
                        map[home.Location] = owners = new List<string>();
                    if (!owners.Contains(pair.Key))
                        owners.Add(pair.Key);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Could not read NPC homes from Data/Characters", ex);
        }
        _ownersByLocation = map;
        _log.Debug("World", $"Homes indexed: {map.Count} location(s), e.g. {string.Join("; ", map.Take(5).Select(p => $"{p.Key}={string.Join("+", p.Value)}"))}.");
    }

    public IReadOnlyList<string> OwnersOf(string? locationName)
        => locationName is not null && _ownersByLocation.TryGetValue(locationName, out var owners) ? owners : Array.Empty<string>();

    /// <summary>Farm, farmhouse, cabins, barns, sheds...: places that belong to the player.</summary>
    public static bool IsPlayerOwned(GameLocation? location)
        => location is null || location.IsFarm || location is FarmHouse || location is IslandFarmHouse || location.ParentBuilding is not null;
}

/// <summary>AliveNpcs' fixed family / close-friend table, as a lookup.</summary>
internal sealed class Relationships
{
    private readonly Log _log;
    private volatile Dictionary<string, HashSet<string>> _related = new(StringComparer.OrdinalIgnoreCase);

    public Relationships(Log log) => _log = log;

    public void Build(AliveNpcsBridge bridge)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (a, b, _) in bridge.GetRelationshipPairs())
        {
            Add(map, a, b);
            Add(map, b, a);
        }
        _related = map;
        _log.Debug("World", $"Relationship table loaded from AliveNpcs: {map.Count} NPC(s) with close relations.");
    }

    public bool AreRelated(string a, string? b)
        => b is not null && _related.TryGetValue(a, out var set) && set.Contains(b);

    private static void Add(Dictionary<string, HashSet<string>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var set))
            map[from] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(to);
    }
}

/// <summary>A bedroom discovered from a "Door &lt;owners&gt;" tile action.</summary>
internal sealed class Room
{
    public string Location { get; init; } = "";
    public Point Door { get; init; }
    public List<string> Owners { get; init; } = new();
    public HashSet<Point> Tiles { get; init; } = new();

    public string OwnersCsv => string.Join(",", Owners);
}

/// <summary>
/// Discovers bedrooms automatically: the game gates bedroom doors with the tile action "Door &lt;NPC...&gt;"
/// (checked in GameLocation.performAction / performTouchAction). From each door we flood-fill the walkable
/// tiles on both sides; the side that does not reach the map's exits is the bedroom.
/// </summary>
internal sealed class RoomIndex
{
    private readonly Func<ModConfig> _config;
    private readonly Log _log;
    private readonly Dictionary<string, List<Room>> _byLocation = new(StringComparer.OrdinalIgnoreCase);

    public RoomIndex(Func<ModConfig> config, Log log)
    {
        _config = config;
        _log = log;
    }

    public void Clear() => _byLocation.Clear();

    public IReadOnlyList<Room> For(GameLocation location)
    {
        string key = location.NameOrUniqueName;
        if (!_byLocation.TryGetValue(key, out var rooms))
            _byLocation[key] = rooms = Build(location);
        return rooms;
    }

    public Room? Find(GameLocation location, Point tile)
    {
        if (location.IsOutdoors)
            return null;
        foreach (Room room in For(location))
        {
            if (room.Tiles.Contains(tile))
                return room;
        }
        return null;
    }

    private List<Room> Build(GameLocation location)
    {
        var rooms = new List<Room>();
        if (location.IsOutdoors || location.Map is null)
            return rooms;

        try
        {
            Layer? back = location.Map.GetLayer("Back");
            Layer? buildings = location.Map.GetLayer("Buildings");
            if (back is null || buildings is null)
                return rooms;

            int width = back.LayerWidth;
            int height = back.LayerHeight;

            var exits = new HashSet<Point>();
            foreach (Warp warp in location.warps)
                AddArea(exits, new Point(warp.X, warp.Y), 2);
            foreach (var pair in location.doors.Pairs)
                AddArea(exits, pair.Key, 2);

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    string? action = location.doesTileHaveProperty(x, y, "Action", "Buildings")
                        ?? location.doesTileHaveProperty(x, y, "TouchAction", "Back");
                    if (action is null || !action.StartsWith("Door ", StringComparison.Ordinal))
                        continue;

                    var owners = action.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                    if (owners.Count == 0)
                        continue;

                    var door = new Point(x, y);
                    Room? room = FindRoomBehind(location, back, buildings, width, height, door, exits, owners);
                    if (room is not null && !rooms.Any(r => r.Owners.SequenceEqual(room.Owners) && r.Tiles.SetEquals(room.Tiles)))
                        rooms.Add(room);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Room discovery failed in {location.NameOrUniqueName}", ex);
        }

        if (rooms.Count > 0)
            _log.Debug("Rooms", $"{location.NameOrUniqueName}: {rooms.Count} bedroom(s): {string.Join("; ", rooms.Select(r => $"{string.Join("+", r.Owners)} door ({r.Door.X},{r.Door.Y}) {r.Tiles.Count} tiles"))}.");
        return rooms;
    }

    private Room? FindRoomBehind(GameLocation location, Layer back, Layer buildings, int width, int height, Point door, HashSet<Point> exits, List<string> owners)
    {
        int cap = _config().World.RoomScanMaxTiles;
        var regions = new List<(HashSet<Point> Tiles, bool ReachesExit, bool Capped)>();
        foreach (Point start in Neighbors(door))
        {
            if (!Walkable(back, buildings, width, height, start) || regions.Any(r => r.Tiles.Contains(start)))
                continue;
            var tiles = Flood(back, buildings, width, height, start, door, cap, out bool capped);
            regions.Add((tiles, tiles.Overlaps(exits), capped));
        }

        var candidates = regions.Where(r => !r.ReachesExit && !r.Capped).OrderBy(r => r.Tiles.Count).ToList();
        if (candidates.Count == 0 || regions.Count < 2)
        {
            _log.Verbose("Rooms", $"{location.NameOrUniqueName}: door ({door.X},{door.Y}) for {string.Join("+", owners)} skipped (regions: {string.Join(", ", regions.Select(r => $"{r.Tiles.Count} tiles exit={r.ReachesExit} capped={r.Capped}"))}).");
            return null;
        }

        return new Room
        {
            Location = location.NameOrUniqueName,
            Door = door,
            Owners = owners,
            Tiles = candidates[0].Tiles,
        };
    }

    private static HashSet<Point> Flood(Layer back, Layer buildings, int width, int height, Point start, Point door, int cap, out bool capped)
    {
        var seen = new HashSet<Point> { start };
        var queue = new Queue<Point>();
        queue.Enqueue(start);
        capped = false;
        while (queue.Count > 0)
        {
            Point current = queue.Dequeue();
            foreach (Point next in Neighbors(current))
            {
                if (next == door || seen.Contains(next) || !Walkable(back, buildings, width, height, next))
                    continue;
                seen.Add(next);
                if (seen.Count > cap)
                {
                    capped = true;
                    return seen;
                }
                queue.Enqueue(next);
            }
        }
        return seen;
    }

    private static bool Walkable(Layer back, Layer buildings, int width, int height, Point p)
        => p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height && back.Tiles[p.X, p.Y] is not null && buildings.Tiles[p.X, p.Y] is null;

    private static IEnumerable<Point> Neighbors(Point p)
    {
        yield return new Point(p.X, p.Y - 1);
        yield return new Point(p.X, p.Y + 1);
        yield return new Point(p.X - 1, p.Y);
        yield return new Point(p.X + 1, p.Y);
    }

    private static void AddArea(HashSet<Point> set, Point center, int radius)
    {
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                set.Add(new Point(center.X + dx, center.Y + dy));
    }
}

/// <summary>Reference points near a tile ("near the entrance to the Saloon", "by the trash can outside Gus' place").</summary>
internal sealed class Landmarks
{
    private readonly Func<ModConfig> _config;
    private readonly Homes _homes;
    private readonly RoomIndex _rooms;
    private readonly Log _log;
    private readonly Dictionary<string, List<(Point Tile, string Id)>> _garbageByLocation = new(StringComparer.OrdinalIgnoreCase);

    public Landmarks(Func<ModConfig> config, Homes homes, RoomIndex rooms, Log log)
    {
        _config = config;
        _homes = homes;
        _rooms = rooms;
        _log = log;
    }

    public void Clear() => _garbageByLocation.Clear();

    /// <summary>Trash cans in a location, from "Garbage &lt;id&gt;" tile actions (cached).</summary>
    public IReadOnlyList<(Point Tile, string Id)> GarbageCans(GameLocation location)
    {
        string key = location.NameOrUniqueName;
        if (_garbageByLocation.TryGetValue(key, out var cans))
            return cans;

        cans = new List<(Point, string)>();
        try
        {
            Layer? buildings = location.Map?.GetLayer("Buildings");
            if (buildings is not null)
            {
                for (int x = 0; x < buildings.LayerWidth; x++)
                {
                    for (int y = 0; y < buildings.LayerHeight; y++)
                    {
                        string? action = location.doesTileHaveProperty(x, y, "Action", "Buildings");
                        if (action is not null && action.StartsWith("Garbage ", StringComparison.Ordinal))
                            cans.Add((new Point(x, y), action.Substring("Garbage ".Length).Trim()));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Trash can scan failed in {key}", ex);
        }
        _garbageByLocation[key] = cans;
        if (cans.Count > 0)
            _log.Debug("World", $"{key}: {cans.Count} trash can(s): {string.Join(", ", cans.Select(c => $"{c.Id}@({c.Tile.X},{c.Tile.Y})"))}.");
        return cans;
    }

    /// <summary>Closest reference point within the configured distance, or null.</summary>
    public (string Phrase, int Tiles)? Nearest(GameLocation location, Point tile)
    {
        int max = _config().World.LandmarkMaxTiles;
        var candidates = new List<(string Phrase, double Distance)>();

        try
        {
            foreach (var pair in location.doors.Pairs)
                candidates.Add(($"near the entrance to {PlaceName(pair.Value)}", Distance(tile, pair.Key)));

            foreach (Warp warp in location.warps)
            {
                string phrase = location.IsOutdoors ? $"near the way to {PlaceName(warp.TargetName)}" : $"near the exit to {PlaceName(warp.TargetName)}";
                candidates.Add((phrase, Distance(tile, new Point(warp.X, warp.Y))));
            }

            foreach (var can in GarbageCans(location))
                candidates.Add(($"by the trash can outside {TrashOwnerPhrase(can.Id)} place", Distance(tile, can.Tile)));

            foreach (Room room in _rooms.For(location))
                candidates.Add(($"by {Possessive(room.Owners.Select(DisplayName).ToList())} bedroom door", Distance(tile, room.Door)));
        }
        catch (Exception ex)
        {
            _log.Verbose("World", $"Landmark lookup failed in {location.NameOrUniqueName}: {ex.Message}");
        }

        var best = candidates.Where(c => c.Distance <= max).OrderBy(c => c.Distance).FirstOrDefault();
        return best.Phrase is null ? null : (best.Phrase, (int)Math.Round(best.Distance));
    }

    /// <summary>Display name of a location, with its residents when it is someone's home.</summary>
    public string PlaceName(string locationName)
    {
        string display = Game1.getLocationFromName(locationName)?.DisplayName ?? locationName;
        var owners = _homes.OwnersOf(locationName);
        return owners.Count == 0 ? display : $"{display} ({Possessive(owners.Select(DisplayName).ToList())} home)";
    }

    public string TrashOwnerPhrase(string canId)
        => _config().World.TrashOwners.TryGetValue(canId, out var owners) && owners.Count > 0
            ? Possessive(owners.Select(DisplayName).ToList())
            : $"the {canId}";

    public static string DisplayName(string npc) => Game1.getCharacterFromName(npc)?.displayName ?? npc;

    private static string Possessive(IReadOnlyList<string> names)
    {
        string joined = Text.JoinAnd(names);
        return joined.EndsWith("s", StringComparison.Ordinal) ? joined + "'" : joined + "'s";
    }

    private static double Distance(Point a, Point b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
}
