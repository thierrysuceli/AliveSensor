using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Core;
using AliveSensor.World;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData.Shops;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace AliveSensor.Sensors;

/// <summary>S4 — digging through trash: a new id in the world's CheckedGarbage set (sampled 4×/s).</summary>
internal sealed class TrashSensor
{
    private readonly ModEntry _mod;
    private readonly HashSet<string> _known = new();

    public TrashSensor(ModEntry mod) => _mod = mod;

    public void Reset()
    {
        _known.Clear();
        foreach (string id in Game1.netWorldState.Value.CheckedGarbage)
            _known.Add(id);
        _mod.Log.Debug("Sensor:Trash", $"Baseline: {_known.Count} trash can(s) already checked today.");
    }

    public void Sample()
    {
        ISet<string> checkedToday = Game1.netWorldState.Value.CheckedGarbage;
        if (checkedToday.Count == _known.Count)
            return;

        foreach (string id in checkedToday.ToList())
        {
            if (!_known.Add(id))
                continue;
            if (!_mod.Config.Sensors.Trash || !_mod.Recorder.CanRecord("Trash", out _))
                continue;
            OnTrashChecked(id);
        }
    }

    private void OnTrashChecked(string canId)
    {
        Farmer player = Game1.player;
        GameLocation location = player.currentLocation;
        var can = _mod.Landmarks.GarbageCans(location).FirstOrDefault(c => c.Id == canId);
        Point tile = can.Id is null ? player.TilePoint : can.Tile;
        var owners = _mod.Config.World.TrashOwners.TryGetValue(canId, out var list) ? list : new List<string>();

        _mod.Log.Debug("Sensor:Trash", $"{player.Name} checked trash can '{canId}' at {location.NameOrUniqueName} ({tile.X},{tile.Y}); owners: {(owners.Count == 0 ? "unknown" : string.Join("+", owners))}.");

        var draft = new EventDraft(EventTypes.Trash, "Trash", location, tile) { Target = owners.FirstOrDefault() };
        draft.Involved.AddRange(owners.Skip(1));
        draft.MentionedNpcs.AddRange(owners);
        draft.Payload["canId"] = canId;
        draft.Payload["owners"] = string.Join(",", owners);
        draft.PerWitness = (npc, w) =>
        {
            if (owners.Contains(npc, StringComparer.OrdinalIgnoreCase))
                Grading.ApplyBonus(_mod.Config, w, "trashOwnerWitness");
        };
        _mod.Recorder.Record(draft);
    }
}

/// <summary>S5 — changing maps / entering and leaving places (Player.Warped).</summary>
internal sealed class WarpSensor
{
    private readonly ModEntry _mod;
    private string? _lastLocation;
    private Point _lastTile;
    private string? _previousLocation;
    private Point _previousTile;
    private bool _eventUpLast;
    private bool _eventUpBeforeLocationChange;

    public WarpSensor(ModEntry mod) => _mod = mod;

    /// <summary>
    /// Remember where the player stood, to know which door they used (every tick; no logging).
    /// The tick handler can run after the warp but before SMAPI raises Warped, so the last tile on the
    /// previous map is kept separately when the location changes.
    /// </summary>
    public void Track()
    {
        Farmer player = Game1.player;
        if (player?.currentLocation is null)
            return;
        string location = player.currentLocation.NameOrUniqueName;
        if (_lastLocation is not null && location != _lastLocation)
        {
            _previousLocation = _lastLocation;
            _previousTile = _lastTile;
            _eventUpBeforeLocationChange = _eventUpLast;
        }
        _lastLocation = location;
        _lastTile = player.TilePoint;
        _eventUpLast = Game1.eventUp;
    }

    /// <summary>Was a cutscene already running before this warp? (A cutscene that starts on arrival doesn't count.)</summary>
    private bool EventWasRunningBefore(GameLocation from)
        => _lastLocation == from.NameOrUniqueName ? _eventUpLast : _eventUpBeforeLocationChange;

    private Point ExitTile(GameLocation from, Point fallback)
    {
        string name = from.NameOrUniqueName;
        if (_lastLocation == name)
            return _lastTile;
        if (_previousLocation == name)
            return _previousTile;
        _mod.Log.Verbose("Sensor:Warp", $"No tracked tile on {name}; using arrival tile as fallback.");
        return fallback;
    }

    public void OnWarped(WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer || !_mod.Config.Sensors.MapAndPlace || !_mod.Recorder.CanRecord("Warp", out _, ignoreEvent: true))
            return;

