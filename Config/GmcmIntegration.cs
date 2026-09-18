using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Core;
using StardewModdingAPI;

namespace AliveSensor.Config;

/// <summary>Registers every setting in Generic Mod Config Menu (optional dependency).</summary>
internal sealed class GmcmIntegration
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";

    private readonly IModHelper _helper;
    private readonly IManifest _manifest;
    private readonly EventCatalog _catalog;
    private readonly Func<ModConfig> _get;
    private readonly Action<ModConfig> _set;
    private readonly Action _onSaved;
    private readonly Log _log;
    private IGenericModConfigMenuApi _api = null!;

    public GmcmIntegration(IModHelper helper, IManifest manifest, EventCatalog catalog, Func<ModConfig> get, Action<ModConfig> set, Action onSaved, Log log)
    {
        _helper = helper;
        _manifest = manifest;
        _catalog = catalog;
        _get = get;
        _set = set;
        _onSaved = onSaved;
        _log = log;
    }

    public bool IsRegistered { get; private set; }

    public void Register()
    {
        if (!_helper.ModRegistry.IsLoaded(GmcmId))
        {
            _log.Debug("GMCM", "Generic Mod Config Menu not installed; config UI skipped (config.json still works).");
            return;
        }

        try
        {
            var api = _helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
            if (api is null)
            {
                _log.Warn("Generic Mod Config Menu is installed but its API could not be loaded; use config.json.");
                return;
            }
            _api = api;
            Build();
            IsRegistered = true;
            _log.Debug("GMCM", "Config menu registered.");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to register the Generic Mod Config Menu page", ex);
        }
    }

    private void Build()
    {
        _api.Register(
            _manifest,
            reset: () =>
            {
                var fresh = new ModConfig();
                fresh.Normalize(_catalog);
                _set(fresh);
                _log.Debug("GMCM", "Config reset to defaults.");
            },
            save: () =>
            {
                int fixes = _get().Normalize(_catalog);
                _helper.WriteConfig(_get());
                _log.Debug("GMCM", $"Config saved ({fixes} value(s) normalized).");
                _onSaved();
            });

        // ── Main page ──
        Paragraph("config.intro");
        Bool("config.enabled", c => c.Enabled, (c, v) => c.Enabled = v);
        Section("config.section.pages");
        foreach (string page in new[] { "delivery", "sensors", "visibility", "certainty", "grade", "scoring", "talk", "cycle", "confront", "debug" })
            _api.AddPageLink(_manifest, page, () => T($"config.page.{page}"), Tip($"config.page.{page}"));

        // ── Delivery (channel A) ──
        Page("delivery");
        Bool("config.delivery.npcBlock", c => c.Delivery.NpcBlockEnabled, (c, v) => c.Delivery.NpcBlockEnabled = v);
        Int("config.delivery.maxPerNpc", c => c.Delivery.MaxPerNpc, (c, v) => c.Delivery.MaxPerNpc = v, 1, 15);
        Int("config.delivery.maxChars", c => c.Delivery.MaxChars, (c, v) => c.Delivery.MaxChars = v, 200, 1800, 50);
        _api.AddTextOption(_manifest, () => _get().Delivery.PromptLanguage, v => _get().Delivery.PromptLanguage = v,
            () => T("config.delivery.language.name"), Tip("config.delivery.language"), new[] { "en", "pt-BR" });

        // ── Sensors ──
        Page("sensors");
        Paragraph("config.sensors.intro");
        Bool("config.sensor.bomb", c => c.Sensors.Bomb, (c, v) => c.Sensors.Bomb = v);
        Bool("config.sensor.sleepOutside", c => c.Sensors.SleepOutside, (c, v) => c.Sensors.SleepOutside = v);
        Bool("config.sensor.gift", c => c.Sensors.Gift, (c, v) => c.Sensors.Gift = v);
        Bool("config.sensor.trash", c => c.Sensors.Trash, (c, v) => c.Sensors.Trash = v);
        Bool("config.sensor.mapAndPlace", c => c.Sensors.MapAndPlace, (c, v) => c.Sensors.MapAndPlace = v);
        Bool("config.sensor.room", c => c.Sensors.Room, (c, v) => c.Sensors.Room = v);
        Bool("config.sensor.lockedDoor", c => c.Sensors.LockedDoor, (c, v) => c.Sensors.LockedDoor = v);
        Bool("config.sensor.talk", c => c.Sensors.Talk, (c, v) => c.Sensors.Talk = v);
        Bool("config.sensor.purchase", c => c.Sensors.Purchase, (c, v) => c.Sensors.Purchase = v);
        Bool("config.sensor.consume", c => c.Sensors.Consume, (c, v) => c.Sensors.Consume = v);
        Bool("config.sensor.npcMove", c => c.Sensors.NpcMove, (c, v) => c.Sensors.NpcMove = v);

        // ── Visibility ──
        Page("visibility");
        Paragraph("config.visibility.intro");
        Section("config.visibility.section.distance");
        for (int i = 0; i < _get().Visibility.DistanceBands.Count; i++)
        {
            int index = i;
            Int("config.visibility.bandTiles", c => c.Visibility.DistanceBands[index].MaxTiles, (c, v) => c.Visibility.DistanceBands[index].MaxTiles = v, 0, 60, 1, new { band = index + 1 });
            Float("config.visibility.bandFactor", c => c.Visibility.DistanceBands[index].Factor, (c, v) => c.Visibility.DistanceBands[index].Factor = v, 0f, 1f, 0.05f, new { band = index + 1 });
        }
        Float("config.visibility.beyond", c => c.Visibility.BeyondBandsFactor, (c, v) => c.Visibility.BeyondBandsFactor = v, 0f, 1f, 0.05f);
        Section("config.visibility.section.range");
        foreach (EventDefinition def in _catalog.All)
        {
            string id = def.Id;
            Float("config.visibility.range", c => c.Visibility.RangeTiles[id], (c, v) => c.Visibility.RangeTiles[id] = v, -1f, 60f, 1f, new { type = TypeName(id) }, v => v < 0 ? T("config.value.wholeMap") : $"{v:0}");
        }
        Section("config.visibility.section.conditions");
        Float("config.visibility.rain", c => c.Visibility.RainFactor, (c, v) => c.Visibility.RainFactor = v, 0f, 1f, 0.05f);
        Float("config.visibility.storm", c => c.Visibility.StormFactor, (c, v) => c.Visibility.StormFactor = v, 0f, 1f, 0.05f);
        Float("config.visibility.snow", c => c.Visibility.SnowFactor, (c, v) => c.Visibility.SnowFactor = v, 0f, 1f, 0.05f);
        Int("config.visibility.nightFrom", c => c.Visibility.NightOutdoorFromTime, (c, v) => c.Visibility.NightOutdoorFromTime = v, 1800, 2400, 100);
        Float("config.visibility.night", c => c.Visibility.NightOutdoorFactor, (c, v) => c.Visibility.NightOutdoorFactor = v, 0f, 1f, 0.05f);
        Int("config.visibility.lateNightFrom", c => c.Visibility.LateNightFromTime, (c, v) => c.Visibility.LateNightFromTime = v, 2000, 2600, 100);
        Float("config.visibility.lateNight", c => c.Visibility.LateNightOutdoorFactor, (c, v) => c.Visibility.LateNightOutdoorFactor = v, 0f, 1f, 0.05f);
        Float("config.visibility.walking", c => c.Visibility.WalkingFactor, (c, v) => c.Visibility.WalkingFactor = v, 0f, 1f, 0.05f);
        Float("config.visibility.busy", c => c.Visibility.BusyFactor, (c, v) => c.Visibility.BusyFactor = v, 0f, 1f, 0.05f);
        Section("config.visibility.section.rules");
        Bool("config.visibility.directKnows", c => c.Visibility.DirectAlwaysKnows, (c, v) => c.Visibility.DirectAlwaysKnows = v);
        Float("config.visibility.noticeRoll", c => c.Visibility.NoticeRollBelow, (c, v) => c.Visibility.NoticeRollBelow = v, 0f, 1f, 0.05f);
        foreach (EventDefinition def in _catalog.All.Where(def => def.Perception.HeardFloor > 0))
        {
            string id = def.Id;
            Float("config.visibility.heardFloor", c => c.Visibility.HeardFloorByType[id], (c, v) => c.Visibility.HeardFloorByType[id] = v, 0f, 1f, 0.05f, new { type = TypeName(id) });
        }
        Bool("config.visibility.walls", c => c.Visibility.BedroomWalls, (c, v) => c.Visibility.BedroomWalls = v);
        Int("config.visibility.doorway", c => c.Visibility.DoorwayViewTiles, (c, v) => c.Visibility.DoorwayViewTiles = v, 0, 10);
        Int("config.visibility.asleep", c => c.Visibility.AsleepInBedroomFromTime, (c, v) => c.Visibility.AsleepInBedroomFromTime = v, 1800, 2700, 100);
        Int("config.visibility.directRange", c => c.Visibility.DirectRangeTiles, (c, v) => c.Visibility.DirectRangeTiles = v, 1, 60);

        // ── Certainty levels ──
        Page("certainty");
        Paragraph("config.certainty.intro");
        Float("config.certainty.l5", c => c.Certainty.Level5, (c, v) => c.Certainty.Level5 = v, 0.05f, 1f, 0.05f);
        Float("config.certainty.l4", c => c.Certainty.Level4, (c, v) => c.Certainty.Level4 = v, 0.04f, 1f, 0.05f);
        Float("config.certainty.l3", c => c.Certainty.Level3, (c, v) => c.Certainty.Level3 = v, 0.03f, 1f, 0.05f);
        Float("config.certainty.l2", c => c.Certainty.Level2, (c, v) => c.Certainty.Level2 = v, 0.02f, 1f, 0.05f);
        Section("config.certainty.section.floors");
        foreach (EventDefinition def in _catalog.All)
        {
            string id = def.Id;
            Int("config.certainty.minLevel",
                c => c.Certainty.MinLevelByType.TryGetValue(id, out int level) ? level : 1,
                (c, v) => c.Certainty.MinLevelByType[id] = v,
                1, 5, 1, new { type = TypeName(id) });
        }

        // ── Grade ──
        Page("grade");
        Paragraph("config.grade.intro");
        Section("config.grade.section.base");
        foreach (EventDefinition def in _catalog.All)
        {
            string id = def.Id;
            Float("config.grade.base", c => c.Grade.BaseByType[id], (c, v) => c.Grade.BaseByType[id] = v, 0f, 10f, 0.5f, new { type = TypeName(id) });
        }
        Section("config.grade.section.aggravators");
        foreach (string key in GradeConfig.DefaultAggravators().Keys)
        {
            string k = key;
            Float($"config.aggravator.{k}", c => c.Grade.Aggravators[k], (c, v) => c.Grade.Aggravators[k] = v, 1f, 5f, 0.1f);
        }
        Int("config.grade.manyDrinks", c => c.Grade.ManyDrinksThreshold, (c, v) => c.Grade.ManyDrinksThreshold = v, 2, 20);
        Int("config.grade.partnerTiles", c => c.Grade.PartnerNearbyTiles, (c, v) => c.Grade.PartnerNearbyTiles = v, 1, 20);
        Int("config.grade.longStay", c => c.Grade.RoomLongStayMinutes, (c, v) => c.Grade.RoomLongStayMinutes = v, 10, 600, 10);

        // ── Scoring & memory ──
        Page("scoring");
        Paragraph("config.scoring.intro");
        Section("config.scoring.section.halfLife");
        foreach (EventDefinition def in _catalog.All)
        {
            string id = def.Id;
            Float("config.scoring.halfLife", c => c.Scoring.HalfLifeDaysByType[id], (c, v) => c.Scoring.HalfLifeDaysByType[id] = v, 0.25f, 112f, 0.25f, new { type = TypeName(id) });
        }
        Section("config.scoring.section.involvement");
        Float("config.scoring.victim", c => c.Scoring.VictimMultiplier, (c, v) => c.Scoring.VictimMultiplier = v, 1f, 5f, 0.1f);
        Float("config.scoring.relative", c => c.Scoring.RelativeMultiplier, (c, v) => c.Scoring.RelativeMultiplier = v, 1f, 5f, 0.1f);
        Float("config.scoring.vendor", c => c.Scoring.VendorMultiplier, (c, v) => c.Scoring.VendorMultiplier = v, 1f, 5f, 0.1f);
        Section("config.scoring.section.limits");
        Float("config.scoring.minScore", c => c.Scoring.MinScore, (c, v) => c.Scoring.MinScore = v, 0f, 10f, 0.05f);
        Float("config.scoring.compact", c => c.Scoring.CompactMaxGrade, (c, v) => c.Scoring.CompactMaxGrade = v, 0f, 10f, 0.5f);
        Float("config.scoring.repeatStep", c => c.Scoring.RepeatStep, (c, v) => c.Scoring.RepeatStep = v, 0f, 3f, 0.05f);
        Float("config.scoring.repeatMax", c => c.Scoring.RepeatMaxMultiplier, (c, v) => c.Scoring.RepeatMaxMultiplier = v, 1f, 10f, 0.25f);
        Float("config.scoring.notable", c => c.Scoring.NotableScore, (c, v) => c.Scoring.NotableScore = v, 0f, 20f, 0.25f);
        Int("config.scoring.notableMax", c => c.Scoring.NotableMaxPerPrompt, (c, v) => c.Scoring.NotableMaxPerPrompt = v, 0, 5);
        Float("config.scoring.notableAge", c => c.Scoring.NotableMaxAgeDays, (c, v) => c.Scoring.NotableMaxAgeDays = v, 0f, 28f, 0.25f);
        Int("config.scoring.retention", c => c.Scoring.RetentionSeasons, (c, v) => c.Scoring.RetentionSeasons = v, 1, 16);
        Float("config.scoring.forget", c => c.Scoring.ForgetBelowScore, (c, v) => c.Scoring.ForgetBelowScore = v, 0f, 1f, 0.01f);

        // ── Conversations ──
        Page("talk");
        Bool("config.talk.secret", c => c.Talk.RespectKeepSecret, (c, v) => c.Talk.RespectKeepSecret = v);
        Int("config.talk.fullChars", c => c.Talk.SnippetFullChars, (c, v) => c.Talk.SnippetFullChars = v, 20, 300, 10);
        Int("config.talk.topicChars", c => c.Talk.SnippetTopicChars, (c, v) => c.Talk.SnippetTopicChars = v, 10, 120, 5);

        // ── Night cycle ──
        Page("cycle");
        Bool("config.cycle.block", c => c.Cycle.CycleBlockEnabled, (c, v) => c.Cycle.CycleBlockEnabled = v);
        Int("config.cycle.maxEvents", c => c.Cycle.MaxEvents, (c, v) => c.Cycle.MaxEvents = v, 1, 40);
        Int("config.cycle.maxChars", c => c.Cycle.MaxChars, (c, v) => c.Cycle.MaxChars = v, 300, 3000, 100);
        Bool("config.cycle.inject", c => c.Cycle.InjectGossip, (c, v) => c.Cycle.InjectGossip = v);
        Int("config.cycle.injectCount", c => c.Cycle.InjectGossipCount, (c, v) => c.Cycle.InjectGossipCount = v, 0, 15);
        Float("config.cycle.injectMinWeight", c => c.Cycle.InjectGossipMinWeight, (c, v) => c.Cycle.InjectGossipMinWeight = v, 0f, 50f, 0.5f);

        // ── Confrontations (saturation) ──
        Page("confront");
        Paragraph("config.confront.intro");
        Bool("config.confront.enabled", c => c.Confront.Enabled, (c, v) => c.Confront.Enabled = v);
        Int("config.confront.maxReactions", c => c.Confront.MaxReactionsPerEvent, (c, v) => c.Confront.MaxReactionsPerEvent = v, 0, 20);
        Int("config.confront.triggerTiles", c => c.Confront.TriggerTiles, (c, v) => c.Confront.TriggerTiles = v, 1, 30);
        Int("config.confront.cooldown", c => c.Confront.CooldownMinutes, (c, v) => c.Confront.CooldownMinutes = v, 0, 600, 10);
        Int("config.confront.pendingExpires", c => c.Confront.PendingExpiresMinutes, (c, v) => c.Confront.PendingExpiresMinutes = v, 10, 1200, 10);
        Int("config.confront.reasonLasts", c => c.Confront.ReasonLastsMinutes, (c, v) => c.Confront.ReasonLastsMinutes = v, 10, 1200, 10);
        Int("config.confront.delay", c => c.Confront.DelayTicks, (c, v) => c.Confront.DelayTicks = v, 0, 600, 10);
        Int("config.confront.reasonLines", c => c.Confront.MaxReasonLines, (c, v) => c.Confront.MaxReasonLines = v, 1, 6);
        Bool("config.confront.emoteWaiting", c => c.Confront.EmoteWhileWaiting, (c, v) => c.Confront.EmoteWhileWaiting = v);
        Int("config.confront.emoteRange", c => c.Confront.EmoteRangeTiles, (c, v) => c.Confront.EmoteRangeTiles = v, 1, 60);
        Int("config.confront.emoteRepeat", c => c.Confront.EmoteRepeatSeconds, (c, v) => c.Confront.EmoteRepeatSeconds = v, 2, 120);
        Section("config.confront.section.meters");
        Paragraph("config.confront.meters.intro");
        foreach (MeterDefinition meter in _catalog.Meters)
        {
            MeterDefinition current = meter;
            Paragraph(() => $"{current.Label} ({current.Id}): levels {string.Join("/", current.Thresholds)} · half-life {current.HalfLifeHours:0.##}h · '{current.Emote}' bubble · {current.Delivery}");
        }

        // ── Debug ──
        Page("debug");
        Bool("config.debug.enabled", c => c.Debug.Enabled, (c, v) => c.Debug.Enabled = v);
        Bool("config.debug.verbose", c => c.Debug.Verbose, (c, v) => c.Debug.Verbose = v);
        Bool("config.debug.overlay", c => c.Debug.Overlay, (c, v) => c.Debug.Overlay = v);
        _api.AddKeybind(_manifest, () => _get().Debug.OverlayKey, v => _get().Debug.OverlayKey = v, () => T("config.debug.overlayKey.name"), Tip("config.debug.overlayKey"));
        Int("config.debug.overlaySeconds", c => c.Debug.OverlaySeconds, (c, v) => c.Debug.OverlaySeconds = v, 1, 30);
    }

    // ── helpers ──

    private string T(string key, object? tokens = null) => _helper.Translation.Get(key, tokens).ToString();

    private string TypeName(string typeId) => T($"type.{typeId}");

    /// <summary>Tooltip only if a "&lt;key&gt;.tip" translation exists.</summary>
    private Func<string>? Tip(string key, object? tokens = null)
    {
        string tipKey = key + ".tip";
        return _helper.Translation.Get(tipKey).HasValue() ? () => T(tipKey, tokens) : null;
    }

    private void Page(string pageId) => _api.AddPage(_manifest, pageId, () => T($"config.page.{pageId}"));

    private void Section(string key) => _api.AddSectionTitle(_manifest, () => T(key), Tip(key));

    private void Paragraph(string key) => _api.AddParagraph(_manifest, () => T(key));

    /// <summary>A paragraph whose text comes from the mod itself rather than a translation.</summary>
    private void Paragraph(Func<string> text) => _api.AddParagraph(_manifest, text);

    private void Bool(string key, Func<ModConfig, bool> get, Action<ModConfig, bool> set)
        => _api.AddBoolOption(_manifest, () => get(_get()), v => set(_get(), v), () => T(key + ".name"), Tip(key));

    private void Int(string key, Func<ModConfig, int> get, Action<ModConfig, int> set, int min, int max, int interval = 1, object? tokens = null)
        => _api.AddNumberOption(_manifest, () => get(_get()), v => set(_get(), v), () => T(key + ".name", tokens), Tip(key, tokens), min, max, interval);

    private void Float(string key, Func<ModConfig, float> get, Action<ModConfig, float> set, float min, float max, float interval, object? tokens = null, Func<float, string>? format = null)
        => _api.AddNumberOption(_manifest, () => get(_get()), v => set(_get(), v), () => T(key + ".name", tokens), Tip(key, tokens), min, max, interval, format ?? (v => v.ToString("0.00")));
}
