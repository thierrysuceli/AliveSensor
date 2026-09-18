using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Delivery;
using AliveSensor.Memory;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace AliveSensor.World;

/// <summary>What a sensor saw, before witnesses and grade are resolved.</summary>
internal sealed class EventDraft
{
    public EventDraft(string type, string source, GameLocation location, Point tile)
    {
        Type = type;
        Source = source;
        Location = location;
        Tile = tile;
    }

    public string Type { get; }
    public string Source { get; }
    public GameLocation Location { get; }
    public Point Tile { get; }

    public string Actor { get; set; } = Game1.player?.Name ?? "the farmer";
    public string? Target { get; set; }
    public List<string> Involved { get; } = new();
    public Dictionary<string, string> Payload { get; } = new();
    public List<string> Aggravators { get; } = new();

    /// <summary>NPCs directly involved: full visibility if present on a scope map.</summary>
    public HashSet<string> Direct { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>NPCs who never become witnesses (the actor, the person being talked to).</summary>
    public HashSet<string> Exclude { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Other maps to look for witnesses on (e.g. the street outside a door).</summary>
    public List<WitnessScope> ExtraScopes { get; } = new();
    /// <summary>NPC names mentioned in the payload, so their display names get captured.</summary>
    public List<string> MentionedNpcs { get; } = new();

    /// <summary>Per-witness adjustments (bonus, counters) after witnesses are known.</summary>
    public Action<string, WitnessRecord>? PerWitness { get; set; }

    public IEnumerable<WitnessScope> Scopes()
    {
        yield return new WitnessScope(Location, new Vector2(Tile.X, Tile.Y));
        foreach (var scope in ExtraScopes)
            yield return scope;
    }
}

/// <summary>Turns sensor drafts into stored memories: witnesses, grade, landmark, names. Main thread only.</summary>
internal sealed class EventRecorder
{
    private readonly ModEntry _mod;

    public EventRecorder(ModEntry mod) => _mod = mod;

    public MemoryEvent? LastRecorded { get; private set; }

    /// <summary>
    /// Whether sensors may record right now; logs the reason when not.
    /// <paramref name="ignoreEvent"/> lets a sensor decide about cutscenes itself (e.g. a cutscene that starts on arrival).
    /// </summary>
    public bool CanRecord(string sensor, out string reason, bool ignoreEvent = false)
    {
        reason = !_mod.Config.Enabled ? "AliveSensor disabled"
            : !Context.IsWorldReady ? "world not ready"
            : !_mod.Store.IsLoaded ? "no save loaded"
            : !_mod.SensorsAllowed ? "multiplayer farmhand"
            : !ignoreEvent && Game1.eventUp ? "cutscene or festival running"
            : "";
        if (reason.Length == 0)
            return true;
        _mod.Log.Debug($"Sensor:{sensor}", $"Ignored: {reason}.");
        return false;
    }

    public MemoryEvent? Record(EventDraft draft)
    {
        ModConfig c = _mod.Config;
        string category = $"Sensor:{draft.Source}";
        try
        {
            MemoryEvent e = CreateEvent(draft);
            e.Witnesses = _mod.Witnesses.Resolve(e.Id, draft.Type, draft.Scopes(), draft.Direct, draft.Exclude, out _);

            if (e.Witnesses.Count == 0)
            {
                _mod.Log.Debug(category, $"{Summary(e)} — not stored: nobody saw it.");
                return null;
            }

            foreach (var pair in e.Witnesses)
                draft.PerWitness?.Invoke(pair.Key, pair.Value);

            ApplyRules(e, e.Witnesses, draft);
            CaptureNames(e, draft);
            _mod.Store.Add(e);
            _mod.Overlay.Show(e);
            LastRecorded = e;

            _mod.Log.Debug(category, $"{Summary(e)} · grade {e.Grade:0.00}{(e.Aggravators.Count > 0 ? $" ({string.Join(", ", e.Aggravators)})" : "")}"
                + $" · witnesses: {string.Join(", ", e.Witnesses.OrderByDescending(w => w.Value.Level).Select(w => $"{w.Key} L{w.Value.Level}{(w.Value.Bonus != 1f ? $" bonus×{w.Value.Bonus:0.##}" : "")}{(w.Value.Count > 0 ? $" count {w.Value.Count}" : "")}{Feelings(w.Value)}"))}");
            return e;
        }
        catch (Exception ex)
        {
            _mod.Log.Error($"{category}: recording a '{draft.Type}' event failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Resolve witnesses for a draft without storing it (used when merging into an existing memory, and by
    /// as_witness). Pass the memory being merged into as <paramref name="context"/> so its rules run too.
    /// </summary>
    public Dictionary<string, WitnessRecord> ResolveOnly(EventDraft draft, string eventId, out List<WitnessCheck> checks, MemoryEvent? context = null)
    {
        var witnesses = _mod.Witnesses.Resolve(eventId, draft.Type, draft.Scopes(), draft.Direct, draft.Exclude, out checks);
        if (context is not null)
            ApplyRules(context, witnesses, draft);
        return witnesses;
    }

    /// <summary>
    /// Ask the event type's rules what each witness feels about this, and store the answer on them. Runs on the
    /// main thread, while the world is still there to be asked ("is a child nearby?", "how many hearts?").
    /// </summary>
    public void ApplyRules(MemoryEvent memory, IDictionary<string, WitnessRecord> witnesses, EventDraft draft)
    {
        var origin = new Vector2(draft.Tile.X, draft.Tile.Y);
        foreach (var (npc, witness) in witnesses)
        {
            string role = MemorySelector.Role(memory, npc, _mod.Relationships);
            RuleOutcome outcome = _mod.Rules.Evaluate(memory, npc, witness, role, origin, draft.Location);
            witness.Meters = new Dictionary<string, float>(outcome.Meters, StringComparer.OrdinalIgnoreCase);
            witness.Rules = outcome.MatchedRules.ToList();
            witness.Silent = outcome.Silent;
        }
    }

    public MemoryEvent CreateEvent(EventDraft draft)
    {
        ModConfig c = _mod.Config;
        SDate now = SDate.Now();
        GameLocation location = draft.Location;
        var e = new MemoryEvent
        {
            Type = draft.Type,
            Source = draft.Source,
            Day = now.Day,
            Season = now.SeasonKey,
            Year = now.Year,
            TotalDay = now.DaysSinceStart,
            Time = Game1.timeOfDay,
            Location = location.NameOrUniqueName,
            LocationDisplay = location.DisplayName,
            TileX = draft.Tile.X,
            TileY = draft.Tile.Y,
            Outdoors = location.IsOutdoors,
            Weather = location.GetWeather()?.Weather ?? "",
            Actor = draft.Actor,
            Target = draft.Target,
            Involved = draft.Involved.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Payload = new Dictionary<string, string>(draft.Payload),
        };
        e.Id = _mod.Store.NextId(e);
        e.Grade = Grading.Compute(c, draft.Type, e.Time, draft.Aggravators, out var applied);
        e.Aggravators = applied;

        var near = _mod.Landmarks.Nearest(location, draft.Tile);
        if (near is not null)
        {
            e.Near = near.Value.Phrase;
            e.NearTiles = near.Value.Tiles;
        }
        return e;
    }

    public void CaptureNames(MemoryEvent e, EventDraft? draft = null)
    {
        IEnumerable<string> names = e.Witnesses.Keys
            .Concat(e.Involved)
            .Concat(draft?.MentionedNpcs ?? Enumerable.Empty<string>())
            .Append(e.Actor);
        if (e.Target is not null)
            names = names.Append(e.Target);

        foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!e.Names.ContainsKey(name))
                e.Names[name] = Landmarks.DisplayName(name);
        }
    }

    /// <summary>"[humor 3.0, confront 1.0 · rule: Shane thinks it's a laugh]" for the sensor log.</summary>
    private static string Feelings(WitnessRecord witness)
    {
        if (witness.Meters.Count == 0)
            return witness.Rules.Count > 0 ? $" [no meter · {string.Join(", ", witness.Rules)}]" : "";
        string meters = string.Join(", ", witness.Meters.OrderByDescending(m => m.Value).Select(m => $"{m.Key} {m.Value:0.##}"));
        string rules = witness.Rules.Count > 0 ? $" · {string.Join(", ", witness.Rules)}" : "";
        return $" [{meters}{rules}{(witness.Silent ? " · silent" : "")}]";
    }

    public static string Summary(MemoryEvent e)
        => $"{e.Id} {e.Type} by {e.Actor}{(e.Target is null ? "" : $" → {e.Target}")} @ {e.Location} ({e.TileX},{e.TileY}){(e.Near is null ? "" : $" {e.Near} [{e.NearTiles}t]")}"
           + (e.Payload.Count == 0 ? "" : $" {{{string.Join(", ", e.Payload.Select(p => $"{p.Key}={p.Value}"))}}}");
}
