# Meters reference

A meter is one feeling a villager can build up about the farmer. Meters are declared in `assets/meters.json`,
separately from events, because they are shared: any event type can feed any meter by id, and adding a fifth
feeling is a JSON edit rather than a code change.

## The four shipped meters

| Id | Label | Levels | Half-life | Bubble | Priority | Max/day | Grudge |
|---|---|---|---|---|---|---|---|
| `confront` | confrontation | 15 / 30 / 50 | 3h | angry | 1.2 | 3 | 0.4 |
| `humor` | amusement | 12 / 26 / 45 | 1.5h | note | 0.9 | 3 | 0 |
| `tenderness` | fondness | 15 / 32 / 55 | 6h | happy | 0.8 | 2 | 0.2 |
| `love` | romantic feeling | 20 / 42 / 70 | 12h | heart | 1.0 | 1 | 0.5 |

The numbers say a lot about the intended character of each. Amusement has the lowest bar and the fastest fade:
easy to trigger, gone within the afternoon, and never carried into tomorrow. Romantic feeling is the hardest to
reach, lasts all day, half of it survives the night, and it may only fire once a day.

## Fields

```json
{
  "Format": 1,
  "Meters": {
    "confront": {
      "Label": "confrontation",
      "Thresholds": [ 15, 30, 50 ],
      "HalfLifeHours": 3,
      "Emote": "angry",
      "Delivery": "walkUp",
      "Priority": 1.2,
      "MinCertainty": 4,
      "MaxPerNpcPerDay": 3,
      "GrudgeCarryover": 0.4,
      "Header": "...",
      "Opening": "...",
      "Tones": [ "...", "...", "..." ],
      "Closing": "..."
    }
  }
}
```

| Field | Type | Default | Meaning |
|---|---|---|---|
| `Label` | string | — | Human name for logs and the config menu |
| `Thresholds` | array | `[15,30,50]` | The levels this feeling passes through. Each fires once per villager per day. Must climb. |
| `HalfLifeHours` | number | `3` | In-game hours after which the meter has drained by half |
| `Emote` | string | `exclamation` | The bubble shown while they wait to speak. See the list below. |
| `Delivery` | string | `walkUp` | `walkUp` — they come over and open a conversation. `aside` — no emote, no interruption; it only colours their next normal conversation. |
| `Priority` | number | `1` | Weight of this feeling when several villagers want to speak at once |
| `MinCertainty` | 1–5 | `4` | How sure a witness must be before this feeling makes them speak up. Below it, the feeling still fills and becomes an aside. |
| `MaxPerNpcPerDay` | int | `3` | How many of this meter's levels one villager can act on per day |
| `GrudgeCarryover` | number | `0` | Fraction of the meter that survives the night after its strongest level fires. `0` means forget by morning. |
| `Header` | template | — | The line that opens the block handed to the AI |
| `Opening` | template | — | Sentence introducing the memories, before the list |
| `Tones` | array | — | How the villager feels at each level, from first nudge to strongest. Used as `{tone}` in `Closing`. Needs at least as many entries as there are thresholds. |
| `Closing` | template | — | The instruction after the list. `{tone}` becomes the level's tone. |

A meters file is a normal catalog file, so you can declare meters from `event-types/*.json` too — the loader
reads a `Meters` section wherever it finds one.

### Emotes

The bubbles an NPC can actually show: `can`, `question`, `angry`, `exclamation`, `heart`, `sleep`, `sad`,
`happy`, `x`, `pause`, `game`, `note`, `blush`.

The player's emote wheel lists more, but most of those are body animations that reuse these same bubbles — an
NPC only gets the bubble, so this is the whole palette. There is no laugh bubble, which is why amusement uses
`note`.

## How a meter fills

Every witnessed memory contributes `score × weight`, where the score is the memory's own (see
[how-it-works.md](how-it-works.md)) and the weight comes from the event type and its rules. Contributions are
kept individually with a timestamp and decayed on the meter's half-life, so a reading is always "how strongly
do they feel about all of it, right now".

A memory that grows — you drank a third beer in front of the same person — adds only the difference, so
repetition raises one memory's contribution instead of stacking duplicates.

## One feeling per event type per day

If an event type feeds more than one meter, the **first meter to reach level 1** for that villager locks the
event type for the rest of the day. Other meters stop receiving anything from that type for that person.

Without this, a villager who watched you drink could reach level 1 amusement in the morning and level 1 anger
in the afternoon from the same beers, then walk over twice about the same thing with two different faces. The
lock means the first reaction sets the tone: if they laughed at it, it stays funny today.

The lock is per event type, not per event, and resets every morning.

## Levels, cooldowns and caps

- Passing a level makes them show the emote and join the queue.
- `MaxPerNpcPerDay` limits how many levels of that meter one villager can act on per day.
- A global cooldown (`Confront.CooldownMinutes`, default 60 in-game minutes) sits between any two walk-ups.
- A villager who reached a level but never got close to you gives up after `Confront.PendingExpiresMinutes`
  and keeps it as an aside.

## Arbitration: who gets the turn

Only one conversation can happen at a time. Among everyone ready:

```
score = proximity × intensity × priority
```

- **Proximity** is 1 next to the farmer, falling with distance, with a floor so someone relevant is never
  ruled out entirely.
- **Intensity** is the reading as a multiple of the level it passed: 1.0 is just over the line, 2.0 is twice
  what it took. Normalising this way lets meters with very different scales compete fairly.
- **Priority** is the meter's own weight.

Ties break by whoever is absolutely closest.

A single event can only send `Confront.MaxReactionsPerEvent` villagers over (default 3), because each is an AI
call. Everyone past the cap keeps their feeling as an aside for the next time you talk to them — free, and it
still gets said.

## Asides

An aside is the cheap channel: no emote, no interruption, no extra AI call. The content is merged into the
villager's next normal conversation prompt. A feeling becomes an aside when:

- the villager is not sure enough to speak up (`MinCertainty`),
- they were past the per-event reaction cap,
- they waited too long and gave up,
- or the meter is declared `"Delivery": "aside"` and never interrupts at all.

## Grudges

When the strongest level fires and `GrudgeCarryover` is above zero, that fraction of the reading is seeded into
the next day's meter. Anger carries 40% into tomorrow; amusement carries nothing. This is how a serious
incident stays in the air for a day or two without permanently poisoning anything.

## Inspecting it

`as_saturation` prints every villager's meters, their current reading, which levels they have acted on, what
is queued, and why anyone who is not speaking is blocked. `as_saturation <NPC>` narrows it to one.
