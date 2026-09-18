using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AliveNpcs.Api;
using AliveSensor.Config;
using AliveSensor.Core;
using AliveSensor.Integration;
using AliveSensor.Memory;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace AliveSensor.Delivery;

/// <summary>
/// Channel A (PRD §9): the NPC's own witnessed memories, injected into its AliveNpcs conversation prompts.
/// Runs on AliveNpcs' background prompt thread: reads only the store snapshot, config and game clock.
/// </summary>
internal sealed class NpcPromptBlock
{
    public const string BlockId = "witnessed-memories";
    public const string Target = "dialogue";

    public const string Header = "[Things you personally witnessed — real events you saw or heard yourself]";
    public const string Rules = "Certainty matters: \"clearly saw\"/\"saw\" = sure; \"think you saw\" = unsure, say you think so; \"not sure\"/\"looked like\" = vague, speak hesitantly. Only people named as present were there. Invent nothing beyond these lines.";
    public const string Closing = "These memories are true. If the farmer asks whether you saw, heard or noticed anything, answer from them in character (you may be upset, evasive or teasing, but never claim nothing happened). The line marked [stands out] just happened in front of you: react to it first, in your own words, before any other topic (rumors, favors, small talk). Mention the others only when relevant.";

    private readonly Func<ModConfig> _config;
    private readonly MemoryStore _store;
    private readonly AliveNpcsBridge _aliveNpcs;
    private readonly MemorySelector _selector;
    private readonly MemoryRenderer _renderer;
    private readonly Log _log;

    /// <summary>Why the NPC walked up to the farmer (confrontation), if it did; background-thread safe.</summary>
    public Func<string, string?>? ReasonFor { get; set; }

    /// <summary>The memory id(s) <see cref="ReasonFor"/> is about, if any — guaranteed a "[stands out]" slot below.</summary>
    public Func<string, IReadOnlyList<string>>? ReasonEventIds { get; set; }

    public NpcPromptBlock(Func<ModConfig> config, MemoryStore store, AliveNpcsBridge aliveNpcs, MemorySelector selector, MemoryRenderer renderer, Log log)
    {
        _config = config;
        _store = store;
        _aliveNpcs = aliveNpcs;
        _selector = selector;
        _renderer = renderer;
        _log = log;
    }

    public bool Register() => _aliveNpcs.RegisterPromptBlock(BlockId, Target, _config().Delivery.MaxChars, Provide);

    /// <summary>Called by AliveNpcs whenever it builds a conversation prompt.</summary>
    private string? Provide(ExperimentalRuntimeHookContext context)
    {
        try
        {
            ModConfig config = _config();
            if (!config.Enabled || !config.Delivery.NpcBlockEnabled)
            {
                _log.Verbose("Prompt:A", "Skipped: AliveSensor or channel A disabled.");
                return null;
            }

            string? npc = _aliveNpcs.GetCurrentNpcName(out string source);
            if (npc is null)
            {
                _log.Debug("Prompt:A", $"AliveNpcs asked for a dialogue block but no NPC could be identified (mode '{context.ActiveModeId}'); nothing injected.");
                return null;
            }

            string? block = Build(npc, includeWhenEmpty: false, out var selected, out int candidates);
            if (block is null)
            {
                _log.Debug("Prompt:A", $"{npc} (via {source}): no memories to inject ({candidates} witnessed, none above min score {config.Scoring.MinScore:0.00}).");
                return null;
            }

            _log.Debug("Prompt:A", $"{npc} (via {source}): injecting {selected.Count} of {candidates} memory(ies), {block.Length} chars: {string.Join(", ", selected.Select(m => $"{m.Event.Type}/L{m.Witness.Level}/{m.Score:0.00}"))}.");
            _log.Verbose("Prompt:A", $"{npc} block:\n{block}");
            return block;
        }
        catch (Exception ex)
        {
            // AliveNpcs disables a provider after 3 consecutive exceptions; never let one escape.
            _log.Error("Building the witnessed-memories block failed", ex);
            return null;
        }
    }

