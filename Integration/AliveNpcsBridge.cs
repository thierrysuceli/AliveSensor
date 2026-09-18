using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AliveNpcs.Api;
using AliveNpcs.Data;
using AliveNpcs.Patches;
using AliveSensor.Core;
using HarmonyLib;
using StardewModdingAPI;

namespace AliveSensor.Integration;

/// <summary>
/// Every touch point with AliveNpcs lives here. Each one is probed at launch so a future AliveNpcs
/// update that renames something disables only that feature (reported by as_status), never the game.
/// </summary>
internal sealed class AliveNpcsBridge
{
    public const string ModId = "Lucas.AliveNpcs";
    public const string ApplyReactionMethod = "AliveNpcs.Controllers.ResponseFlowController:ApplyReaction";

    private readonly IModHelper _helper;
    private readonly IManifest _manifest;
    private readonly Log _log;
    private readonly List<IDisposable> _registrations = new();

    public AliveNpcsBridge(IModHelper helper, IManifest manifest, Log log)
    {
        _helper = helper;
        _manifest = manifest;
        _log = log;
    }

    public ISemanticVersion? Version { get; private set; }
    public IAliveNpcsApi? Api { get; private set; }
    public IAliveNpcsExperimentalContentApi? Experimental { get; private set; }

    public bool HasCurrentNpc { get; private set; }
    public bool HasPromptScope { get; private set; }
    public bool HasRelationshipPairs { get; private set; }
    public bool HasApplyReaction { get; private set; }
    /// <summary>Private DialoguePatches state + DialogueManager.InvalidateNpcDialogueCache, used to let an NPC start a conversation.</summary>
    public bool HasDialogueStart { get; private set; }

    private System.Reflection.FieldInfo? _generatingField;
    private System.Reflection.FieldInfo? _dialogueManagerField;

    public List<string> RegisteredBlocks { get; } = new();

    public void Connect()
    {
        Version = _helper.ModRegistry.Get(ModId)?.Manifest.Version;
        _log.Debug("AliveNpcs", $"Detected AliveNpcs {Version?.ToString() ?? "(not found)"}.");

        Api = TryGetApi<IAliveNpcsApi>("IAliveNpcsApi");
        Experimental = TryGetApi<IAliveNpcsExperimentalContentApi>("IAliveNpcsExperimentalContentApi");
        ReadCapabilities();

        HasCurrentNpc = Probe("DialoguePatches.CurrentNpc", ProbeCurrentNpc);
        HasRelationshipPairs = Probe("NpcPersonalities.RelationshipPairs", ProbeRelationshipPairs);
        HasApplyReaction = Probe("ResponseFlowController.ApplyReaction", () => AccessTools.Method(ApplyReactionMethod) is not null);
        HasDialogueStart = Probe("DialoguePatches dialogue start (confrontations)", ProbeDialogueStart);

        if (Version is not null && Version.IsOlderThan("1.6.1"))
            _log.Warn($"AliveNpcs {Version} is older than the version AliveSensor was built for (1.6.1). Some features may not work.");
        if (Version is not null && Version.IsNewerThan("1.6.1"))
            _log.Debug("AliveNpcs", $"AliveNpcs {Version} is newer than 1.6.1; all hooks were probed above.");

        if (Experimental is null || (!HasCurrentNpc && !HasPromptScope))
            _log.Warn("AliveSensor could not find the AliveNpcs hooks it needs to add memories to conversations. Run 'as_status' for details.");
    }

    /// <summary>Apply the read-only Harmony patch that tells us which NPC a prompt is for.</summary>
    public void ApplyPatches(Harmony harmony)
    {
        try
        {
            HasPromptScope = PromptNpcScope.Apply(harmony, _log);
        }
        catch (Exception ex)
        {
            HasPromptScope = false;
            _log.Error("Could not patch AliveNpcs' prompt composer; conversation openings may miss memories", ex);
        }
    }

