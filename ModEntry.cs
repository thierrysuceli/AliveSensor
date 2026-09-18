using System;
using System.Linq;
using AliveSensor.Commands;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Delivery;
using AliveSensor.Integration;
using AliveSensor.Memory;
using AliveSensor.Sensors;
using AliveSensor.World;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace AliveSensor;

/// <summary>AliveSensor: NPCs remember what they witness the player do (see ALIVESENSOR_PRD.md).</summary>
public sealed class ModEntry : Mod
{
    /// <summary>Highest PRD phase whose features are implemented in this build.</summary>
    internal const int ImplementedPhase = 7;

    internal ModConfig Config { get; private set; } = null!;
    internal Log Log { get; private set; } = null!;
    internal MemoryStore Store { get; private set; } = null!;
    internal AliveNpcsBridge AliveNpcs { get; private set; } = null!;
    internal SpaceCoreBridge SpaceCore { get; private set; } = null!;
    internal Homes Homes { get; private set; } = null!;
    internal Relationships Relationships { get; private set; } = null!;
    internal RoomIndex Rooms { get; private set; } = null!;
    internal Landmarks Landmarks { get; private set; } = null!;
    internal WitnessResolver Witnesses { get; private set; } = null!;
    internal RuleEvaluator Rules { get; private set; } = null!;
    internal EventRecorder Recorder { get; private set; } = null!;
    internal Overlay Overlay { get; private set; } = null!;
    internal SensorHub Sensors { get; private set; } = null!;
    internal MemorySelector Selector { get; private set; } = null!;
    internal EventCatalog Catalog { get; private set; } = null!;
    internal MemoryRenderer Renderer { get; private set; } = null!;
    internal NpcPromptBlock NpcBlock { get; private set; } = null!;
    internal CyclePromptBlock CycleBlock { get; private set; } = null!;
    internal GossipReinforcer Reinforcer { get; private set; } = null!;
    internal FeelingEngine Feelings { get; private set; } = null!;
    internal GmcmIntegration? Gmcm { get; private set; }

    /// <summary>False when this player is a farmhand: sensors stay off (multiplayer is out of MVP scope).</summary>
    internal bool SensorsAllowed { get; private set; }

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        Log = new Log(Monitor, () => Config);
        Catalog = EventCatalog.Load(helper, Log);
        int fixes = Config.Normalize(Catalog);
        helper.WriteConfig(Config);
        if (fixes > 0)
            Log.Debug("Config", $"Normalized {fixes} value(s) in config.json.");

        Store = new MemoryStore(helper.DirectoryPath, Log);
        AliveNpcs = new AliveNpcsBridge(helper, ModManifest, Log);
        SpaceCore = new SpaceCoreBridge(helper, Log);
        Homes = new Homes(Log);
        Relationships = new Relationships(Log);
        Rooms = new RoomIndex(() => Config, Log);
        Landmarks = new Landmarks(() => Config, Homes, Rooms, Log);
        Witnesses = new WitnessResolver(() => Config, Catalog, Log) { Rooms = Rooms };
        Rules = new RuleEvaluator(Catalog, Log);
        Recorder = new EventRecorder(this);
        Overlay = new Overlay(() => Config, Log);
        Sensors = new SensorHub(this);
        Selector = new MemorySelector(() => Config, Relationships);
        Renderer = new MemoryRenderer(Catalog);
        NpcBlock = new NpcPromptBlock(() => Config, Store, AliveNpcs, Selector, Renderer, Log);
        CycleBlock = new CyclePromptBlock(() => Config, Store, AliveNpcs, Renderer, Log);
        Reinforcer = new GossipReinforcer(() => Config, Store, AliveNpcs, Renderer, Log);
        Feelings = new FeelingEngine(this);
        Feelings.Prime();
        NpcBlock.ReasonFor = Feelings.ReasonFor;
        NpcBlock.ReasonEventIds = Feelings.ReasonEventIds;
        Store.Changed += Feelings.OnMemoryChanged;

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicked += (_, e) =>
        {
            if (!e.IsMultipleOf(10) || !Context.IsWorldReady || !SensorsAllowed)
                return;
            try
            {
                Feelings.Tick();
            }
            catch (Exception ex)
            {
                Log.Error("Feeling engine tick failed", ex);
            }
        };
        helper.Events.Input.ButtonPressed += (_, e) => Overlay.OnButtonPressed(e, Helper);
        helper.Events.Display.RenderedWorld += (_, e) => Overlay.OnRenderedWorld(e);