    /// <summary>Build the block for an NPC. Also used by as_prompt.</summary>
    public string? Build(string npc, bool includeWhenEmpty, out List<ScoredMemory> selected, out int candidates)
    {
        ModConfig c = _config();
        SDate now = SDate.Now();
        int time = Game1.timeOfDay;
        selected = _selector.Select(_store.Snapshot, npc, now.DaysSinceStart, time, out candidates);
        string? reason = ReasonFor?.Invoke(npc);

        // Whatever memory the reason above is actually about must survive the budget cut below — otherwise the
        // NPC is told to react to "the line marked [stands out]" and there might not be one. All of them get
        // priority for the budget; only the single strongest one is forced to carry the mark itself.
        IReadOnlyList<string> pinned = reason is null ? Array.Empty<string>() : ReasonEventIds?.Invoke(npc) ?? Array.Empty<string>();
        string? forcedMark = pinned.Count > 0 ? pinned[0] : null;
        if (pinned.Count > 0)
            selected = selected.OrderByDescending(memory => pinned.Contains(memory.Event.Id, StringComparer.Ordinal)).ToList();

        if (selected.Count == 0 && reason is null && !includeWhenEmpty)
            return null;

        var text = new StringBuilder();
        if (reason is not null)
        {
            text.AppendLine(reason);
            text.AppendLine();
            _log.Debug("Prompt:A", $"{npc}: confrontation reason included ({reason.Length} chars).");
        }
        text.AppendLine(Header);
        string farmer = Game1.player?.Name ?? "";
        string who = farmer.Length == 0 ? "" : $"{farmer} in these lines is the farmer you are talking to right now: what {farmer} did, the person in front of you did. ";
        text.AppendLine(who + Rules);
        if (selected.Count == 0)
            text.AppendLine("- (nothing yet)");

        int budget = c.Delivery.MaxChars - (reason?.Length + 2 ?? 0) - Header.Length - who.Length - Rules.Length - Closing.Length - 6;
        int used = 0;
        var kept = new List<ScoredMemory>();
        int marked = 0;
        foreach (ScoredMemory memory in selected)
        {
            // Only the strongest fresh memories get the mark, so the NPC knows exactly what to react to —
            // except the memory the reason above is actually about, which always gets it.
            bool standsOut = string.Equals(memory.Event.Id, forcedMark, StringComparison.Ordinal)
                || (marked < c.Scoring.NotableMaxPerPrompt && memory.StandsOut(c));
            string line = _renderer.NpcLine(memory, npc, now.DaysSinceStart, time, now.Year, c, standsOut);
            if (used + line.Length + 1 > budget)
            {
                _log.Debug("Prompt:A", $"{npc}: dropped {memory.Event.Id} (character budget {c.Delivery.MaxChars} reached).");
                continue;
            }
            text.AppendLine(line);
            used += line.Length + 1;
            if (standsOut)
                marked++;
            kept.Add(memory);
        }
        text.Append(Closing);
        selected = kept;
        return text.ToString();
    }
}

/// <summary>
/// Channel B: the analysed day's witnessed events inside AliveNpcs' nightly gossip/diary prompt.
/// The day is pinned at DayEnding because AliveNpcs builds that prompt after the new day may have started.
/// </summary>
internal sealed class CyclePromptBlock
{
    public const string BlockId = "witnessed-today";
    public const string Target = "gossip";

    private readonly Func<ModConfig> _config;
    private readonly MemoryStore _store;
    private readonly AliveNpcsBridge _aliveNpcs;
    private readonly MemoryRenderer _renderer;
    private readonly Log _log;
    private volatile int _pinnedDay;

    public CyclePromptBlock(Func<ModConfig> config, MemoryStore store, AliveNpcsBridge aliveNpcs, MemoryRenderer renderer, Log log)
    {
        _config = config;
        _store = store;
        _aliveNpcs = aliveNpcs;
        _renderer = renderer;
        _log = log;
    }

    public bool Register() => _aliveNpcs.RegisterPromptBlock(BlockId, Target, _config().Cycle.MaxChars, Provide);

