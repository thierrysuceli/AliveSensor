using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI;

namespace AliveSensor.Core;

/// <summary>
/// Every event type the mod knows, loaded from JSON instead of code. <c>assets/event-types.json</c> ships with
/// the mod; any <c>event-types/*.json</c> file in the mod folder is loaded afterwards and can add new types or
/// replace shipped ones, so a sensor author never has to touch the pipeline.
///
/// Loading never throws: a missing or broken file is reported and the affected types simply fall back to neutral
/// defaults, which keeps a bad edit from taking the save's memories down with it.
/// </summary>
internal sealed class EventCatalog
{
    /// <summary>The catalog that ships with the mod.</summary>
    public const string MainFile = "assets/event-types.json";

    /// <summary>The feelings NPCs can build up, which event types give weight to.</summary>
    public const string MetersFile = "assets/meters.json";

    /// <summary>Folder scanned for extra or overriding type files.</summary>
    public const string ExtraFolder = "event-types";

    private readonly Dictionary<string, EventDefinition> _byId;
    private readonly Dictionary<string, MeterDefinition> _meters;

    private EventCatalog(Dictionary<string, EventDefinition> byId, Dictionary<string, MeterDefinition> meters, List<EventRule> globalRules)
    {
        _byId = byId;
        _meters = meters;
        All = byId.Values.OrderByDescending(def => def.Grade).ThenBy(def => def.Id, StringComparer.Ordinal).ToArray();
        Meters = meters.Values.OrderByDescending(meter => meter.Priority).ThenBy(meter => meter.Id, StringComparer.Ordinal).ToArray();
        GlobalRules = globalRules;
    }

    /// <summary>The feelings NPCs can build up, strongest priority first.</summary>
    public IReadOnlyList<MeterDefinition> Meters { get; }

    /// <summary>Rules that apply to every event type, checked after that type's own rules — e.g. "the village is quieter on weekends".</summary>
    public IReadOnlyList<EventRule> GlobalRules { get; }

    public bool TryGetMeter(string id, out MeterDefinition meter) => _meters.TryGetValue(id, out meter!);

    /// <summary>All types, strongest first — the order the config pages and <c>as_types</c> list them in.</summary>
    public IReadOnlyList<EventDefinition> All { get; }

    public int Count => All.Count;

    public bool TryGet(string id, out EventDefinition definition) => _byId.TryGetValue(id, out definition!);

    /// <summary>The definition for a type, or a neutral one so an unknown type still records and renders.</summary>
    public EventDefinition GetOrDefault(string id)
        => _byId.TryGetValue(id, out EventDefinition? definition) ? definition : new EventDefinition { Id = id };

