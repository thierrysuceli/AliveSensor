# Creating a new event

Two paths, depending on whether the thing you want villagers to notice is already being recorded.

- **JSON only** — you want existing events to mean something different. No compiler, no restart of anything
  but the game.
- **A new sensor** — nothing records the thing yet, so it needs a small piece of C#. Everything after that
  point is still JSON.

## Path 1: JSON only

Create `Mods/AliveSensor/event-types/my-pack.json`. Anything in that folder is loaded after the shipped
catalog.

### Give an existing event a new meaning for one character

Say Emily should find your late-night wandering charming rather than suspicious. `room` currently feeds
`confront` for everyone. Add a rule without rewriting the type:

```json
{
  "Format": 1,
  "Types": {
    "room": {
      "Grade": 7,
      "HalfLifeDays": 3,
      "RangeTiles": 8,
      "Meters": { "confront": { "Weight": 1 } },
      "Rules": [
        {
          "Name": "Emily finds it charming",
          "When": { "Witness": [ "Emily" ] },
          "Multiply": { "confront": 0 },
          "Set": { "tenderness": 1.5 },
          "Phrases": { "Full": "{actor} wandering into {owners} room, curious about everything" }
        }
      ],
      "Phrases": {
        "Full": "{actor} walk into {owners} bedroom[, {nearPhrase}]",
        "Reduced": "{actor} somewhere they shouldn't be",
        "Past": "{actor} walked into {owners} bedroom"
      }
    }
  }
}
```

Redefining a type replaces it whole, so copy the fields you are not changing. `as_types` prints the current
values to copy from.

### Change something about every event at once

`GlobalRules` are checked for every witness of every type:

```json
{
  "Format": 1,
  "GlobalRules": [
    {
      "Name": "Storms put everyone on edge",
      "When": { "Weather": [ "Storm" ] },
      "Multiply": { "confront": 1.3 }
    }
  ]
}
```

### Add a whole new meter

Meters can be declared from your file too:

```json
{
  "Format": 1,
  "Meters": {
    "envy": {
      "Label": "envy",
      "Thresholds": [ 18, 38, 60 ],
      "HalfLifeHours": 8,
      "Emote": "sad",
      "Priority": 0.9,
      "MinCertainty": 4,
      "MaxPerNpcPerDay": 1,
      "GrudgeCarryover": 0.3,
      "Header": "[Why you are talking to the farmer right now]",
      "Opening": "You came over to {farmer} because of what you keep seeing:",
      "Tones": [
        "quietly, not quite admitting it bothers you",
        "openly comparing yourself to them",
        "unable to keep the resentment out of your voice"
      ],
      "Closing": "Open by bringing this up right away, {tone}. Do not greet them as if nothing happened."
    }
  }
}
```

Any event type can then give it weight. See [meters.md](meters.md) for every field.

## Path 2: A new sensor

The sensor is the only C# a new kind of event needs. Its whole job is: detect, describe, record.

### 1. Record the event

```csharp
var draft = new EventDraft("marriage_rejected", "Bouquet", location, npc.TilePoint)
{
    Target = npc.Name
};
draft.Payload["reason"] = "not_enough_hearts";
draft.Direct.Add(npc.Name);          // they were certainly aware of it
_mod.Recorder.Record(draft);
```

`EventDraft` is the entire contract:

| Field | Purpose |
|---|---|
| `Type` | The catalog id. Any string; it does not have to exist yet. |
| `Source` | Sensor name, for the log |
| `Location`, `Tile` | Where it happened — the origin for distance and witness checks |
| `Actor` | Defaults to the player |
| `Target` | The person it was done to, if any |
| `Involved` | Other participants |
| `Payload` | Raw facts as strings. Rules match on these, phrases print them. |
| `Aggravators` | Named grade multipliers from the config |
| `Direct` | Villagers who are certain (level 5) regardless of distance, within `DirectRangeTiles` |
| `Exclude` | Villagers who can never be witnesses of this — usually the actor |
| `ExtraScopes` | Other maps to look for witnesses on, e.g. the street outside a door |
| `MentionedNpcs` | Names appearing in the payload, so their display names are captured for rendering |
| `PerWitness` | A callback to adjust each witness after they are resolved (bonus, counters) |

Write facts, not conclusions. `kind: "alcohol"` is a fact; "this is bad behaviour" is a conclusion and belongs
in the catalog.

### 2. Hook it up

Sensors are registered in `Sensors/SensorHub.cs`. Existing ones show the three patterns: SpaceCore events
(gifts, bombs, eating), SMAPI events (warps, menus, day start), and small Harmony patches for things neither
exposes (locked doors, passing out).

### 3. Declare it in the catalog

```json
"marriage_rejected": {
  "Grade": 5,
  "HalfLifeDays": 4,
  "RangeTiles": 10,
  "Meters": { "love": { "Weight": 2, "Roles": [ "target" ] } },
  "Rules": [
    {
      "Name": "already seeing someone",
      "When": { "Payload": { "reason": "already_dating" } },
      "Multiply": { "love": 1.5 },
      "Phrases": { "Full": "{actor} asking {target} to marry them, and {target} already seeing someone else" }
    },
    {
      "Name": "far too soon",
      "When": { "Payload": { "reason": "not_enough_hearts" }, "MaxHearts": 6 },
      "Silent": true
    }
  ],
  "Phrases": {
    "Full": "{actor} ask {target} to marry them, and get turned down[, {nearPhrase}]",
    "Reduced": "{actor} and {target} having an awkward moment",
    "Glimpse": "talking to {target}",
    "Past": "{actor} proposed to {target} and was turned down"
  }
}
```

That is the whole feature. Perception, decay, scoring, meters, arbitration, emotes, prompt injection and
gossip all work on it immediately, because none of them know what `marriage_rejected` is — they read the
catalog by id.

## Testing without waiting for the world

```
as_witness marriage_rejected    → who would witness it right here, and how sure they would be
as_simulate marriage_rejected Abigail
                                → actually record one at your position
as_list Abigail                 → the finished line, worded as the AI will read it
as_score Abigail                → why it scored what it scored
as_saturation Abigail           → how close she now is to walking over
as_prompt Abigail               → the exact text going into her next conversation
as_confront Abigail             → force her to come over now
as_forget npc Abigail           → wipe and try again
```

The log is the other half of this. `[Catalog]` lines report what loaded and everything that looked wrong;
`[Feelings]` lines show every point added to every meter, which rule shaped it, and why anyone who wanted to
speak could not.

## Things worth knowing before you start

**Leaving a meter out is a decision.** A rule only overrides what it declares. If you multiply `confront` and
say nothing about `humor`, that villager still gets the type's default amusement. Zero it explicitly if the
point is that they do not find it funny.

**The first feeling of the day wins.** If a type feeds several meters, the first one to reach level 1 locks
the type for that villager that day. Design around it rather than against it.

**Certainty gates speech, not memory.** A villager who half-saw something still remembers it and still brings
it up when relevant — they just will not march over about it unless they are sure (`MinCertainty`).

**Reactions cost money.** Every walk-up is an AI call. `MaxReactionsPerEvent` exists for that reason, and
`Silent: true` is the right answer for anything that should be remembered but is not worth interrupting for.

**Do not invent facts in phrases.** Everything in a line reaches the AI as something the villager personally
witnessed. If the payload does not contain it, do not write it.