        GameLocation from = e.OldLocation;
        GameLocation to = e.NewLocation;
        if (EventWasRunningBefore(from))
        {
            _mod.Log.Debug("Sensor:Warp", $"{from.NameOrUniqueName} → {to.NameOrUniqueName}: moved by a cutscene; ignored.");
            return;
        }
        if (Game1.eventUp)
            _mod.Log.Debug("Sensor:Warp", $"A cutscene started on arrival at {to.NameOrUniqueName}; the entry is still recorded.");
        Point fromTile = ExitTile(from, e.Player.TilePoint);
        Point toTile = e.Player.TilePoint;

        if (Homes.IsPlayerOwned(from) && Homes.IsPlayerOwned(to))
        {
            _mod.Log.Verbose("Sensor:Warp", $"{from.NameOrUniqueName} → {to.NameOrUniqueName}: both belong to the player; ignored.");
            return;
        }

        bool entering = from.IsOutdoors && !to.IsOutdoors;
        bool leaving = !from.IsOutdoors && to.IsOutdoors;
        string type = entering || leaving ? EventTypes.Place : EventTypes.Map;
        string action = entering ? "enter" : leaving ? "leave" : "move";

        _mod.Log.Debug("Sensor:Warp", $"{e.Player.Name}: {from.NameOrUniqueName} ({fromTile.X},{fromTile.Y}) → {to.NameOrUniqueName} ({toTile.X},{toTile.Y}) = {type}/{action}.");

        GameLocation primary = leaving || type == EventTypes.Map ? from : to;
        Point primaryTile = primary == from ? fromTile : toTile;
        GameLocation other = primary == from ? to : from;
        Point otherTile = primary == from ? toTile : fromTile;

        var draft = new EventDraft(type, "Warp", primary, primaryTile);
        draft.ExtraScopes.Add(new WitnessScope(other, new Vector2(otherTile.X, otherTile.Y)));
        draft.Payload["action"] = action;
        draft.Payload["from"] = from.NameOrUniqueName;
        draft.Payload["to"] = to.NameOrUniqueName;
        draft.Payload["fromDisplay"] = _mod.Landmarks.PlaceName(from.NameOrUniqueName);
        draft.Payload["toDisplay"] = _mod.Landmarks.PlaceName(to.NameOrUniqueName);

        if (type == EventTypes.Place)
        {
            GameLocation place = entering ? to : from;
            var owners = _mod.Homes.OwnersOf(place.Name).ToList();
            draft.Payload["place"] = place.NameOrUniqueName;
            draft.Payload["placeDisplay"] = _mod.Landmarks.PlaceName(place.NameOrUniqueName);
            if (owners.Count > 0)
            {
                draft.Payload["owners"] = string.Join(",", owners);
                draft.Target = owners[0];
                draft.MentionedNpcs.AddRange(owners);
                draft.Aggravators.Add("placeNpcHome");
                foreach (string owner in owners)
                    draft.Direct.Add(owner);
            }
        }
        _mod.Recorder.Record(draft);
    }
}

/// <summary>S8 — purchases: items gained while a ShopMenu is open, recorded once when the shop closes.</summary>
internal sealed class PurchaseSensor
{
    private readonly ModEntry _mod;
    private Session? _session;

    public PurchaseSensor(ModEntry mod) => _mod = mod;

    private sealed class Session
    {
        public string ShopId = "";
        public GameLocation Location = null!;
        public Point Tile;
        public string? Vendor;
        public readonly Dictionary<string, (string Name, int Count, bool Alcohol)> Items = new();
    }

    public void OnMenuChanged(MenuChangedEventArgs e)
    {
        if (e.NewMenu is ShopMenu shop && _session is null && Context.IsWorldReady)
        {
            Farmer player = Game1.player;
            _session = new Session
            {
                ShopId = shop.ShopId ?? "",
                Location = player.currentLocation,
                Tile = player.TilePoint,
                Vendor = FindVendor(shop, player),
            };
            _mod.Log.Debug("Sensor:Purchase", $"Shop '{_session.ShopId}' opened at {_session.Location.NameOrUniqueName}; vendor: {_session.Vendor ?? "none found"}.");
        }
        else if (_session is not null && e.OldMenu is ShopMenu && e.NewMenu is not ShopMenu)
        {
            Finish();
        }
    }

    public void OnInventoryChanged(InventoryChangedEventArgs e)
    {
        if (_session is null || !e.IsLocalPlayer || Game1.activeClickableMenu is not ShopMenu)
            return;
        foreach (Item item in e.Added)
            Add(item, item.Stack);
        foreach (ItemStackSizeChange change in e.QuantityChanged)
        {
            if (change.NewSize > change.OldSize)
                Add(change.Item, change.NewSize - change.OldSize);
        }
    }

