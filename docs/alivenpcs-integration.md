# AliveNpcs integration

AliveSensor is useless on its own — it exists to put true things into AliveNpcs' prompts. That makes the
coupling between the two mods the most important engineering decision in the project, and the one most likely
to break on an update.

This document records exactly what AliveSensor touches, which parts are supported and which are not, and what
would have to exist in the public API for the unsupported parts to go away. It is written against the AliveNpcs
modder guide, and every claim about the API here was verified by reading the type metadata of
`AliveNpcs.dll` 1.6.1 rather than inferred from behaviour.

## Three tiers of coupling

| Tier | What it is | Risk |
|---|---|---|
| **1. Public API** | `IAliveNpcsApi`, `IAliveNpcsExperimentalContentApi` via `GetApi` | Safe. Versioned, documented, duck-typed by SMAPI. |
| **2. Public statics outside the API** | `DialoguePatches.CurrentNpc`, `NpcPersonalities.RelationshipPairs` | Compiles, but is not a supported surface. May change without notice. |
| **3. Internals** | Harmony patches and private-field reflection | Fragile by definition. Each one exists because tier 1 has no equivalent. |

Everything in tiers 2 and 3 is probed at startup and degrades to a logged warning instead of a crash. `as_status`
prints the live result of every probe, so a player on a future AliveNpcs version can see at a glance which
hooks still work.

## Tier 1: the supported API

