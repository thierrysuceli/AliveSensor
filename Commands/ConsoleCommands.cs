using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AliveSensor.Core;
using AliveSensor.Delivery;
using AliveSensor.Memory;
using AliveSensor.World;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace AliveSensor.Commands;

/// <summary>Console commands (PRD §13). Read-only unless the description says otherwise.</summary>
internal sealed class ConsoleCommands
{
    private readonly ModEntry _mod;

    public ConsoleCommands(ModEntry mod) => _mod = mod;

    public void Register(ICommandHelper commands)
    {
        Add(commands, "as_help", "List AliveSensor commands.", _ => Help());
        Add(commands, "as_status", "Sensors, AliveNpcs/SpaceCore hooks, save data and settings.", _ => Status());
        Add(commands, "as_debug", "Toggle detailed logs (writes config). Usage: as_debug [on|off] [verbose]", Debug);
        Add(commands, "as_day", "Events recorded on a day with witnesses and levels. Usage: as_day [days ago, default 0]", Day);
        Add(commands, "as_list", "What an NPC remembers, rendered as the AI would read it. Usage: as_list <NPC>", List);
        Add(commands, "as_score", "Score breakdown of every memory an NPC holds (grade × recency × involvement × visibility × bonus × repetition). Usage: as_score <NPC>", Score);
        Add(commands, "as_prompt", "Exact block injected into an NPC's conversation prompt. Usage: as_prompt <NPC>", Prompt);
        Add(commands, "as_cycle", "Exact block injected into tonight's gossip prompt. Usage: as_cycle [days ago]", Cycle);
        Add(commands, "as_where", "Where you are: map, tile, weather, nearby landmark, bedroom, and NPCs with distance/visibility.", _ => Where());
        Add(commands, "as_witness", "Dry run: who would witness an event of this type right here (records nothing). Usage: as_witness <type>", Witness);
        Add(commands, "as_simulate", "RECORDS a test event at your position. Usage: as_simulate <type> [target NPC] [item]", Simulate);
        Add(commands, "as_rooms", "Bedrooms discovered from door tiles. Usage: as_rooms [location name]", Rooms);
        Add(commands, "as_trash", "Trash cans on this map: id, tile, owners, checked today.", _ => Trash());
        Add(commands, "as_types", "Event types with base grade, half-life and witness range.", _ => Types());
        Add(commands, "as_export", "Write a readable JSON dump of this save's memories.", _ => Export());
        Add(commands, "as_save", "Save memories to disk now.", _ => Save());
        Add(commands, "as_saturation", "Confrontation meters: how close each NPC is to walking up to you, and why. Usage: as_saturation [NPC]", args => _mod.Log.Info(_mod.Feelings.Describe(args.Length > 0 ? ResolveNpcName(args[0]) : null)));
        Add(commands, "as_confront", "Make an NPC confront you about what they witnessed (as soon as you're close). Usage: as_confront <NPC>", args =>
        {
            if (args.Length == 0) { _mod.Log.Info("Usage: as_confront <NPC>"); return; }
            _mod.Log.Info(_mod.Feelings.Force(ResolveNpcName(args[0]), args.Length > 1 ? args[1] : null));
        });
        Add(commands, "as_forget", "DELETES memories. Usage: as_forget <eventId> | as_forget npc <NPC> | as_forget all confirm", Forget);
    }

    private void Add(ICommandHelper commands, string name, string doc, Action<string[]> run)
        => commands.Add(name, $"AliveSensor: {doc}", (_, args) =>
        {
            try
            {
                run(args);
            }
            catch (Exception ex)
            {
                _mod.Log.Error($"{name} failed", ex);
            }
        });

    private void Help()
    {
        _mod.Log.Info(string.Join("\n", new[]
        {
            "AliveSensor commands:",
            "  as_status                     hooks, sensors, save data",
            "  as_debug [on|off] [verbose]   detailed logs",
            "  as_day [daysAgo]              events + witnesses",
            "  as_list <NPC>                 memories as the AI reads them",
            "  as_score <NPC>                score breakdown",
            "  as_prompt <NPC>               conversation block (channel A)",
            "  as_cycle [daysAgo]            gossip block (channel B)",
            "  as_where                      position, landmark, room, nearby NPCs",
            "  as_witness <type>             dry-run witnesses here",
            "  as_simulate <type> [NPC] [item]  record a test event",
            "  as_rooms [location]           discovered bedrooms",
            "  as_trash                      trash cans on this map",
            "  as_types                      event types and defaults",
            "  as_export / as_save           dump / save now",
            "  as_forget <id> | npc <NPC> | all confirm",
            $"Types: {string.Join(", ", _mod.Catalog.All.Select(t => t.Id))}",
        }));
    }