    private void Add(Item item, int count)
    {
        bool alcohol = ConsumeSensor.IsAlcohol(_mod, item);
        string key = item.QualifiedItemId;
        _session!.Items[key] = _session.Items.TryGetValue(key, out var current)
            ? (current.Name, current.Count + count, alcohol)
            : (item.DisplayName, count, alcohol);
        _mod.Log.Verbose("Sensor:Purchase", $"+{count} {item.DisplayName} ({key}){(alcohol ? " [alcohol]" : "")}");
    }

    private void Finish()
    {
        Session session = _session!;
        _session = null;
        if (session.Items.Count == 0)
        {
            _mod.Log.Debug("Sensor:Purchase", $"Shop '{session.ShopId}' closed with no purchases.");
            return;
        }
        if (!_mod.Config.Sensors.Purchase || !_mod.Recorder.CanRecord("Purchase", out _))
            return;

        int alcohol = session.Items.Values.Where(i => i.Alcohol).Sum(i => i.Count);
        string items = string.Join(", ", session.Items.Values.Select(i => i.Count > 1 ? $"{i.Count}× {i.Name}" : i.Name));
        var draft = new EventDraft(EventTypes.Purchase, "Purchase", session.Location, session.Tile) { Target = session.Vendor };
        draft.Payload["shop"] = session.ShopId;
        draft.Payload["shopDisplay"] = session.Location.DisplayName;
        draft.Payload["items"] = items;
        draft.Payload["alcohol"] = alcohol.ToString();
        if (session.Vendor is not null)
        {
            draft.Payload["vendor"] = session.Vendor;
            draft.Direct.Add(session.Vendor);
            draft.MentionedNpcs.Add(session.Vendor);
        }
        if (alcohol > 0)
        {
            draft.Aggravators.Add("purchaseDrink");
            int bought = 0;
            for (int i = 0; i < alcohol; i++)
                bought = _mod.Store.IncrementCounter(SDate.Now().DaysSinceStart, "_player", "alcoholBought");
            draft.Payload["alcoholBoughtToday"] = bought.ToString();
            if (bought >= _mod.Config.Grade.ManyDrinksThreshold)
                draft.Aggravators.Add("purchaseManyDrinks");
            // Every bottle bought today counts as a repetition, so 18 beers weigh more than 2.
            draft.PerWitness = (_, w) => w.Count = bought;
        }
        _mod.Log.Debug("Sensor:Purchase", $"Shop '{session.ShopId}' closed: {items}.");
        _mod.Recorder.Record(draft);
    }

    private string? FindVendor(ShopMenu shop, Farmer player)
    {
        GameLocation location = player.currentLocation;
        if (shop.ShopData?.Owners is not null)
        {
            foreach (ShopOwnerData owner in shop.ShopData.Owners)
            {
                if (owner.Type == ShopOwnerType.NamedNpc && !string.IsNullOrWhiteSpace(owner.Name)
                    && location.characters.Any(n => n.Name == owner.Name))
                    return owner.Name;
            }
        }
        NPC? nearest = location.characters
            .Where(n => n.IsVillager)
            .OrderBy(n => Vector2.Distance(n.Tile, player.Tile))
            .FirstOrDefault(n => Vector2.Distance(n.Tile, player.Tile) <= 5);
        return nearest?.Name;
    }
}

/// <summary>S10 — NPCs arriving at a place (World.NpcListChanged). Off by default.</summary>
internal sealed class NpcMoveSensor
{
    private readonly ModEntry _mod;

    public NpcMoveSensor(ModEntry mod) => _mod = mod;

    public void OnNpcListChanged(NpcListChangedEventArgs e)
    {
        if (!_mod.Config.Sensors.NpcMove || !e.Added.Any() || Game1.timeOfDay <= 610 || !_mod.Recorder.CanRecord("NpcMove", out _))
            return;
        if (_mod.Config.World.NpcMoveIndoorsOnly && e.Location.IsOutdoors)
            return;

        foreach (NPC npc in e.Added.Where(n => n.IsVillager))
        {
            var draft = new EventDraft(EventTypes.NpcMove, "NpcMove", e.Location, npc.TilePoint) { Actor = npc.Name };
            draft.Exclude.Add(npc.Name);
            draft.MentionedNpcs.Add(npc.Name);
            draft.Payload["action"] = "arrive";
            draft.Payload["placeDisplay"] = _mod.Landmarks.PlaceName(e.Location.NameOrUniqueName);
            _mod.Recorder.Record(draft);
        }
    }
}

