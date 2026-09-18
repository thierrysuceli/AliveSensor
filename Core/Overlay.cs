using System;
using System.Collections.Generic;
using System.Linq;
using AliveSensor.Config;
using AliveSensor.Memory;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace AliveSensor.Core;

/// <summary>Debug overlay: after an event, shows the certainty level above each witness for a few seconds.</summary>
internal sealed class Overlay
{
    private readonly Func<ModConfig> _config;
    private readonly Log _log;
    private MemoryEvent? _event;
    private DateTime _until;

    public Overlay(Func<ModConfig> config, Log log)
    {
        _config = config;
        _log = log;
    }

    public void Show(MemoryEvent e)
    {
        _event = e;
        _until = DateTime.UtcNow.AddSeconds(_config().Debug.OverlaySeconds);
    }

    public void OnButtonPressed(ButtonPressedEventArgs e, IModHelper helper)
    {
        ModConfig c = _config();
        if (!Context.IsWorldReady || e.Button != c.Debug.OverlayKey)
            return;
        c.Debug.Overlay = !c.Debug.Overlay;
        helper.WriteConfig(c);
        Game1.addHUDMessage(new HUDMessage($"AliveSensor overlay {(c.Debug.Overlay ? "ON" : "OFF")}", HUDMessage.newQuest_type));
        _log.Debug("Overlay", $"Overlay {(c.Debug.Overlay ? "enabled" : "disabled")} with {e.Button}.");
    }

    public void OnRenderedWorld(RenderedWorldEventArgs e)
    {
        MemoryEvent? ev = _event;
        if (ev is null || !_config().Debug.Overlay || DateTime.UtcNow > _until || !Context.IsWorldReady)
            return;
        GameLocation location = Game1.currentLocation;
        if (location is null)
            return;

        Vector2 playerPos = Game1.GlobalToLocal(Game1.viewport, Game1.player.Position + new Vector2(0, -150));
        Utility.drawTextWithShadow(e.SpriteBatch, $"{ev.Type} · grade {ev.Grade:0.0}", Game1.smallFont, playerPos, Color.Yellow);

        foreach (NPC npc in location.characters.ToList())
        {
            if (!ev.Witnesses.TryGetValue(npc.Name, out WitnessRecord? w))
                continue;
            Color color = w.Level switch { 5 => Color.LimeGreen, 4 => Color.GreenYellow, 3 => Color.Yellow, 2 => Color.Orange, _ => Color.OrangeRed };
            Vector2 pos = Game1.GlobalToLocal(Game1.viewport, npc.Position + new Vector2(0, -130));
            Utility.drawTextWithShadow(e.SpriteBatch, $"L{w.Level} {w.Visibility:0.00}{(w.Direct ? " direct" : "")}", Game1.smallFont, pos, color);
        }
    }
}
