# Configuration and console commands

Settings live in `Mods/AliveSensor/config.json`, written on first run. With Generic Mod Config Menu installed,
all of it is editable in game from the mod list.

Edit `config.json` with the game **closed**. A running game holds its own copy in memory and writes it back on
exit, silently reverting anything changed on disk meanwhile.

Balance that belongs to content — what each event is worth, how each feeling behaves — is not here. It lives in
`assets/event-types.json` and `assets/meters.json`, because it is data, not preference. See
[event-catalog.md](event-catalog.md) and [meters.md](meters.md).

## Delivery

What reaches an NPC's conversation prompt.

| Setting | Default | Meaning |
|---|---|---|
| `NpcBlockEnabled` | `true` | Inject witnessed memories into conversations at all |
| `MaxPerNpc` | `5` | Most memories considered for one prompt |
| `MaxChars` | `1800` | Character budget for the injected block |
| `PromptLanguage` | `"en"` | Language of the injected text. The AI replies in the game's language regardless; this is the language it reads the memories in. |

## Visibility

How well villagers perceive things. These are global — per-type reach and wall-piercing belong to the catalog.

| Setting | Default | Meaning |
|---|---|---|
| `DistanceBands` | bands | Visibility factor by distance in tiles |
| `BeyondBandsFactor` | `0.25` | Factor past the last band |
| `RainFactor` / `StormFactor` / `SnowFactor` | `0.8` / `0.7` / `0.9` | Weather penalties |
| `NightOutdoorFromTime` / `NightOutdoorFactor` | `2000` / `0.7` | Outdoor darkness |
| `LateNightFromTime` / `LateNightOutdoorFactor` | `2400` / `0.55` | Deeper darkness |
| `WalkingFactor` / `BusyFactor` | `0.9` / `0.95` | Attention penalties |
| `DirectAlwaysKnows` | `true` | People directly involved are certain |
| `DirectRangeTiles` | `15` | How far that certainty reaches. Beyond it, a shopkeeper at the counter no longer automatically knows someone rattled their bedroom door across the building. |
| `NoticeRollBelow` | `0.45` | Below this visibility, whether they noticed at all is a seeded roll |
| `BedroomWalls` | `true` | Bedroom walls block sight both ways |
| `DoorwayViewTiles` | `2` | How far into a doorway someone can see |
| `AsleepInBedroomFromTime` | `2300` | From this hour, being in their own bedroom counts as asleep |

## Certainty

Where visibility becomes a level from 1 to 5.

| Setting | Default |
|---|---|
| `Level5` | `0.85` |
| `Level4` | `0.65` |
| `Level3` | `0.45` |
| `Level2` | `0.25` |
| `MinLevelByType` | per type, a floor |

## Scoring

How much a memory is worth to a villager.

| Setting | Default | Meaning |
|---|---|---|
| `MinScore` | `0.3` | Below this, a memory is not injected |
| `VictimMultiplier` | `2.0` | It happened to them |
| `RelativeMultiplier` | `1.5` | It happened to family |
| `VendorMultiplier` | `1.3` | They sold it |
| `RepeatStep` | `0.5` | Added per extra time seen |
| `RepeatMaxMultiplier` | `3` | Cap on repetition |
| `NotableScore` | `4` | Score at which a memory is marked `[stands out]` |
| `NotableMaxAgeDays` | `1` | And how fresh it must be |
| `NotableMaxPerPrompt` | `1` | How many lines may carry that mark |
| `CompactMaxGrade` | `2` | Below this grade, old memories get compacted |
| `RetentionSeasons` | `2` | How long memories are kept |
| `ForgetBelowScore` | `0.05` | Pruned when they fade below this |

## Confrontations

Global knobs for walk-ups. Per-feeling behaviour is in `meters.json`.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Villagers may walk up at all |
| `MaxReactionsPerEvent` | `3` | How many villagers one event may send over. Each is an AI call; everyone past the cap keeps it as a free aside. |
| `TriggerTiles` | `8` | They act when you are this close |
| `CooldownMinutes` | `60` | In-game minutes between any two walk-ups |
| `PendingExpiresMinutes` | `180` | They give up waiting after this and keep it as an aside |
| `ReasonLastsMinutes` | `120` | How long the reason stays attached to their conversation |
| `DelayTicks` | `60` | Pause after the emote before the conversation opens (60 ≈ 1s) |
| `MaxReasonLines` | `3` | Memories used to explain why they came over |
| `EmoteWhileWaiting` | `true` | Show the bubble while waiting for a chance to speak |
| `EmoteRangeTiles` | `12` | How far the bubble is visible |
| `EmoteRepeatSeconds` | `8` | How often it repeats |

## Nightly gossip

| Setting | Default | Meaning |
|---|---|---|
| `CycleBlockEnabled` | `true` | Add the day's events to AliveNpcs' nightly gossip prompt |
| `MaxEvents` | `12` | Most events included |
| `MaxChars` | `1800` | Character budget |
| `InjectGossip` | `true` | Also push the strongest events into AliveNpcs' gossip pool |
| `InjectGossipCount` | `3` | How many |
| `InjectGossipMinWeight` | `6` | Minimum weight to qualify |

## Other sections

`Sensors` turns individual sensors on and off. `Grade` holds the base relevance per type and the aggravator
multipliers. `Talk` covers overheard conversations and whether "keep this secret" is respected. `World` holds
room detection and trash-can settings. `Debug` controls logging.

## Console commands

| Command | What it does |
|---|---|
| `as_help` | List every command |
| `as_status` | Sensors, AliveNpcs and SpaceCore hooks, save data, settings |
| `as_debug [on\|off] [verbose]` | Toggle detailed logging (writes config) |
| `as_day [days ago]` | Events recorded that day, with witnesses and levels |
| `as_list <NPC>` | What that villager remembers, worded as the AI reads it |
| `as_score <NPC>` | Score breakdown of every memory they hold |
| `as_prompt <NPC>` | The exact block injected into their next conversation |
| `as_cycle [days ago]` | The exact block injected into that night's gossip |
| `as_saturation [NPC]` | Meters, readings, what is queued, and what is blocking whom |
| `as_confront <NPC>` | Make a villager come over as soon as you are close |
| `as_where` | Your map, tile, light, weather, landmark, and who can see you |
| `as_witness <type>` | Dry run: who would witness this here, and how sure. Records nothing. |
| `as_simulate <type> [NPC] [item]` | Records a test event at your position |
| `as_types` | Every loaded event type with grade, half-life and range |
| `as_rooms [location]` | Bedrooms discovered from door tiles |
| `as_trash` | Trash cans on this map, with owners and whether checked today |
| `as_export` | Write a readable JSON dump of this save's memories |
| `as_save` | Save memories to disk now |
| `as_forget <eventId>` | Delete one memory |
| `as_forget npc <NPC>` | Delete everything one villager remembers |
| `as_forget all confirm` | Delete everything |

## Storage

Memories are stored per save at `Mods/AliveSensor/Data/<SaveFolderName>/`, alongside AliveNpcs' own data rather
than in SMAPI's global folder. Deleting that folder resets a save's memories without touching anything else.
