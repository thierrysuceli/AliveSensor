using System;
using System.Collections.Generic;
using AliveSensor.Core;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace AliveSensor.Sensors;

/// <summary>Owns every sensor and wires them to SMAPI, SpaceCore and Harmony.</summary>
internal sealed class SensorHub
{
    private readonly ModEntry _mod;

    public SensorHub(ModEntry mod)
    {
        _mod = mod;
        Trash = new TrashSensor(mod);
        Warp = new WarpSensor(mod);
        Purchase = new PurchaseSensor(mod);
        NpcMove = new NpcMoveSensor(mod);
        Room = new RoomSensor(mod);
        PassOut = new PassOutSensor(mod);
        LockedDoor = new LockedDoorSensor(mod);
        Talk = new TalkSensor(mod);
        SpaceCore = new SpaceCoreSensors(mod);
    }

    public TrashSensor Trash { get; }
    public WarpSensor Warp { get; }
    public PurchaseSensor Purchase { get; }
    public NpcMoveSensor NpcMove { get; }
    public RoomSensor Room { get; }
    public PassOutSensor PassOut { get; }
    public LockedDoorSensor LockedDoor { get; }
    public TalkSensor Talk { get; }
    public SpaceCoreSensors SpaceCore { get; }

    /// <summary>Sensor wiring status for as_status: name → "ok" or failure reason.</summary>
    public Dictionary<string, string> Wiring { get; } = new();

    public void Enable(IModHelper helper, Harmony harmony)
    {
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Player.Warped += (_, e) => Guard("warp", () => Warp.OnWarped(e));
        helper.Events.Display.MenuChanged += (_, e) => Guard("purchase", () => Purchase.OnMenuChanged(e));
        helper.Events.Player.InventoryChanged += (_, e) => Guard("purchase", () => Purchase.OnInventoryChanged(e));
        helper.Events.World.NpcListChanged += (_, e) => Guard("npcMove", () => NpcMove.OnNpcListChanged(e));
        Wiring["trash"] = "ok (samples CheckedGarbage 4×/s)";
        Wiring["warp"] = "ok (Player.Warped)";
        Wiring["purchase"] = "ok (MenuChanged + InventoryChanged)";
        Wiring["npcMove"] = "ok (World.NpcListChanged)";
        Wiring["room"] = "ok (samples tile 4×/s against discovered bedrooms)";

        Patch("passOut", () => PassOut.Apply(harmony));
        Patch("lockedDoor", () => LockedDoor.Apply(harmony));
        Patch("talk", () => Talk.Apply(harmony));

        _mod.SpaceCore.Subscribe(SpaceCore.OnGift, SpaceCore.OnEaten, SpaceCore.OnBomb);
        string spaceCore = _mod.SpaceCore.Subscribed ? "ok (SpaceCore)" : "OFF (SpaceCore events unavailable)";
        Wiring["gift"] = spaceCore;
        Wiring["consume"] = spaceCore;
        Wiring["bomb"] = spaceCore;

        _mod.Log.Debug("Sensors", $"Wiring: {string.Join("; ", Wiring)}");
    }

    /// <summary>Called on SaveLoaded and DayStarted to set daily baselines.</summary>
    public void ResetDaily()
    {
        Guard("trash", Trash.Reset);
        Guard("room", Room.Reset);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        Warp.Track();
        if (!e.IsMultipleOf(15))
            return;
        Guard("trash", Trash.Sample);
        Guard("room", Room.Sample);
    }

    private void Patch(string name, Func<bool> apply)
    {
        try
        {
            Wiring[name] = apply() ? "ok (Harmony)" : "OFF (target not found)";
        }
        catch (Exception ex)
        {
            Wiring[name] = $"FAILED ({ex.GetType().Name})";
            _mod.Log.Error($"Could not enable the {name} sensor", ex);
        }
    }

    private void Guard(string sensor, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _mod.Log.Error($"Sensor '{sensor}' failed", ex);
        }
    }
}
