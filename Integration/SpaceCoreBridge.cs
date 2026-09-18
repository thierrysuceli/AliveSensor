using System;
using System.Runtime.CompilerServices;
using AliveSensor.Core;
using Microsoft.Xna.Framework;
using SpaceCore.Events;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace AliveSensor.Integration;

/// <summary>SpaceCore touch points. Events are static on SpaceCore.Events.SpaceEvents (verified in 1.28.4).</summary>
internal sealed class SpaceCoreBridge
{
    public const string ModId = "spacechase0.SpaceCore";

    private readonly IModHelper _helper;
    private readonly Log _log;
    private Action<Farmer, NPC, SObject>? _onGift;
    private Action<Farmer>? _onEaten;
    private Action<Farmer?, Vector2, int>? _onBomb;

    public SpaceCoreBridge(IModHelper helper, Log log)
    {
        _helper = helper;
        _log = log;
    }

    public ISemanticVersion? Version { get; private set; }

    public bool HasSpaceEvents { get; private set; }

    public bool Subscribed { get; private set; }

    public void Connect()
    {
        Version = _helper.ModRegistry.Get(ModId)?.Manifest.Version;
        try
        {
            HasSpaceEvents = ProbeSpaceEvents();
        }
        catch (Exception ex)
        {
            HasSpaceEvents = false;
            _log.Debug("SpaceCore", $"SpaceEvents probe failed: {ex.GetType().Name}: {ex.Message}");
        }
        _log.Debug("SpaceCore", $"Detected SpaceCore {Version?.ToString() ?? "(not found)"}; SpaceEvents {(HasSpaceEvents ? "ok" : "MISSING")}.");
        if (!HasSpaceEvents)
            _log.Warn("SpaceCore events are unavailable: bomb, gift and eating/drinking sensors will stay off. Run 'as_status' for details.");
    }

    /// <summary>Subscribe the gift, eaten and bomb sensors. Handlers never let exceptions reach SpaceCore.</summary>
    public void Subscribe(Action<Farmer, NPC, SObject> onGift, Action<Farmer> onEaten, Action<Farmer?, Vector2, int> onBomb)
    {
        if (!HasSpaceEvents || Subscribed)
            return;
        _onGift = onGift;
        _onEaten = onEaten;
        _onBomb = onBomb;
        try
        {
            AttachHandlers();
            Subscribed = true;
            _log.Debug("SpaceCore", "Subscribed to AfterGiftGiven, OnItemEaten and BombExploded.");
        }
        catch (Exception ex)
        {
            _log.Error("Could not subscribe to SpaceCore events", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeSpaceEvents() => typeof(SpaceEvents).FullName is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AttachHandlers()
    {
        SpaceEvents.AfterGiftGiven += HandleGift;
        SpaceEvents.OnItemEaten += HandleEaten;
        SpaceEvents.BombExploded += HandleBomb;
    }

    private void HandleGift(object? sender, EventArgsGiftGiven e)
    {
        try
        {
            _onGift?.Invoke(sender as Farmer ?? Game1.player, e.Npc, e.Gift);
        }
        catch (Exception ex)
        {
            _log.Error("Gift sensor failed", ex);
        }
    }

    private void HandleEaten(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Farmer farmer)
                _onEaten?.Invoke(farmer);
        }
        catch (Exception ex)
        {
            _log.Error("Eating/drinking sensor failed", ex);
        }
    }

    private void HandleBomb(object? sender, EventArgsBombExploded e)
    {
        try
        {
            _onBomb?.Invoke(sender as Farmer, e.Position, e.Radius);
        }
        catch (Exception ex)
        {
            _log.Error("Explosion sensor failed", ex);
        }
    }
}