    private void Status()
    {
        var c = _mod.Config;
        var b = _mod.AliveNpcs;
        var s = _mod.SpaceCore;
        var text = new StringBuilder();
        text.AppendLine($"AliveSensor {_mod.ModManifest.Version} — {(c.Enabled ? "ENABLED" : "DISABLED")} · debug {(c.Debug.Enabled ? "on" : "off")}{(c.Debug.Verbose ? " + verbose" : "")} · overlay {(c.Debug.Overlay ? "on" : "off")} ({c.Debug.OverlayKey})");
        text.AppendLine($"AliveNpcs {b.Version?.ToString() ?? "(missing)"}");
        text.AppendLine($"  Public API ............ {Ok(b.Api is not null)}");
        text.AppendLine($"  Experimental API ...... {Ok(b.Experimental is not null)}");
        text.AppendLine($"  Prompt NPC scope ...... {Ok(b.HasPromptScope)}");
        text.AppendLine($"  Current NPC hook ...... {Ok(b.HasCurrentNpc)}");
        text.AppendLine($"  Relationship table .... {Ok(b.HasRelationshipPairs)}");
        text.AppendLine($"  ApplyReaction (talk) .. {Ok(b.HasApplyReaction)}");
        text.AppendLine($"  Prompt blocks ......... {(b.RegisteredBlocks.Count == 0 ? "none" : string.Join("; ", b.RegisteredBlocks))}");
        text.AppendLine($"SpaceCore {s.Version?.ToString() ?? "(missing)"} · events {Ok(s.HasSpaceEvents)} · subscribed {Ok(s.Subscribed)}");
        text.AppendLine($"GMCM page ............... {(_mod.Gmcm?.IsRegistered == true ? "registered" : "not registered")}");
        text.AppendLine("Sensors (config · wiring):");
        foreach (var (name, enabled) in SensorToggles())
            text.AppendLine($"  {name,-12} {(enabled ? "on " : "off")} · {_mod.Sensors.Wiring.GetValueOrDefault(name, "not wired")}");
        var store = _mod.Store;
        text.AppendLine(store.IsLoaded
            ? $"Save '{store.SaveFolderName}': {store.Count} event(s){(store.IsDirty ? " (unsaved changes)" : "")} · {store.DataDirectory}"
            : "No save loaded.");
        text.AppendLine($"Sensors allowed: {_mod.SensorsAllowed} · cutscene/festival now: {(Context.IsWorldReady && Game1.eventUp)}");
        text.AppendLine($"  Dialogue start (confront) {Ok(b.HasDialogueStart)} · confrontations {(c.Confront.Enabled ? "on" : "off")} · pending: {_mod.Feelings.PendingNpc ?? "none"}");
        text.Append($"Channel A {(c.Delivery.NpcBlockEnabled ? "on" : "off")} (max {c.Delivery.MaxPerNpc} / {c.Delivery.MaxChars} chars) · B {(c.Cycle.CycleBlockEnabled ? "on" : "off")} (max {c.Cycle.MaxEvents}) · C {(c.Cycle.InjectGossip ? "on" : "off")} (top {c.Cycle.InjectGossipCount})");
        _mod.Log.Info(text.ToString());
    }

    private IEnumerable<(string Name, bool Enabled)> SensorToggles()
    {
        var s = _mod.Config.Sensors;
        yield return ("bomb", s.Bomb);
        yield return ("passOut", s.SleepOutside);
        yield return ("gift", s.Gift);
        yield return ("trash", s.Trash);
        yield return ("warp", s.MapAndPlace);
        yield return ("room", s.Room);
        yield return ("lockedDoor", s.LockedDoor);
        yield return ("talk", s.Talk);
        yield return ("purchase", s.Purchase);
        yield return ("consume", s.Consume);
        yield return ("npcMove", s.NpcMove);
    }

    private void Debug(string[] args)
    {
        var c = _mod.Config;
        bool enable = args.Length == 0 ? !c.Debug.Enabled : args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
        c.Debug.Enabled = enable;
        c.Debug.Verbose = enable && args.Any(a => a.Equals("verbose", StringComparison.OrdinalIgnoreCase));
        _mod.Helper.WriteConfig(c);
        _mod.Log.Info($"AliveSensor debug logs {(enable ? "ON" : "OFF")}{(c.Debug.Verbose ? " + verbose (sampling goes to the log file)" : "")}. Saved to config.json.");
    }

