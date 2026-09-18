# Event catalog reference

Everything AliveSensor knows about a kind of event is declared in JSON. This is the reference for
`assets/event-types.json` and any file you add yourself.

## File shape

```json
{
  "Format": 1,
  "Types": {
    "<id>": { /* an event type */ }
  },
  "GlobalRules": [ /* rules applied to every type — see rules.md */ ]
}
```

`Format` is a stamp bumped only if the schema changes in a way old files cannot survive. Both `Types` and
`GlobalRules` are optional, so a file may contain only global rules.

## Where files are loaded from

| Order | File | Purpose |
|---|---|---|
| 1 | `assets/event-types.json` | The catalog shipped with the mod |
| 2 | `event-types/*.json` | Your files, loaded alphabetically after the shipped catalog |

A type in a later file with the same id **replaces** the earlier one entirely (the log notes it). Global rules
**accumulate** across all files. Meters can also be declared from these files — see [meters.md](meters.md).

Loading never throws. A malformed file is reported in the log and the affected types fall back to neutral
defaults, so a bad edit cannot take a save's memories down with it. Check the log for `[Catalog]` lines after
editing; it prints what loaded and warns about unknown meters, bad tokens and empty phrase sets.

Edit with the game closed. The game rewrites `config.json` on exit, and a running game holds the files it read
at startup.

## Event type

```json
"room_locked": {
  "Grade": 4,
  "HalfLifeDays": 3,
  "RangeTiles": 8,
  "MinCertainty": 1,
  "Perception": { "ThroughWalls": false, "WakesSleepers": false, "HeardFloor": 0 },
  "Meters": { "confront": { "Weight": 1 } },
  "Rules": [ ],
  "VariantBy": null,
  "Variants": { },
  "Phrases": { },
  "Tokens": { }
}
```

| Field | Type | Default | Meaning |
|---|---|---|---|
| `Grade` | number | `1` | How much this kind of event matters at all. The final multiplier on a memory's score, and what every meter weight is applied to. |
| `HalfLifeDays` | number | `3` | Days after which the memory is worth half as much. Governs how fast it is forgotten, not how fast the meter drains. |
| `RangeTiles` | int | `8` | How far a witness can be. `-1` means the whole map. |
| `MinCertainty` | 1–5 | `1` | A floor nobody who witnessed it falls below, however poor the view. |
| `Perception` | object | — | See below. |
| `Meters` | object | `{}` | What this type is worth to each feeling. See below. |
| `Rules` | array | `[]` | Per-witness exceptions. See [rules.md](rules.md). |
| `VariantBy` | string | `null` | A token whose value selects a phrase set, e.g. `"p:action"` for enter/leave. |
| `Variants` | object | `{}` | Phrase sets by that token's value. Missing slots fall back to `Phrases`. |
| `Phrases` | object | `{}` | The wording. See [phrases.md](phrases.md). |
| `Tokens` | object | `{}` | Computed tokens this type defines. See [phrases.md](phrases.md). |

A type with no `Meters` and no rules that add any (`talk`, `purchase`, `place`, `map`) is still recorded and
still reaches conversation prompts — it simply never makes anyone fill a meter or walk over.

### Perception

How well this kind of event can be perceived, beyond plain distance.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `ThroughWalls` | bool | `false` | Bedroom walls do not hide it. True for explosions, false for footsteps. |
| `WakesSleepers` | bool | `false` | It reaches villagers asleep in their own room. |
| `HeardFloor` | number | `0` | A visibility floor for everyone on the map — in effect, how loud it is. `0` means no floor. |

### Meters

What the type is worth to each feeling, and for whom.

```json
"Meters": {
  "tenderness": { "Weight": 2, "Roles": [ "target" ] },
  "confront":   { "Weight": 1, "RequiresPayload": "alcohol", "OwnerTrustExempt": true }
}
```

| Field | Type | Default | Meaning |
|---|---|---|---|
| `Weight` | number | `1` | Points toward that meter, before the memory's own score scales it. `0` means never. |
| `Roles` | array | `[]` | Limit it to certain parts: `target`, `vendor`, `relative`, `bystander`. Empty means everyone. |
| `RequiresPayload` | string | `null` | Only counts when the payload has this key — `"alcohol"`, so a sandwich bothers nobody. |
| `OwnerTrustExempt` | bool | `false` | Does not count for an owner who already trusts the farmer. The game itself opens their bedroom door at two hearts, so being offended by walking through it makes no sense. |

The meter id must exist in `meters.json` (or in a meters file of your own) or the catalog warns and ignores it.

## The shipped catalog

| Type | Grade | Meters | Notes |
|---|---|---|---|
| `bomb` | 8 | confront 1, humor 0.25 | Pierces walls, wakes sleepers, heard across the map |
| `room` | 7 | confront 1 | Entering someone's bedroom |
| `sleep_outside` | 6 | confront 0.6, tenderness 0.5, humor 0.3 | Passing out — worrying, endearing and a bit funny at once |
| `trash` | 5 | confront 1 | Going through a trash can |
| `gift` | 4 | tenderness 2 (target) | Two rules: a hated or disliked gift earns no warmth |
| `room_locked` | 4 | confront 1 | Turned away at a locked bedroom door |
| `room_unlocked` | 3 | confront 0.5 | Opening a door you have been trusted with |
| `consume` | 2.5 | confront 1, humor 1 (alcohol only) | Three rules: Shane, Gus and Penny |
| `talk` | — | none | Recorded only |
| `purchase` | — | none | Recorded only |
| `place` / `map` | — | none | Recorded only |
| `npc_move` | — | none | Off by default |

`as_types` prints this table from whatever is actually loaded, including your own files.

## Payload

The payload is the sensor's responsibility, not the catalog's. Sensors write raw facts and the JSON decides
what they mean and how they are worded:

| Type | Payload keys it writes |
|---|---|
| `consume` | `item`, `itemId`, `kind` (`alcohol`/`drink`/`food`), `drink`, `alcohol`, `count` |
| `gift` | `item`, `taste` (`loved`/`liked`/`disliked`/`hated`), `partner`, `birthday` |
| `room` / `room_locked` | `owners`, `attempt`, `stayMinutes` (only when 10 or more) |
| `purchase` | `items`, `shop`, `shopDisplay` |
| `place` / `map` | `action` (`enter`/`leave`), `from`, `to`, `place`, `placeDisplay`, `owners` |
| `bomb` | `count`, `radius` |

Rules match on these with `Payload`, phrases print them with `{p:key}`, and a rule can be written against any
key a sensor writes without the sensor knowing the rule exists.

Some keys are written by the engine itself: when a `NearbyAge` condition matches, it records
`nearby<Age>Count` and `nearby<Age>Names` so a phrase can say exactly how many children were standing there.