    public void PinDay(int totalDay)
    {
        _pinnedDay = totalDay;
        _log.Debug("Prompt:B", $"Night cycle pinned to day {totalDay}.");
    }

    private string? Provide(ExperimentalRuntimeHookContext context)
    {
        try
        {
            ModConfig c = _config();
            if (!c.Enabled || !c.Cycle.CycleBlockEnabled)
                return null;
            int day = _pinnedDay > 0 ? _pinnedDay : SDate.Now().DaysSinceStart;
            string? block = Build(day, out int count);
            _log.Debug("Prompt:B", block is null ? $"Gossip prompt: nothing witnessed on day {day}." : $"Gossip prompt: injecting {count} event(s) from day {day}, {block.Length} chars.");
            _log.Verbose("Prompt:B", block ?? "");
            return block;
        }
        catch (Exception ex)
        {
            _log.Error("Building the night-cycle block failed", ex);
            return null;
        }
    }

    public string? Build(int day, out int count)
    {
        ModConfig c = _config();
        var events = _store.Snapshot
            .Where(e => e.TotalDay == day && e.Witnesses.Count > 0)
            .OrderByDescending(e => MemorySelector.GossipWeight(c, e))
            .ThenBy(e => e.Time)
            .Take(c.Cycle.MaxEvents)
            .ToList();
        count = 0;
        if (events.Count == 0)
            return null;

        var text = new StringBuilder("[Things villagers witnessed the farmer do today — AliveSensor]\n");
        const string rules = "Use these as possible gossip sources. Only the listed witnesses know about each event, with the certainty shown; do not invent details.";
        int budget = c.Cycle.MaxChars - text.Length - rules.Length - 2;
        foreach (MemoryEvent e in events.OrderBy(e => e.Time))
        {
            string line = _renderer.CycleLine(e, c);
            if (line.Length + 1 > budget)
                continue;
            text.AppendLine(line);
            budget -= line.Length + 1;
            count++;
        }
        text.Append(rules);
        return count == 0 ? null : text.ToString();
    }
}

/// <summary>Channel C: at DayEnding, hand the most relevant events to AliveNpcs' official InjectGossip.</summary>
internal sealed class GossipReinforcer
{
    private readonly Func<ModConfig> _config;
    private readonly MemoryStore _store;
    private readonly AliveNpcsBridge _aliveNpcs;
    private readonly MemoryRenderer _renderer;
    private readonly Log _log;

    public GossipReinforcer(Func<ModConfig> config, MemoryStore store, AliveNpcsBridge aliveNpcs, MemoryRenderer renderer, Log log)
    {
        _config = config;
        _store = store;
        _aliveNpcs = aliveNpcs;
        _renderer = renderer;
        _log = log;
    }

    public void Run(int day)
    {
        ModConfig c = _config();
        if (!c.Enabled || !c.Cycle.InjectGossip || c.Cycle.InjectGossipCount <= 0)
            return;

        var ranked = _store.Snapshot
            .Where(e => e.TotalDay == day && e.Witnesses.Count > 0)
            .Select(e => (Event: e, Weight: MemorySelector.GossipWeight(c, e)))
            .Where(p => p.Weight >= c.Cycle.InjectGossipMinWeight)
            .OrderByDescending(p => p.Weight)
            .ToList();

        // One event per type first, so a bombing spree doesn't take every slot; then fill by weight.
        var top = ranked.GroupBy(p => p.Event.Type).Select(g => g.First()).OrderByDescending(p => p.Weight).Take(c.Cycle.InjectGossipCount).ToList();
        foreach (var extra in ranked.Where(p => !top.Contains(p)))
        {
            if (top.Count >= c.Cycle.InjectGossipCount)
                break;
            top.Add(extra);
        }
        _log.Debug("Prompt:C", $"Day {day}: {top.Count} of {ranked.Count} event(s) reinforced as gossip: {string.Join(", ", top.Select(p => $"{p.Event.Type}/{p.Weight:0.0}"))}.");
        foreach (var (e, _) in top)
            _aliveNpcs.InjectGossip(_renderer.CycleLine(e, c).TrimStart('-', ' '));
    }
}