    private void Day(string[] args)
    {
        if (!RequireSave())
            return;
        int daysAgo = args.Length > 0 && int.TryParse(args[0], out int d) ? Math.Max(0, d) : 0;
        int day = SDate.Now().DaysSinceStart - daysAgo;
        var events = _mod.Store.Snapshot.Where(e => e.TotalDay == day).OrderBy(e => e.Time).ToList();
        var text = new StringBuilder($"Day {day} ({(daysAgo == 0 ? "today" : $"{daysAgo} day(s) ago")}): {events.Count} event(s)");
        foreach (MemoryEvent e in events)
        {
            text.Append($"\n  {GameClock.Clock12(e.Time),-8} {EventRecorder.Summary(e)}");
            text.Append($"\n           grade {e.Grade:0.00}{(e.Aggravators.Count > 0 ? $" ({string.Join(", ", e.Aggravators)})" : "")}");
            foreach (var w in e.Witnesses.OrderByDescending(w => w.Value.Level).ThenBy(w => w.Value.Distance))
                text.Append($"\n           └ {w.Key}: L{w.Value.Level} vis {w.Value.Visibility:0.00} dist {w.Value.Distance}{(w.Value.Direct ? " direct" : "")}{(w.Value.Bonus != 1f ? $" bonus×{w.Value.Bonus:0.##} [{string.Join(", ", w.Value.BonusReasons)}]" : "")}{(w.Value.Count > 0 ? $" count {w.Value.Count}" : "")}");
        }
        _mod.Log.Info(text.ToString());
    }

    /// <summary>"abigail" → "Abigail" (internal NPC name), when the world is loaded.</summary>
    private static string ResolveNpcName(string input)
    {
        if (!Context.IsWorldReady)
            return input;
        NPC? match = Utility.getAllVillagers().FirstOrDefault(n => n.Name.Equals(input, StringComparison.OrdinalIgnoreCase) || n.displayName.Equals(input, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? input;
    }

    private void List(string[] args)
    {
        if (!RequireSave() || !RequireNpc(args, "as_list"))
            return;
        string npc = args[0];
        SDate now = SDate.Now();
        var scored = _mod.Selector.ScoreAll(_mod.Store.Snapshot, npc, now.DaysSinceStart, Game1.timeOfDay).OrderByDescending(m => m.Score).ToList();
        var text = new StringBuilder($"{npc} remembers {scored.Count} event(s) (min score for prompts: {_mod.Config.Scoring.MinScore:0.00})");
        foreach (ScoredMemory m in scored)
            text.Append($"\n  [{m.Score:0.00}] {_mod.Renderer.NpcLine(m, npc, now.DaysSinceStart, Game1.timeOfDay, now.Year, _mod.Config).TrimStart('-', ' ')}");
        _mod.Log.Info(text.ToString());
    }

    private void Score(string[] args)
    {
        if (!RequireSave() || !RequireNpc(args, "as_score"))
            return;
        string npc = args[0];
        SDate now = SDate.Now();
        var scored = _mod.Selector.ScoreAll(_mod.Store.Snapshot, npc, now.DaysSinceStart, Game1.timeOfDay).OrderByDescending(m => m.Score).ToList();
        var text = new StringBuilder($"{npc}: {scored.Count} memory(ies) · score = grade × recency × involvement × visibility × bonus");
        foreach (ScoredMemory m in scored)
        {
            MemoryEvent e = m.Event;
            double age = GameClock.AgeInDays(e.TotalDay, e.Time, now.DaysSinceStart, Game1.timeOfDay);
            text.Append($"\n  {m.Score,6:0.00} = {e.Grade:0.00} × {m.Recency:0.00} (age {age:0.00}d) × {m.Involvement:0.0} ({m.InvolvementReason}) × {m.Witness.Visibility:0.00} × {m.Witness.Bonus:0.00} × {m.Repeat:0.00} (seen {m.Seen}×){(m.StandsOut(_mod.Config) ? " ★ stands out" : "")}"
                + $"  {(m.Score >= _mod.Config.Scoring.MinScore ? "✔" : "✘ below min")}  {e.Id} L{m.Witness.Level}");
        }
        _mod.Log.Info(text.ToString());
    }

    private void Prompt(string[] args)
    {
        if (!RequireSave() || !RequireNpc(args, "as_prompt"))
            return;
        string npc = args[0];
        string? block = _mod.NpcBlock.Build(npc, includeWhenEmpty: true, out var selected, out int candidates);
        _mod.Log.Info($"Channel A block for {npc} ({selected.Count} of {candidates} memories, {block?.Length ?? 0} chars):\n{block}");
    }

    private void Cycle(string[] args)
    {
        if (!RequireSave())
            return;
        int daysAgo = args.Length > 0 && int.TryParse(args[0], out int d) ? Math.Max(0, d) : 0;
        int day = SDate.Now().DaysSinceStart - daysAgo;
        string? block = _mod.CycleBlock.Build(day, out int count);
        _mod.Log.Info(block is null ? $"Channel B: nothing witnessed on day {day}." : $"Channel B block for day {day} ({count} event(s), {block.Length} chars):\n{block}");
    }

    private void Where()
    {
        if (!RequireWorld())
            return;
        Farmer player = Game1.player;
        GameLocation location = player.currentLocation;
        Point tile = player.TilePoint;
        var c = _mod.Config;
        var near = _mod.Landmarks.Nearest(location, tile);
        Room? room = _mod.Rooms.Find(location, tile);
        var text = new StringBuilder($"{location.NameOrUniqueName} (\"{location.DisplayName}\") tile ({tile.X},{tile.Y}) · {(location.IsOutdoors ? "outdoors" : "indoors")} · weather {location.GetWeather()?.Weather} · {GameClock.Clock12(Game1.timeOfDay)}");
        text.Append($"\n  Visibility factors here: weather {WitnessResolver.WeatherFactor(c, location):0.00} · light {WitnessResolver.LightFactor(c, location, Game1.timeOfDay):0.00}");
        text.Append($"\n  Landmark: {(near is null ? "none within range" : $"{near.Value.Phrase} ({near.Value.Tiles} tiles)")}");
        text.Append($"\n  Bedroom: {(room is null ? "no" : $"{string.Join("+", room.Owners)}'s (door {room.Door.X},{room.Door.Y})")}");
        text.Append($"\n  Player-owned place: {Homes.IsPlayerOwned(location)} · residents: {string.Join(", ", _mod.Homes.OwnersOf(location.Name))}");
        foreach (NPC npc in location.characters.Where(n => n.IsVillager).OrderBy(n => Vector2.Distance(n.Tile, player.Tile)))
        {
            int dist = (int)Math.Round(Vector2.Distance(npc.Tile, player.Tile));
            float vis = WitnessResolver.DistanceFactor(c, dist) * WitnessResolver.WeatherFactor(c, location) * WitnessResolver.LightFactor(c, location, Game1.timeOfDay) * WitnessResolver.AttentionFactor(c, npc);
            text.Append($"\n  {npc.Name,-12} dist {dist,3} · vis {vis:0.00} · {(npc.isSleeping.Value ? "sleeping" : npc.isMoving() ? "walking" : "standing")}{(npc.IsInvisible ? " · invisible" : "")}");
        }
        _mod.Log.Info(text.ToString());
    }

    private void Witness(string[] args)
    {
        if (!RequireWorld())
            return;
        string type = args.Length > 0 ? args[0] : EventTypes.Consume;
        if (!_mod.Catalog.TryGet(type, out _))
        {
            _mod.Log.Info($"Unknown type '{type}'. Types: {string.Join(", ", _mod.Catalog.All.Select(t => t.Id))}");
            return;
        }
        Farmer player = Game1.player;
        var draft = new EventDraft(type, "dry-run", player.currentLocation, player.TilePoint);
        _mod.Recorder.ResolveOnly(draft, $"dryrun-{Game1.timeOfDay}", out var checks);
        _mod.Log.Info(WitnessResolver.Describe("dry run", type, checks));
    }

    private void Simulate(string[] args)
    {
        if (!RequireSave() || !RequireWorld())
            return;
        if (args.Length == 0 || !_mod.Catalog.TryGet(args[0], out _))
        {
            _mod.Log.Info($"Usage: as_simulate <type> [target NPC] [item]. Types: {string.Join(", ", _mod.Catalog.All.Select(t => t.Id))}");
            return;
        }
        string type = args[0];
        string? target = args.Length > 1 ? args[1] : null;
        string item = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "Beer";
        Farmer player = Game1.player;
        GameLocation location = player.currentLocation;

        var draft = new EventDraft(type, "Simulate", location, player.TilePoint) { Target = target };
        draft.Payload["simulated"] = "true";
        if (target is not null)
        {
            draft.MentionedNpcs.Add(target);
            draft.Direct.Add(target);
        }
        switch (type)
        {
            case EventTypes.Gift:
                draft.Payload["item"] = item;
                draft.Payload["taste"] = "liked";
                break;
            case EventTypes.Consume:
                draft.Payload["item"] = item;
                draft.Payload["drink"] = "true";
                draft.Payload["alcohol"] = "true";
                if (location.Name != "Saloon")
                    draft.Aggravators.Add("drinkOutsideSaloon");
                break;
            case EventTypes.Purchase:
                draft.Payload["items"] = item;
                draft.Payload["shopDisplay"] = location.DisplayName;
                if (target is not null) draft.Payload["vendor"] = target;
                break;
            case EventTypes.Talk:
                draft.Payload["said"] = "This is a simulated conversation line.";
                draft.Payload["replied"] = "And this is the simulated reply.";
                draft.Payload["topic"] = "a simulated topic";
                if (target is not null) { draft.Exclude.Add(target); draft.Direct.Remove(target); }
                break;
            case EventTypes.Trash:
            case EventTypes.Room:
            case EventTypes.RoomLocked:
            case EventTypes.RoomUnlocked:
                if (target is not null) draft.Payload["owners"] = target;
                break;
            case EventTypes.Place:
                draft.Payload["action"] = "enter";
                draft.Payload["placeDisplay"] = location.DisplayName;
                break;
            case EventTypes.Map:
                draft.Payload["fromDisplay"] = "somewhere";
                draft.Payload["toDisplay"] = location.DisplayName;
                break;
            case EventTypes.SleepOutside:
                draft.Payload["reason"] = "2am";
                break;
        }
        MemoryEvent? recorded = _mod.Recorder.Record(draft);
        _mod.Log.Info(recorded is null ? "Simulated event not stored (nobody saw it — see the [Witness] debug lines)." : $"Recorded {recorded.Id} with {recorded.Witnesses.Count} witness(es). Try: as_day, as_list <NPC>, as_prompt <NPC>.");
    }

    private void Rooms(string[] args)
    {
        if (!RequireWorld())
            return;
        GameLocation? location = args.Length > 0 ? Game1.getLocationFromName(args[0]) : Game1.player.currentLocation;
        if (location is null)
        {
            _mod.Log.Info($"Location '{args[0]}' not found.");
            return;
        }
        var rooms = _mod.Rooms.For(location);
        var text = new StringBuilder($"{location.NameOrUniqueName}: {rooms.Count} bedroom(s)");
        foreach (Room room in rooms)
            text.Append($"\n  {string.Join("+", room.Owners),-16} door ({room.Door.X},{room.Door.Y}) · {room.Tiles.Count} tiles · x {room.Tiles.Min(t => t.X)}–{room.Tiles.Max(t => t.X)}, y {room.Tiles.Min(t => t.Y)}–{room.Tiles.Max(t => t.Y)}");
        if (rooms.Count == 0)
            text.Append("\n  (none — only interiors with 'Door <NPC>' tile actions have bedrooms; try SeedShop, SamHouse, HaleyHouse, JoshHouse, ScienceHouse, Trailer, ManorHouse)");
        _mod.Log.Info(text.ToString());
    }

    private void Trash()
    {
        if (!RequireWorld())
            return;
        GameLocation location = Game1.player.currentLocation;
        var cans = _mod.Landmarks.GarbageCans(location);
        ISet<string> checkedToday = Game1.netWorldState.Value.CheckedGarbage;
        var text = new StringBuilder($"{location.NameOrUniqueName}: {cans.Count} trash can(s)");
        foreach (var can in cans)
        {
            string owners = _mod.Config.World.TrashOwners.TryGetValue(can.Id, out var list) ? string.Join("+", list) : "(no owners configured)";
            text.Append($"\n  {can.Id,-14} ({can.Tile.X},{can.Tile.Y}) · owners {owners} · {(checkedToday.Contains(can.Id) ? "checked today" : "not checked")}");
        }
        _mod.Log.Info(text.ToString());
    }

    private void Types()
    {
        var c = _mod.Config;
        var text = new StringBuilder($"{_mod.Catalog.Count} event type(s) from the catalog ({EventCatalog.MainFile} plus any {EventCatalog.ExtraFolder}/*.json):");
        text.Append("\n  type          grade  half-life  range      feelings                      notes");
        foreach (EventDefinition def in _mod.Catalog.All)
        {
            float range = c.Visibility.RangeTiles.GetValueOrDefault(def.Id, def.RangeTiles);
            string feelings = def.Meters.Count == 0
                ? "-"
                : string.Join(" ", def.Meters.OrderByDescending(m => m.Value.Weight).Select(m => $"{m.Key}:{m.Value.Weight:0.##}{(m.Value.Roles.Count > 0 ? $"({string.Join("/", m.Value.Roles)})" : "")}"));
            var notes = new List<string>();
            if (c.Certainty.MinLevelByType.TryGetValue(def.Id, out int floor))
                notes.Add($"min level {floor}");
            if (def.Perception.HeardFloor > 0)
                notes.Add($"heard by all ≥{c.Visibility.HeardFloorByType.GetValueOrDefault(def.Id, def.Perception.HeardFloor):0.00}");
            if (def.Perception.ThroughWalls)
                notes.Add("through walls");
            if (def.Perception.WakesSleepers)
                notes.Add("wakes sleepers");
            foreach (var (meterId, contribution) in def.Meters)
            {
                if (contribution.RequiresPayload is { Length: > 0 } key)
                    notes.Add($"{meterId} only with '{key}'");
                if (contribution.OwnerTrustExempt)
                    notes.Add($"{meterId} exempt once trusted");
            }
            if (def.Rules.Count > 0)
                notes.Add($"{def.Rules.Count} rule(s): {string.Join("; ", def.Rules.Select(rule => rule.Name))}");
            if (def.VariantBy is { Length: > 0 } variantBy)
                notes.Add($"variants by {variantBy}: {string.Join("/", def.Variants.Keys)}");
            if (def.Phrases.IsEmpty && def.Variants.Count == 0)
                notes.Add("NO PHRASES");

            text.Append($"\n  {def.Id,-13} {c.Grade.BaseByType.GetValueOrDefault(def.Id, def.Grade),5:0.0}  {c.Scoring.HalfLifeDaysByType.GetValueOrDefault(def.Id, def.HalfLifeDays),6:0.0}d  {(range < 0 ? "whole map" : $"{range:0} tiles"),-9}  {feelings,-28}  {string.Join(", ", notes)}");
        }
        _mod.Log.Info(text.ToString());
    }

    private void Export()
    {
        if (!RequireSave())
            return;
        string? path = _mod.Store.Export();
        _mod.Log.Info(path is null ? "Nothing to export." : $"Exported to {path}");
    }

    private void Save()
    {
        if (!RequireSave())
            return;
        _mod.Store.Save("as_save command", force: true);
        _mod.Log.Info($"Saved {_mod.Store.Count} event(s) to {_mod.Store.DataDirectory}.");
    }

    private void Forget(string[] args)
    {
        if (!RequireSave())
            return;
        if (args.Length == 2 && args[0] == "npc")
        {
            int affected = _mod.Store.RemoveWitness(args[1]);
            _mod.Log.Info($"{args[1]} forgot {affected} event(s).");
        }
        else if (args.Length == 2 && args[0] == "all" && args[1] == "confirm")
        {
            int removed = _mod.Store.RemoveWhere(_ => true);
            _mod.Log.Info($"Deleted all {removed} event(s) for this save.");
        }
        else if (args.Length == 1 && args[0] != "all" && args[0] != "npc")
        {
            int removed = _mod.Store.RemoveWhere(e => e.Id == args[0]);
            _mod.Log.Info(removed == 0 ? $"No event with id {args[0]}." : $"Deleted {args[0]}.");
        }
        else
        {
            _mod.Log.Info("Usage: as_forget <eventId> | as_forget npc <NPC> | as_forget all confirm");
        }
    }

    private bool RequireSave()
    {
        if (_mod.Store.IsLoaded)
            return true;
        _mod.Log.Info("Load a save first.");
        return false;
    }

    private bool RequireWorld()
    {
        if (Context.IsWorldReady)
            return true;
        _mod.Log.Info("Load a save first.");
        return false;
    }

    private bool RequireNpc(string[] args, string command)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            return true;
        _mod.Log.Info($"Usage: {command} <NPC internal name>, e.g. {command} Abigail");
        return false;
    }

    private static string Ok(bool value) => value ? "ok" : "MISSING";
}