    /// <summary>Read the shipped catalog plus any user or community files, reporting anything wrong.</summary>
    public static EventCatalog Load(IModHelper helper, Log log)
    {
        var byId = new Dictionary<string, EventDefinition>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        var meters = new Dictionary<string, MeterDefinition>(StringComparer.OrdinalIgnoreCase);
        ReadMeters(helper, MetersFile, meters, problems, log);

        var globalRules = new List<EventRule>();
        int shipped = ReadInto(helper, MainFile, byId, globalRules, problems, log);
        if (shipped == 0)
            log.Error($"No event types loaded from '{MainFile}'. Memories will still be recorded, but they will read as \"did something\". Is the mod folder complete?");

        string extraPath = Path.Combine(helper.DirectoryPath, ExtraFolder);
        int extra = 0;
        if (Directory.Exists(extraPath))
        {
            foreach (string file in Directory.EnumerateFiles(extraPath, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.Combine(ExtraFolder, Path.GetFileName(file));
                extra += ReadInto(helper, relative, byId, globalRules, problems, log);
                ReadMeters(helper, relative, meters, problems, log);
            }
        }

        // A type written before meters existed still works: its old Confront block becomes a "confront" weight.
        foreach (EventDefinition definition in byId.Values)
            AdoptLegacyConfront(definition);

        foreach (EventDefinition definition in byId.Values)
        {
            foreach (string meterId in definition.Meters.Keys)
            {
                if (!meters.ContainsKey(meterId))
                    problems.Add($"'{definition.Id}' gives weight to meter '{meterId}', which no meters file declares.");
            }
        }

        var catalog = new EventCatalog(byId, meters, globalRules);
        foreach (string problem in problems)
            log.Warn($"Event catalog: {problem}");

        log.Debug("Catalog", $"{catalog.Count} event type(s) loaded ({shipped} shipped{(extra > 0 ? $", {extra} from {ExtraFolder}/" : "")}): {string.Join(", ", catalog.All.Select(def => def.Id))}.");
        log.Debug("Catalog", $"{catalog.Meters.Count} meter(s): {string.Join(", ", catalog.Meters.Select(meter => $"{meter.Id} [{string.Join("/", meter.Thresholds)}]"))}.");
        if (catalog.GlobalRules.Count > 0)
            log.Debug("Catalog", $"{catalog.GlobalRules.Count} global rule(s), applied to every event type: {string.Join(", ", catalog.GlobalRules.Select(rule => rule.Name))}.");
        return catalog;
    }

    /// <summary>Read a meters file, if the given file has a "Meters" section.</summary>
    private static void ReadMeters(IModHelper helper, string relativePath, Dictionary<string, MeterDefinition> meters, List<string> problems, Log log)
    {
        MetersFileModel? file;
        try
        {
            file = helper.Data.ReadJsonFile<MetersFileModel>(relativePath);
        }
        catch (Exception ex)
        {
            log.Error($"Meters file '{relativePath}' could not be read: {ex.Message}");
            return;
        }
        if (file?.Meters is null)
            return;

        foreach (var (id, meter) in file.Meters)
        {
            if (meter is null || string.IsNullOrWhiteSpace(id))
                continue;
            meter.Id = id.Trim();
            foreach (string problem in meter.Compile())
                problems.Add($"{problem} ({relativePath})");
            meters[meter.Id] = meter;
        }
    }

    /// <summary>Turn a pre-meters "Confront" block into a normal "confront" weight, so old files keep working.</summary>
    private static void AdoptLegacyConfront(EventDefinition definition)
    {
        if (definition.Meters.Count > 0 || definition.Confront.Weight <= 0)
            return;
        definition.Meters["confront"] = new MeterContribution
        {
            Weight = definition.Confront.Weight,
            RequiresPayload = definition.Confront.RequiresPayload,
            OwnerTrustExempt = definition.Confront.OwnerTrustExempt,
        };
    }

    private static int ReadInto(IModHelper helper, string relativePath, Dictionary<string, EventDefinition> byId, List<EventRule> globalRules, List<string> problems, Log log)
    {
        CatalogFile? file;
        try
        {
            file = helper.Data.ReadJsonFile<CatalogFile>(relativePath);
        }
        catch (Exception ex)
        {
            log.Error($"Event catalog '{relativePath}' could not be read: {ex.Message}");
            return 0;
        }
        if (file is null)
            return 0;

        if (file.GlobalRules is not null)
        {
            for (int i = 0; i < file.GlobalRules.Count; i++)
            {
                EventRule rule = file.GlobalRules[i];
                foreach (string problem in rule.Compile("global", globalRules.Count + i))
                    problems.Add($"{problem} ({relativePath})");
                globalRules.Add(rule);
            }
        }

        if (file.Types is null)
            return 0;

        int loaded = 0;
        foreach (var (id, definition) in file.Types)
        {
            if (definition is null || string.IsNullOrWhiteSpace(id))
            {
                problems.Add($"'{relativePath}' has an entry with no id; skipped.");
                continue;
            }
            definition.Id = id.Trim();
            foreach (string problem in definition.Compile())
                problems.Add($"{problem} ({relativePath})");
            if (byId.ContainsKey(definition.Id))
                log.Debug("Catalog", $"'{definition.Id}' redefined by {relativePath}.");
            byId[definition.Id] = definition;
            loaded++;
        }
        return loaded;
    }

    /// <summary>The meters file's shape.</summary>
    private sealed class MetersFileModel
    {
        public int Format { get; set; } = 1;

        public Dictionary<string, MeterDefinition>? Meters { get; set; }
    }

    /// <summary>The JSON file's shape: a format stamp, the types by id, and rules that apply to every type.</summary>
    private sealed class CatalogFile
    {
        /// <summary>Bumped only when the shape changes in a way old files can't survive.</summary>
        public int Format { get; set; } = 1;

        public Dictionary<string, EventDefinition>? Types { get; set; }

        /// <summary>Checked for every witness of every event type, after that type's own rules — e.g. day-of-week or weather-wide adjustments.</summary>
        public List<EventRule>? GlobalRules { get; set; }
    }
}