    /// <summary>Register a dynamic prompt block. ModeId "" = active in every experimental mode, including None.</summary>
    public bool RegisterPromptBlock(string blockId, string target, int maxChars, Func<ExperimentalRuntimeHookContext, string?> provider)
    {
        if (Experimental is null)
        {
            _log.Debug("AliveNpcs", $"Cannot register block '{blockId}': experimental content API unavailable.");
            return false;
        }

        try
        {
            IDisposable handle = Experimental.RegisterDynamicPromptBlock(new DynamicPromptBlockRegistration
            {
                OwnerModId = _manifest.UniqueID,
                BlockId = blockId,
                ModeId = "",
                Target = target,
                MaxChars = maxChars,
                Provider = provider,
            });
            _registrations.Add(handle);
            RegisteredBlocks.Add($"{blockId} → {target} (≤{maxChars} chars)");
            _log.Debug("AliveNpcs", $"Registered dynamic prompt block '{blockId}' for target '{target}' (max {maxChars} chars).");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Could not register the '{blockId}' prompt block with AliveNpcs", ex);
            return false;
        }
    }

    /// <summary>
    /// Name of the NPC whose conversation prompt is being built, or null. Safe from any thread.
    /// Prefers the prompt composer scope (exact, covers openings); falls back to DialoguePatches.CurrentNpc.
    /// </summary>
    public string? GetCurrentNpcName(out string source)
    {
        string? scoped = PromptNpcScope.Current;
        if (!string.IsNullOrWhiteSpace(scoped))
        {
            source = "composer";
            return scoped;
        }

        source = "current-npc";
        if (!HasCurrentNpc)
            return null;
        try
        {
            return ReadCurrentNpcName();
        }
        catch (Exception ex)
        {
            _log.WarnOnce("current-npc-read", $"Reading AliveNpcs' current NPC failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>AliveNpcs' fixed table of related NPC pairs (family, couples, close friends).</summary>
    public IReadOnlyList<(string Npc1, string Npc2, string Relationship)> GetRelationshipPairs()
    {
        if (!HasRelationshipPairs)
            return Array.Empty<(string, string, string)>();
        try
        {
            return ReadRelationshipPairs();
        }
        catch (Exception ex)
        {
            _log.WarnOnce("relationship-pairs-read", $"Reading AliveNpcs' relationship table failed: {ex.Message}");
            return Array.Empty<(string, string, string)>();
        }
    }

    public bool InjectGossip(string text)
    {
        if (Api is null)
            return false;
        try
        {
            bool accepted = Api.InjectGossip(_manifest.UniqueID, text);
            _log.Debug("AliveNpcs", $"InjectGossip {(accepted ? "accepted" : "rejected")}: \"{text}\"");
            return accepted;
        }
        catch (Exception ex)
        {
            _log.Error("InjectGossip failed", ex);
            return false;
        }
    }

    public void Dispose()
    {
        foreach (IDisposable handle in _registrations)
        {
            try { handle.Dispose(); } catch { /* shutting down */ }
        }
        _registrations.Clear();
        RegisteredBlocks.Clear();
    }

    private T? TryGetApi<T>(string label) where T : class
    {
        try
        {
            T? api = _helper.ModRegistry.GetApi<T>(ModId);
            _log.Debug("AliveNpcs", $"{label}: {(api is null ? "NOT available" : "ok")}.");
            return api;
        }
        catch (Exception ex)
        {
            _log.Error($"Could not load AliveNpcs' {label}", ex);
            return null;
        }
    }

    private bool ProbeDialogueStart()
    {
        _generatingField = AccessTools.Field(typeof(DialoguePatches), "_generatingDialogue");
        _dialogueManagerField = AccessTools.Field(typeof(DialoguePatches), "_dialogueManager");
        return _generatingField is not null && _dialogueManagerField is not null
            && AccessTools.Method(_dialogueManagerField.FieldType, "InvalidateNpcDialogueCache", new[] { typeof(string) }) is not null;
    }

    /// <summary>Whether AliveNpcs is busy generating a line (an NPC click would be ignored now).</summary>
    public bool IsGeneratingDialogue()
    {
        try
        {
            return HasDialogueStart && _generatingField!.GetValue(null) is true;
        }
        catch (Exception ex)
        {
            _log.WarnOnce("generating-read", $"Reading AliveNpcs' generation state failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>Drop today's cached line for an NPC so the next interaction generates a fresh one (also lifts the one-talk-per-day limit).</summary>
    public bool InvalidateDialogueCache(string npcName)
    {
        try
        {
            object? manager = _dialogueManagerField?.GetValue(null);
            if (manager is null)
                return false;
            AccessTools.Method(manager.GetType(), "InvalidateNpcDialogueCache", new[] { typeof(string) })!.Invoke(manager, new object[] { npcName });
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Clearing AliveNpcs' cached dialogue for {npcName} failed", ex);
            return false;
        }
    }

    /// <summary>
    /// What this AliveNpcs build supports, as it reports itself. Asking the build beats comparing version
    /// strings, which break on forks and pre-releases.
    /// </summary>
    public ExperimentalContentCapabilities? Capabilities { get; private set; }

    /// <summary>True when this build accepts the dynamic prompt blocks AliveSensor delivers everything through.</summary>
    public bool HasDynamicPromptBlocks => Capabilities?.DynamicPromptBlocks ?? Experimental is not null;

    /// <summary>True when a block can target the first-meeting greeting, not just ordinary dialogue.</summary>
    public bool HasGreetingTarget => Capabilities?.GreetingPromptTarget ?? false;

    private void ReadCapabilities()
    {
        if (Experimental is null)
            return;
        try
        {
            Capabilities = Experimental.GetCapabilities();
            if (Capabilities is not null)
            {
                _log.Debug("AliveNpcs", $"Capabilities: dynamicPromptBlocks={Capabilities.DynamicPromptBlocks}, greetingTarget={Capabilities.GreetingPromptTarget}, "
                    + $"modeScopedNpcContext={Capabilities.ModeScopedNpcContext}, storyArcHooks={Capabilities.StoryArcHooks}.");
            }
        }
        catch (Exception ex)
        {
            // An older build without the method: fall back to assuming only what we can see.
            _log.Debug("AliveNpcs", $"GetCapabilities unavailable ({ex.GetType().Name}); assuming the baseline feature set.");
        }
    }

    /// <summary>Whether AliveNpcs handles this NPC at all.</summary>
    public bool IsNpcDisabled(string npcName)
    {
        try
        {
            return Api?.IsNpcDisabled(npcName) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The villagers AliveNpcs is willing to write for: everyone the player has not switched off, minus the
    /// characters whose authors asked the community not to generate AI content for them. AliveSensor keeps no
    /// memories at all for anyone outside this list, so an opted-out character never appears in a prompt —
    /// not as a witness, and not as someone another villager was talking about.
    ///
    /// Refreshed once per day; null means AliveNpcs could not answer, and then nobody is filtered out.
    /// </summary>
    public IReadOnlySet<string>? EligibleNpcs => _eligible;

    private HashSet<string>? _eligible;

    /// <summary>Re-read the eligible villagers from AliveNpcs. Cheap, but not something to do every tick.</summary>
    public void RefreshEligibleNpcs()
    {
        if (Api is null)
            return;
        try
        {
            IEnumerable<string>? names = Api.GetAvailableNpcNames();
            if (names is null)
                return;

            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0)
            {
                // An empty list this early usually means "no save loaded yet", not "nobody is allowed".
                _log.Debug("AliveNpcs", "GetAvailableNpcNames returned nothing; not filtering witnesses this time.");
                return;
            }

            int before = _eligible?.Count ?? -1;
            _eligible = set;
            if (before != set.Count)
                _log.Debug("AliveNpcs", $"{set.Count} villager(s) eligible for AI content; anyone else is ignored entirely.");
        }
        catch (Exception ex)
        {
            _log.Error("Reading the eligible NPC list from AliveNpcs failed", ex);
        }
    }

    /// <summary>Whether AliveSensor may record and speak about this villager at all.</summary>
    public bool IsEligible(string npcName)
        => _eligible is null || _eligible.Contains(npcName);

    private bool Probe(string label, Func<bool> probe)
    {
        try
        {
            bool ok = probe();
            _log.Debug("AliveNpcs", $"Hook {label}: {(ok ? "ok" : "MISSING")}.");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Debug("AliveNpcs", $"Hook {label}: MISSING ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    // Accesses to AliveNpcs internals are isolated in non-inlined methods so a missing member
    // throws inside our try/catch instead of breaking the calling method at JIT time.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeCurrentNpc()
    {
        _ = DialoguePatches.CurrentNpc;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ReadCurrentNpcName() => DialoguePatches.CurrentNpc?.Name;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeRelationshipPairs() => NpcPersonalities.RelationshipPairs is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<(string Npc1, string Npc2, string Relationship)> ReadRelationshipPairs()
        => NpcPersonalities.RelationshipPairs.ToArray();
}