| Member | Where | Why |
|---|---|---|
| `RegisterDynamicPromptBlock` | `AliveNpcsBridge.RegisterPromptBlock` | The delivery mechanism for everything. Two blocks: `dialogue` (what this villager witnessed) and `gossip` (the day's events for the nightly analysis). |
| `InjectGossip` | `GossipReinforcer` | Pushes the day's most notable events into the village gossip pool. |
| `IsNpcDisabled` | `FeelingEngine.DayBlocker` | A villager the player switched off never walks up. |
| `GetAvailableNpcNames` | `AliveNpcsBridge.RefreshEligibleNpcs` | The eligible cast. See below. |
| `GetCapabilities` | `AliveNpcsBridge.ReadCapabilities` | Feature detection instead of version comparison. |

AliveSensor deliberately does **not** use `GenerateOutputAsync`. It never makes an AI call of its own — it only
adds context to calls AliveNpcs was already going to make. That is the whole design: no extra cost to the
player, no second AI configuration, and no way for AliveSensor to make a villager say something AliveNpcs did
not decide to say.

### Respecting the opt-out list

`GetAvailableNpcNames()` returns the villagers AliveNpcs is willing to write for: everyone the player has not
switched off, minus the characters whose authors asked the community not to generate AI content for them.

AliveSensor applies this at the earliest possible point — witness resolution. A villager outside the list is
not recorded as a witness at all, which means they cannot appear later as a line in someone's prompt, as a
name in the night's gossip, or as a person another villager was talking about. Filtering at delivery time
would have been easier and would have leaked: an opted-out character could still have been *described* in
someone else's memory.

The list is refreshed on `DayStarted`, because it depends on a loaded save. If AliveNpcs cannot answer, nobody
is filtered — failing open keeps a broken lookup from silently deleting the whole feature.

## Conformance with the modder guide

| Rule | Status |
|---|---|
| Declare the dependency in `manifest.json` | Yes — `Lucas.AliveNpcs`, minimum 1.6.1, required |
| Get handles in `GameLaunched`, never the constructor | Yes — `ModEntry.OnGameLaunched` |
| One `GetApi` call per interface | Yes — two separate calls |
| Null-check every handle | Yes — every call site guards or uses `?.` |
| Treat `InjectGossip` returning `false` as the player's choice, log at Debug | Yes — logged at Debug, never Warn |
| Call `InjectGossip` during the day | Yes — at `DayEnding` with `[EventPriority(EventPriority.High)]`, so it lands before AliveNpcs' own `DayEnding` handler and makes it into that night's analysis |
| Dispose what returns `IDisposable` | Yes — handles collected in the bridge, disposed from `ModEntry.Dispose(bool)` |
| Detect features, do not compare versions | Yes — `GetCapabilities()`; the version is logged for diagnostics only |
| Respect the opt-out list | Yes — see above |
| Keep the prompt budget small | Yes — 1800 characters, and providers return `null` rather than padding |
| Never block the game thread on AI | Not applicable — AliveSensor makes no AI calls |
| Multiplayer runs on the host | Sensors are disabled for farmhands; single player is the supported configuration |

## Tier 2 and 3: what AliveSensor reaches into, and why

Each of these is a place where AliveSensor needs something the public API does not expose. They are listed with
what was verified in the DLL, so the gap can be judged rather than taken on faith.

### 1. Which NPC a prompt is being built for

**What is patched.** A read-only Harmony prefix and finalizer on
`AliveNpcs.Services.ExperimentalContent.ExperimentalPromptComposer.BuildPromptBlock(string, string, bool)`,
storing the NPC name in a `[ThreadStatic]` slot for the duration of the call
(`Integration/PromptNpcScope.cs`).

**Why.** A dynamic prompt block provider is handed an `ExperimentalRuntimeHookContext`, whose full set of
properties is:

```
ActiveModeId, SaveId, PlayerName, Day, Season, Year, TimeOfDay, NewTime
```

There is no NPC name. A provider therefore cannot know who it is writing for, which makes
`RegisterDynamicPromptBlock` usable only for text that is identical for the entire village. AliveSensor's whole
purpose is per-villager text.

`DialoguePatches.CurrentNpc` (tier 2) is used as a fallback, but it is only set *after* the opening line is
generated, so it is empty for exactly the prompt that matters most — the one that opens a conversation.

**What would remove it.** An `NpcName` on `ExperimentalRuntimeHookContext`, populated when the block is being
composed for a specific villager and left null otherwise. This is the single highest-value change for any mod
that wants to contribute per-character context.

### 2. Knowing the player talked to a villager

**What is patched.** A postfix on `ResponseFlowController.ApplyReaction` (`Sensors/ActionSensors.cs`), used by
the `talk` sensor — so that villagers who overhear a conversation remember it happened.

**Why.** Neither interface exposes any conversation event. The full method list of
`IAliveNpcsExperimentalContentApi` is registration, story arcs, lore, availability providers, a mode-scoped
runtime hook with `OnDayStarted`/`OnDayEnding`, and queries. `RecordGossipInteraction` writes an interaction;
nothing notifies a listener that one occurred.

**What would remove it.** A conversation event on the mode-scoped runtime hook — something like
`OnConversationEnded(npcName, playerLine, npcLine)` — or a registerable observer alongside
`RecordGossipInteraction`.

### 3. Making a villager open a conversation

**What is reflected.** The private static `DialoguePatches._dialogueManager` and `_generatingDialogue` fields,
and `DialogueManager.InvalidateNpcDialogueCache(string)` invoked on the former
(`Integration/AliveNpcsBridge.cs`).

**Why.** This is how a saturated feeling becomes a walk-up: AliveSensor clears the cached opening line so the
next `checkAction` generates a fresh one that includes the reason block, then triggers the interaction. Without
clearing the cache the villager approaches and says whatever was already cached, which is exactly the generic
line the feature exists to replace.

Nothing in the public API invalidates a cached line, and nothing lets a mod ask AliveNpcs to start a
conversation.

**What would remove it.** Either `InvalidateNpcDialogueCache(string npcName)` on the public API, or a higher
level `RequestConversation(string npcName, string reasonBlock)` that does the whole thing. The second would be
better: it would let AliveNpcs decide how a mod-initiated conversation should behave rather than having mods
reconstruct it.

### 4. The NPC relationship graph

**What is read.** `NpcPersonalities.RelationshipPairs` (tier 2 — public static, but not part of the API),
consumed by `World/WorldIndex.cs` to know who is family, which feeds the `relative` involvement multiplier in
scoring.

**Why.** No public member exposes the relationship data AliveNpcs maintains. `GetNpcContextSnapshot` returns
display name, location, hearts and arc ids; `NpcMetadataRegistration.KnownRelationships` is for *writing*
relationships, not reading them.

**Honest note.** This is the weakest of the four justifications: family relationships could be derived from the
vanilla `Data/Characters` data without involving AliveNpcs at all. Reading it from AliveNpcs keeps the two mods
agreeing on who counts as family, including for modded NPCs, but the dependency is a convenience rather than a
necessity, and it is the first one that should be dropped if it causes trouble.

## The assembly reference trade-off

The modder guide recommends copying the API interfaces into your own project, so SMAPI can duck-type the proxy
and a signature change in AliveNpcs degrades to `GetApi` returning `null`.

AliveSensor instead takes a hard assembly reference to `AliveNpcs.dll`, because tiers 2 and 3 need compile-time
access to types that are not part of the API. The cost is real: a binary-incompatible AliveNpcs update could
fail our assembly at load time rather than degrading gracefully.

This is a deliberate, reversible trade. If the four gaps above were closed, AliveSensor could drop the hard
reference, copy the interfaces as the guide recommends, and become resilient to AliveNpcs updates in the way
every other companion mod is.

## Not yet used

`GetCapabilities().GreetingPromptTarget` reports whether this build accepts a prompt block targeting the
first-meeting greeting, which is injected *after* the "you do not know this person" framing specifically so it
can be overridden.

That is a natural fit here: a villager who watched the farmer set off eight bombs in Pierre's shop should not
greet them as a stranger. The capability is already read and exposed as `AliveNpcsBridge.HasGreetingTarget`;
the block is not registered yet.

## Summary of requests to AliveNpcs

In order of value to third-party mods generally, not just this one:

1. **`NpcName` on `ExperimentalRuntimeHookContext`.** Without it, dynamic prompt blocks can only say things
   that are true of the whole village.
2. **A conversation-occurred hook.** Mods can write interactions but cannot observe them.
3. **`InvalidateNpcDialogueCache`, or a `RequestConversation` entry point.** Currently the only way for a mod
   to make a villager speak first is to reach into private state.
4. **Read access to the relationship graph.** Lowest priority; a workaround exists outside AliveNpcs.
