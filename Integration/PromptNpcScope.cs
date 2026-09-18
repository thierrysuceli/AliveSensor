using System;
using AliveSensor.Core;
using HarmonyLib;

namespace AliveSensor.Integration;

/// <summary>
/// Knows which NPC a prompt is being built for.
///
/// AliveNpcs' dynamic prompt providers get no NPC name, and DialoguePatches.CurrentNpc is only set
/// after the opening line is generated (verified in the first in-game test). But
/// ExperimentalPromptComposer.BuildPromptBlock(npcName, ...) receives the name and calls the dynamic
/// block providers synchronously, on the same thread. A read-only prefix stores the name in a
/// [ThreadStatic] slot for the duration of that call; the finalizer restores the previous value.
/// </summary>
internal static class PromptNpcScope
{
    public const string ComposerType = "AliveNpcs.Services.ExperimentalContent.ExperimentalPromptComposer";
    public const string ComposerMethod = "BuildPromptBlock";

    [ThreadStatic]
    private static string? _current;

    private static Log _log = null!;

    /// <summary>NPC whose dialogue prompt is being composed on this thread, or null.</summary>
    public static string? Current => _current;

    public static bool Apply(Harmony harmony, Log log)
    {
        _log = log;
        var target = AccessTools.Method(AccessTools.TypeByName(ComposerType), ComposerMethod, new[] { typeof(string), typeof(string), typeof(bool) });
        if (target is null)
        {
            log.Debug("Harmony", $"{ComposerType}.{ComposerMethod} not found; opening prompts will fall back to DialoguePatches.CurrentNpc.");
            return false;
        }

        harmony.Patch(
            original: target,
            prefix: new HarmonyMethod(typeof(PromptNpcScope), nameof(Prefix)),
            finalizer: new HarmonyMethod(typeof(PromptNpcScope), nameof(Finalizer)));
        log.Debug("Harmony", $"Patched {ComposerType}.{ComposerMethod} (read-only NPC scope).");
        return true;
    }

    private static void Prefix(string npcName, out string? __state)
    {
        __state = _current;
        _current = npcName;
        _log.Verbose("Harmony", $"Prompt scope enter: {npcName}");
    }

    private static Exception? Finalizer(Exception? __exception, string? __state)
    {
        _current = __state;
        return __exception;
    }
}