        new ConsoleCommands(this).Register(helper.ConsoleCommands);

        Log.Info($"AliveSensor {ModManifest.Version} loaded. Debug logs: {(Config.Debug.Enabled ? "ON" : "off — use 'as_debug on'")}. Commands: as_help.");
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        Log.Debug("Lifecycle", "GameLaunched: connecting to AliveNpcs and SpaceCore.");
        var harmony = new Harmony(ModManifest.UniqueID);

        AliveNpcs.Connect();
        AliveNpcs.ApplyPatches(harmony);
        SpaceCore.Connect();

        if (NpcBlock.Register())
            Log.Debug("Lifecycle", "Channel A (conversation memories) is live.");
        if (CycleBlock.Register())
            Log.Debug("Lifecycle", "Channel B (night gossip) is live.");

        Sensors.Enable(Helper, harmony);

        Gmcm = new GmcmIntegration(Helper, ModManifest, Catalog, () => Config, value => Config = value, OnConfigSaved, Log);
        Gmcm.Register();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        SensorsAllowed = Context.IsMainPlayer;
        if (!SensorsAllowed)
            Log.Warn("AliveSensor does not support multiplayer farmhands yet; sensors are off for this player.");

        Log.Debug("Lifecycle", $"SaveLoaded: '{Constants.SaveFolderName}', player '{Game1.player.Name}', main player: {Context.IsMainPlayer}.");
        Store.Load(Constants.SaveFolderName!);
        Homes.Build();
        Relationships.Build(AliveNpcs);
        Rooms.Clear();
        Landmarks.Clear();
        Sensors.ResetDaily();
        Feelings.Reset("save loaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        SDate today = SDate.Now();
        Log.Debug("Lifecycle", $"DayStarted: {today} (day {today.DaysSinceStart}), weather '{Game1.currentLocation?.GetWeather()?.Weather ?? "?"}'. Memories in store: {Store.Count}.");
        Store.PruneCounters(today.DaysSinceStart);
        ApplyRetention(today.DaysSinceStart);
        Sensors.ResetDaily();
        Feelings.OnDayStarted();
    }

    /// <summary>Runs before AliveNpcs' own DayEnding handler so the gossip reinforcement lands in tonight's analysis.</summary>
    [EventPriority(EventPriority.High)]
    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        int day = SDate.Now().DaysSinceStart;
        int today = Store.Snapshot.Count(ev => ev.TotalDay == day);
        Log.Debug("Lifecycle", $"DayEnding: {SDate.Now()} · {today} event(s) today · {Store.Count} in store.");
        CycleBlock.PinDay(day);
        Reinforcer.Run(day);
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        Store.Save("game saving");
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        Store.Unload();
        Rooms.Clear();
        Landmarks.Clear();
        SensorsAllowed = false;
    }

    private void OnConfigSaved()
    {
        Log.Debug("Config", $"Settings applied: enabled={Config.Enabled}, channel A={Config.Delivery.NpcBlockEnabled}, B={Config.Cycle.CycleBlockEnabled}, C={Config.Cycle.InjectGossip}, debug={Config.Debug.Enabled}, verbose={Config.Debug.Verbose}.");
    }

    /// <summary>Forget events older than the retention period that no witness holds strongly anymore.</summary>
    private void ApplyRetention(int today)
    {
        int maxAge = Config.Scoring.RetentionSeasons * 28;
        float threshold = Config.Scoring.ForgetBelowScore;
        int removed = Store.RemoveWhere(e =>
        {
            if (today - e.TotalDay <= maxAge)
                return false;
            float halfLife = Config.Scoring.HalfLifeDaysByType.TryGetValue(e.Type, out float h) ? h : 3f;
            double recency = Math.Pow(0.5, (today - e.TotalDay) / halfLife);
            double best = e.Witnesses.Values.Select(w => e.Grade * recency * w.Visibility * w.Bonus).DefaultIfEmpty(0).Max();
            return best < threshold;
        });
        if (removed > 0)
            Log.Debug("Retention", $"Forgot {removed} old event(s) (older than {maxAge} days and score below {threshold:0.00}).");
    }
}