/// <summary>S6 — entering/leaving someone's bedroom, and S6b unlocking it for the first time.</summary>
internal sealed class RoomSensor
{
    /// <summary>A stay shorter than this isn't worth putting into words, so the phrase's optional part drops.</summary>
    private const int MentionableStayMinutes = 10;

    private readonly ModEntry _mod;
    private Room? _current;
    private string? _eventId;
    private int _enteredDay;
    private int _enteredTime;
    private readonly HashSet<string> _unlockedAtStart = new(StringComparer.OrdinalIgnoreCase);

    public RoomSensor(ModEntry mod) => _mod = mod;

    public void Reset()
    {
        _current = null;
        _eventId = null;
        _unlockedAtStart.Clear();
        foreach (string mail in Game1.player.mailReceived)
        {
            if (mail.StartsWith("doorUnlock", StringComparison.Ordinal))
                _unlockedAtStart.Add(mail.Substring("doorUnlock".Length));
        }
        _mod.Log.Debug("Sensor:Room", $"Baseline: bedroom doors already unlocked for {(_unlockedAtStart.Count == 0 ? "nobody" : string.Join(", ", _unlockedAtStart))}.");
    }

    public void Sample()
    {
        if (!Context.IsWorldReady)
            return;
        Farmer player = Game1.player;
        GameLocation location = player.currentLocation;
        Room? room = location is null ? null : _mod.Rooms.Find(location, player.TilePoint);
        if (ReferenceEquals(room, _current))
            return;

        if (_current is not null)
            Exit(_current);
        _current = room;
        if (room is not null)
            Enter(location!, room, player);
    }

    private void Enter(GameLocation location, Room room, Farmer player)
    {
        _eventId = null;
        _enteredDay = SDate.Now().DaysSinceStart;
        _enteredTime = Game1.timeOfDay;
        _mod.Log.Debug("Sensor:Room", $"{player.Name} entered {string.Join("+", room.Owners)}'s bedroom in {location.NameOrUniqueName} at ({player.TilePoint.X},{player.TilePoint.Y}).");

        foreach (string owner in room.Owners.Where(o => player.mailReceived.Contains("doorUnlock" + o) && !_unlockedAtStart.Contains(o)).ToList())
        {
            _unlockedAtStart.Add(owner);
            if (_mod.Config.Sensors.Room && _mod.Recorder.CanRecord("Room", out _))
                _mod.Recorder.Record(BuildDraft(EventTypes.RoomUnlocked, location, room, player.TilePoint));
        }

        if (!_mod.Config.Sensors.Room || !_mod.Recorder.CanRecord("Room", out _))
            return;

        EventDraft draft = BuildDraft(EventTypes.Room, location, room, player.TilePoint);
        if (Game1.timeOfDay >= _mod.Config.Visibility.NightOutdoorFromTime)
            draft.Aggravators.Add("roomAtNight");
        if (room.Owners.Any(o => location.characters.Any(n => n.Name == o)))
            draft.Aggravators.Add("roomOwnerPresent");
        _eventId = _mod.Recorder.Record(draft)?.Id;
    }

    private void Exit(Room room)
    {
        int minutes = (SDate.Now().DaysSinceStart - _enteredDay) * 1200 + GameClock.MinutesBetween(_enteredTime, Game1.timeOfDay);
        _mod.Log.Debug("Sensor:Room", $"Left {string.Join("+", room.Owners)}'s bedroom after {minutes} game minute(s).");
        if (_eventId is null)
            return;

        int longStay = _mod.Config.Grade.RoomLongStayMinutes;
        _mod.Store.Update(_eventId, e =>
        {
            if (minutes >= MentionableStayMinutes)
                e.Payload["stayMinutes"] = minutes.ToString();
            if (minutes >= longStay && _mod.Config.Grade.Aggravators.TryGetValue("roomLongStay", out float multiplier) && !e.Aggravators.Any(a => a.StartsWith("roomLongStay")))
            {
                e.Grade *= multiplier;
                e.Aggravators.Add($"roomLongStay×{multiplier:0.##}");
            }
        }, "left bedroom");
        _eventId = null;
    }

    private static EventDraft BuildDraft(string type, GameLocation location, Room room, Point tile)
    {
        var draft = new EventDraft(type, "Room", location, tile) { Target = room.Owners[0] };
        draft.Involved.AddRange(room.Owners.Skip(1));
        draft.MentionedNpcs.AddRange(room.Owners);
        draft.Payload["owners"] = room.OwnersCsv;
        foreach (string owner in room.Owners)
            draft.Direct.Add(owner);
        return draft;
    }
}
