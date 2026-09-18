using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AliveSensor.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AliveSensor.Memory;

/// <summary>
/// Witnessed events for the loaded save, stored like AliveNpcs does:
/// Mods\AliveSensor\Data\&lt;SaveFolderName&gt;\*.json, envelope {schema_version, data}, atomic writes.
/// Reads from background threads (prompt providers) use an immutable snapshot, never the live list.
/// </summary>
internal sealed class MemoryStore
{
    public const int SchemaVersion = 1;
    private const string MemoriesFile = "memories.json";
    private const string CountersFile = "daily_counters.json";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        // camelCase properties, but keep dictionary keys (NPC names) exactly as they are.
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = false },
        },
    };

    private readonly string _modDirectory;
    private readonly Log _log;
    private readonly object _lock = new();

    private MemoryFile _memories = new();
    private DailyCountersFile _counters = new();
    private volatile MemoryEvent[] _snapshot = Array.Empty<MemoryEvent>();
    private bool _dirty;

    public MemoryStore(string modDirectory, Log log)
    {
        _modDirectory = modDirectory;
        _log = log;
    }

    public bool IsLoaded { get; private set; }

    public string? SaveFolderName { get; private set; }

    public string? DataDirectory => SaveFolderName is null ? null : Path.Combine(_modDirectory, "Data", SaveFolderName);

    public bool IsDirty
    {
        get { lock (_lock) return _dirty; }
    }

    /// <summary>Thread-safe, allocation-free view of all events.</summary>
    public IReadOnlyList<MemoryEvent> Snapshot => _snapshot;

    public int Count => _snapshot.Length;

    public void Load(string saveFolderName)
    {
        lock (_lock)
        {
            SaveFolderName = saveFolderName;
            string dir = DataDirectory!;
            _memories = ReadEnvelope<MemoryFile>(Path.Combine(dir, MemoriesFile)) ?? new MemoryFile();
            _counters = ReadEnvelope<DailyCountersFile>(Path.Combine(dir, CountersFile)) ?? new DailyCountersFile();
            _memories.Events ??= new List<MemoryEvent>();
            _counters.Days ??= new();
            _snapshot = _memories.Events.ToArray();
            _dirty = false;
            IsLoaded = true;
        }

        var byType = _snapshot.GroupBy(e => e.Type).Select(g => $"{g.Key}={g.Count()}");
        _log.Debug("Store", $"Loaded save '{saveFolderName}': {_snapshot.Length} event(s) [{string.Join(", ", byType)}], {_counters.Days.Count} counter day(s). Folder: {DataDirectory}");
    }

    public void Unload()
    {
        lock (_lock)
        {
            _memories = new MemoryFile();
            _counters = new DailyCountersFile();
            _snapshot = Array.Empty<MemoryEvent>();
            _dirty = false;
            IsLoaded = false;
            SaveFolderName = null;
        }
        _log.Debug("Store", "Unloaded (returned to title).");
    }

    /// <summary>Create a stable, human-readable id: y1-spring-03-1430-trash-0007.</summary>
    public string NextId(MemoryEvent e)
    {
        lock (_lock)
        {
            int sequence = _memories.NextSequence++;
            _dirty = true;
            return $"y{e.Year}-{e.Season}-{e.Day:00}-{e.Time:0000}-{e.Type}-{sequence:0000}";
        }
    }

    /// <summary>Raised on the calling thread after an event is added or updated (sensors run on the main thread).</summary>
    public event Action<MemoryEvent>? Changed;

    public void Add(MemoryEvent e)
    {
        if (!IsLoaded)
        {
            _log.Debug("Store", $"Ignored event '{e.Type}': no save loaded.");
            return;
        }

        lock (_lock)
        {
            _memories.Events.Add(e);
            _snapshot = _memories.Events.ToArray();
            _dirty = true;
        }
        _log.Debug("Store", $"Added {e.Id} ({e.Type}) at {e.Location} ({e.TileX},{e.TileY}); grade {e.Grade:0.00}; {e.Witnesses.Count} witness(es). Total: {_snapshot.Length}.");
        Changed?.Invoke(e);
    }

    /// <summary>
    /// Update an event copy-on-write: the callback receives a clone, and the clone replaces the original.
    /// Background readers keep seeing the old object until the swap. Returns the new event, or null if not found.
    /// </summary>
    public MemoryEvent? Update(string id, Action<MemoryEvent> change, string reason)
    {
        MemoryEvent? updated = null;
        lock (_lock)
        {
            int index = _memories.Events.FindIndex(e => e.Id == id);
            if (index >= 0)
            {
                updated = _memories.Events[index].Clone();
                change(updated);
                _memories.Events[index] = updated;
                _snapshot = _memories.Events.ToArray();
                _dirty = true;
            }
        }
        if (updated is null)
            _log.Debug("Store", $"Update skipped ({reason}): event {id} not found.");
        else
        {
            _log.Debug("Store", $"Updated {id} ({reason}); grade {updated.Grade:0.00}; {updated.Witnesses.Count} witness(es).");
            Changed?.Invoke(updated);
        }
        return updated;
    }

    /// <summary>Most recent event matching the predicate, from the snapshot.</summary>
    public MemoryEvent? FindLast(Func<MemoryEvent, bool> predicate)
    {
        MemoryEvent[] events = _snapshot;
        for (int i = events.Length - 1; i >= 0; i--)
        {
            if (predicate(events[i]))
                return events[i];
        }
        return null;
    }

    /// <summary>Forget one NPC's memories (the events stay for other witnesses). Returns affected events.</summary>
    public int RemoveWitness(string npc)
    {
        int affected = 0;
        lock (_lock)
        {
            for (int i = 0; i < _memories.Events.Count; i++)
            {
                if (!_memories.Events[i].Witnesses.ContainsKey(npc))
                    continue;
                MemoryEvent copy = _memories.Events[i].Clone();
                copy.Witnesses.Remove(npc);
                _memories.Events[i] = copy;
                affected++;
            }
            _memories.Events.RemoveAll(e => e.Witnesses.Count == 0);
            if (affected > 0)
            {
                _snapshot = _memories.Events.ToArray();
                _dirty = true;
            }
        }
        _log.Debug("Store", $"Removed {npc} from {affected} event(s). Total events: {_snapshot.Length}.");
        return affected;
    }

    /// <summary>Increment a per-witness daily counter and return the new value.</summary>
    public int IncrementCounter(int totalDay, string witness, string counter)
    {
        lock (_lock)
        {
            string dayKey = totalDay.ToString();
            if (!_counters.Days.TryGetValue(dayKey, out var witnesses))
                _counters.Days[dayKey] = witnesses = new();
            if (!witnesses.TryGetValue(witness, out var counters))
                witnesses[witness] = counters = new();
            counters.TryGetValue(counter, out int value);
            counters[counter] = ++value;
            _dirty = true;
            return value;
        }
    }

    public int GetCounter(int totalDay, string witness, string counter)
    {
        lock (_lock)
        {
            return _counters.Days.TryGetValue(totalDay.ToString(), out var witnesses)
                && witnesses.TryGetValue(witness, out var counters)
                && counters.TryGetValue(counter, out int value)
                ? value
                : 0;
        }
    }

    /// <summary>Remove events matching the predicate (used by retention). Returns how many were removed.</summary>
    public int RemoveWhere(Func<MemoryEvent, bool> predicate)
    {
        int removed;
        lock (_lock)
        {
            removed = _memories.Events.RemoveAll(e => predicate(e));
            if (removed > 0)
            {
                _snapshot = _memories.Events.ToArray();
                _dirty = true;
            }
        }
        if (removed > 0)
            _log.Debug("Store", $"Removed {removed} event(s). Total: {_snapshot.Length}.");
        return removed;
    }

    /// <summary>Drop counters older than the given number of days.</summary>
    public void PruneCounters(int currentTotalDay, int keepDays = 2)
    {
        lock (_lock)
        {
            foreach (string key in _counters.Days.Keys.ToList())
            {
                if (int.TryParse(key, out int day) && currentTotalDay - day > keepDays)
                {
                    _counters.Days.Remove(key);
                    _dirty = true;
                }
            }
        }
    }

    public void Save(string reason, bool force = false)
    {
        if (!IsLoaded)
            return;

        MemoryFile memories;
        DailyCountersFile counters;
        lock (_lock)
        {
            if (!_dirty && !force)
            {
                _log.Verbose("Store", $"Save skipped ({reason}): nothing changed.");
                return;
            }
            memories = _memories;
            counters = _counters;
            _dirty = false;
        }

        try
        {
            string dir = DataDirectory!;
            Directory.CreateDirectory(dir);
            WriteEnvelope(Path.Combine(dir, MemoriesFile), memories);
            WriteEnvelope(Path.Combine(dir, CountersFile), counters);
            _log.Debug("Store", $"Saved ({reason}): {memories.Events.Count} event(s) → {dir}");
        }
        catch (Exception ex)
        {
            lock (_lock) _dirty = true;
            _log.Error($"Could not save AliveSensor memories ({reason})", ex);
        }
    }

    /// <summary>Write a human-readable dump to Data\&lt;save&gt;\export\. Returns the file path.</summary>
    public string? Export()
    {
        if (!IsLoaded)
            return null;

        string dir = Path.Combine(DataDirectory!, "export");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"memories-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        MemoryEvent[] events = _snapshot;
        File.WriteAllText(path, JsonConvert.SerializeObject(events, JsonSettings));
        _log.Debug("Store", $"Exported {events.Length} event(s) to {path}");
        return path;
    }

    private T? ReadEnvelope<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            _log.Debug("Store", $"No file yet: {Path.GetFileName(path)} (new save or first run).");
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            var envelope = JsonConvert.DeserializeObject<DataEnvelope<T>>(json, JsonSettings);
            if (envelope?.Data is not null)
            {
                if (envelope.SchemaVersion != SchemaVersion)
                    _log.Warn($"{Path.GetFileName(path)} has schema version {envelope.SchemaVersion}; this build expects {SchemaVersion}.");
                return envelope.Data;
            }

            // Accept a raw object without the envelope (hand-edited files).
            return JsonConvert.DeserializeObject<T>(json, JsonSettings);
        }
        catch (Exception ex)
        {
            string corrupt = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try { File.Move(path, corrupt); } catch { /* keep going with defaults */ }
            _log.Error($"{Path.GetFileName(path)} is corrupt and was renamed to {Path.GetFileName(corrupt)}; starting empty", ex);
            return null;
        }
    }

    private static void WriteEnvelope<T>(string path, T data)
    {
        var envelope = new DataEnvelope<T> { SchemaVersion = SchemaVersion, Data = data };
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(envelope, JsonSettings));
        File.Move(temp, path, overwrite: true);
    }
}
