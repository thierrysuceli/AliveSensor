# AliveSensor

A companion mod for [AliveNpcs][alivenpcs] that gives villagers a
memory of what they actually saw you do.

AliveNpcs already lets NPCs hold a real conversation. AliveSensor gives them something true to talk about. It
watches the world, records what each villager was in a position to witness, and feeds that into their
conversation prompt. Nothing is invented by the AI: if Marnie brings up the door you rattled, it is because the
mod recorded you rattling it, from where she was standing, in light she could see by.

It also decides when a villager cares enough to come find you. Set off bombs in Pierre's shop and he walks over
with an exclamation mark above his head and opens the conversation already knowing why.

## What villagers notice

| Event | Recorded when |
|---|---|
| `bomb` | A bomb goes off near them |
| `room` | You walk into someone's bedroom |
| `room_locked` | A locked bedroom door turns you away |
| `room_unlocked` | You open a bedroom door you have been trusted with |
| `trash` | You go through a trash can |
| `gift` | You hand someone a gift (and whether they loved or hated it) |
| `consume` | You eat or drink in front of them |
| `sleep_outside` | You pass out from exhaustion |
| `purchase` | You buy something from a shopkeeper |
| `talk` | You talk to another villager in front of them |
| `place` / `map` | You enter or leave somewhere |

Every one of those is declared in `assets/event-types.json`, not in code. Adding a twelfth is a JSON edit, and
so is changing how any of these behave.

## How sure they are

Being in the room is not the same as seeing clearly. Each witness gets a certainty from 1 to 5, worked out from
distance, weather, indoor light, time of day, walls, and whether they were facing your way. That certainty
changes the words in the prompt:

- **5** — "You clearly saw the farmer set off 8 bombs."
- **3** — "You think you saw the farmer at Shane's bedroom door."
- **1** — "You are not sure, but it looked like someone was going through your trash."

A villager asleep in their own bedroom does not see you walk past it. An explosion still wakes them.

## Four feelings

What a villager witnessed feeds one or more *meters*, declared in `assets/meters.json`:

| Meter | What it is | Levels | Fades by half every | Bubble |
|---|---|---|---|---|
| `confront` | anger, wanting an explanation | 15 / 30 / 50 | 3h | angry |
| `humor` | finding it funny | 12 / 26 / 45 | 1.5h | note |
| `tenderness` | warmth, friendship | 15 / 32 / 55 | 6h | happy |
| `love` | romantic feeling | 20 / 42 / 70 | 12h | heart |

The same event can feed several at once, and differently per person. Drinking in the saloon amuses Shane,
pleases Gus enough that he buys the next round, and — if there is a child within eight tiles — genuinely
bothers Penny. All three of those are rules in the shipped catalog, written in JSON.

When a meter passes a level the villager shows their bubble and walks over to open a normal AliveNpcs
conversation, already knowing what it is about. When several are ready at once, the turn goes to whoever is
closest, feels it most strongly, and holds the weightier feeling; the rest keep it for the next time you talk
to them, which costs no extra AI call.

AliveSensor never changes friendship points. Tone reaches the NPC only as text in the prompt; AliveNpcs decides
what that does to the relationship.

## Requirements

- Stardew Valley 1.6.15
- SMAPI 4.0.0 or later
- [AliveNpcs][alivenpcs] 1.6.1 or later
- [SpaceCore][spacecore] 1.28.0 or later
- [Generic Mod Config Menu][gmcm] (optional, for the settings UI)

Single player only for now. AliveSensor does not modify any AliveNpcs file; it lives in its own folder and
talks to AliveNpcs through its public API.

## Install

1. Install SMAPI, AliveNpcs and SpaceCore.
2. Unzip AliveSensor into `Stardew Valley/Mods`.
3. Run the game once. Settings appear in Generic Mod Config Menu, and memories are stored per save under
   `Mods/AliveSensor/Data/<SaveFolderName>/`.

## Extending it

The whole pipeline — perception, grading, feelings, exceptions, wording — reads from JSON. A file dropped in
`Mods/AliveSensor/event-types/` is loaded after the shipped catalog and can add new event types, replace
existing ones, or add rules that apply to every event in the game.

```json
{
  "Format": 1,
  "GlobalRules": [
    {
      "Name": "Weekend off",
      "When": { "Weekday": [ "Saturday", "Sunday" ], "NotWitness": [ "Penny", "Shane" ] },
      "Multiply": { "confront": 0.5, "humor": 0.5, "tenderness": 0.5, "love": 0.5 }
    }
  ]
}
```

Documentation:

- [How it works](docs/how-it-works.md) — the pipeline from a bomb going off to a line in a prompt
- [Event catalog reference](docs/event-catalog.md) — every field of `event-types.json`
- [Rules reference](docs/rules.md) — conditions, multipliers, rewards, global rules
- [Meters reference](docs/meters.md) — feelings, levels, saturation, arbitration
- [Phrase templates](docs/phrases.md) — the template language and every token
- [Creating a new event](docs/creating-events.md) — worked example, JSON only and with a new sensor
- [Configuration and console commands](docs/configuration.md)
- [AliveNpcs integration](docs/alivenpcs-integration.md) — what AliveSensor uses, what it has to reach into, and why

## Console commands

`as_help` lists them all. The ones worth knowing:

| Command | What it shows |
|---|---|
| `as_list <NPC>` | What that villager remembers, worded as the AI will read it |
| `as_prompt <NPC>` | The exact block injected into their next conversation |
| `as_saturation [NPC]` | How close each villager is to walking over, and why |
| `as_score <NPC>` | Score breakdown of every memory they hold |
| `as_where` | Your map, tile, light, weather, and who can see you from where |
| `as_witness <type>` | Dry run: who would witness this kind of event right here |
| `as_simulate <type>` | Records a test event at your position |

## Building from source

Requires the .NET 6 SDK. `dotnet build -c Release` compiles and copies the mod into your `Mods` folder via
[ModBuildConfig](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md). If your game
is not in a standard location (Xbox Game Pass, for example), set `GamePath` once in
`%USERPROFILE%\stardewvalley.targets` — the project file explains how.

The game must be closed to rebuild once the mod has been loaded, or the DLL copy fails on a file lock.

## Status

Version 0.1.0. Working and playable, but young: the catalog will grow, and the balance of what villagers care
about is still being tuned. Bug reports and event-type contributions are welcome.

## Credits

AliveNpcs by Lucas Gatica. SpaceCore by spacechase0. AliveSensor is an independent companion mod and is not
affiliated with either.

[alivenpcs]: https://www.nexusmods.com/stardewvalley/mods/
[spacecore]: https://www.nexusmods.com/stardewvalley/mods/1348
[gmcm]: https://www.nexusmods.com/stardewvalley/mods/5098
