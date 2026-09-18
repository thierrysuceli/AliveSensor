# Rules reference

A rule says: *when this happens, in front of that person, in these circumstances, feel something different
about it.* Rules are where a generic event becomes a character moment.

They are checked **once per witness, at the moment the event is recorded**, on the main thread. That is why a
rule can ask questions about the live world — who is standing nearby, what the weather is, how many hearts you
have with this person — and why the answer is stored on the memory rather than recomputed later.

## Shape

```json
{
  "Name": "Penny minds the children",
  "When": {
    "Witness": [ "Penny" ],
    "Payload": { "kind": "alcohol" },
    "NearbyAge": { "Age": "child", "Tiles": 8 }
  },
  "Multiply": { "confront": 6, "humor": 0 },
  "Phrases": {
    "Full": "{actor} drinking {p:item} with {p:nearbychildCount?a child|# children} right there — a bad example to be setting"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `Name` | string | Shown in the log and stored on the witness, so you can see which rule shaped a memory. Defaults to `<type>#<n>`. |
| `When` | object | Conditions. Every one written must hold; anything omitted is ignored. |
| `Multiply` | `{meter: factor}` | Multiplies the weight that meter already had. `0` removes it. |
| `Set` | `{meter: weight}` | Replaces the weight outright. Use this to give a meter weight the type never had. |
| `Phrases` | object | Wording for witnesses this rule matched, replacing the type's own. See [phrases.md](phrases.md). |
| `Silent` | bool | They keep score but never bring it up on their own. The feeling still colours later conversations. |
| `Reward` | object | Something handed over when they act on it. See below. |

A rule that does nothing at all (no multiplier, no set, no phrases, no reward, not silent) is reported as a
mistake in the log.

## Order matters

Every matching rule applies its multipliers, in the order written. Wording and rewards come from the **first**
matching rule that declares them, so put the most specific rules first.

Order of application overall:

1. The type's own `Meters` weights (filtered by `Roles` and `RequiresPayload`).
2. The type's `Rules`, in order.
3. `GlobalRules` from every loaded file, in order.

That means a global rule multiplies whatever the specific rules already decided — a weekend discount applies on
top of Shane's ×3, not instead of it.

## Conditions

| Field | Type | Example | Notes |
|---|---|---|---|
| `Witness` | list of names | `[ "Shane", "Gus" ]` | Only these villagers feel it this way |
| `NotWitness` | list of names | `[ "Penny" ]` | Everyone **except** these |
| `Role` | list | `[ "target" ]` | Their part in it: `target`, `vendor`, `relative`, `bystander` |
| `Payload` | `{key: value}` | `{ "kind": "alcohol" }` | An empty value checks only that the key exists |
| `NearbyAge` | object | `{ "Age": "child", "Tiles": 8, "Present": true }` | `Age` is `child`, `teen` or `adult`. `Present: false` requires that **nobody** of that age is around. The witness never counts as their own bystander. |
| `Weather` | list | `[ "Rain", "Storm" ]` | `Sun`, `Rain`, `Storm`, `Snow`, `Wind`, `Festival` |
| `Season` | list | `[ "winter" ]` | |
| `Weekday` | list | `[ "Friday", "Saturday" ]` | `Monday`–`Sunday`. Stardew's calendar keeps a fixed 7-day cycle, so this is stable across years. |
| `Location` | list | `[ "Saloon" ]` | Internal map name |
| `Outdoors` | bool | `true` | |
| `TimeFrom` / `TimeTo` | game clock | `1800` / `2600` | Inclusive. A single window inside one day — it does not wrap past midnight. |
| `MinHearts` / `MaxHearts` | int | `2` | Hearts between the farmer and this witness |
| `MinCertainty` | 1–5 | `4` | How sure this witness has to be for the rule to apply |
| `PlayerPartner` | bool | `true` | This witness is the farmer's own current partner — dating, engaged or married, at the moment of the event. `false` requires that they are not. Lets a rule single out "your own partner watched this" without knowing their name in advance. |

When `NearbyAge` matches with `Present: true`, the engine writes the result into the payload for you:
`{p:nearby<Age>Count}` and `{p:nearby<Age>Names}` — so `{p:nearbychildCount?a child|# children}` renders as
"a child" or "3 children".

## Rewards

```json
"Reward": { "Item": "(O)346", "CountByTier": [ 1, 2, 3 ], "Meter": "humor" }
```

| Field | Type | Meaning |
|---|---|---|
| `Item` | string | Qualified item id, e.g. `(O)346` for beer |
| `CountByTier` | array | How many, per meter level. One entry means the same amount at every level. |
| `Meter` | string | Which meter's level decides the amount. Defaults to the meter that made them speak. |

The item is actually given, and the prompt gains a line telling the AI it is being handed over, so the NPC
mentions it naturally instead of the item appearing out of nowhere.

## Global rules

A rule inside a type only applies to that type. A rule in `GlobalRules` is checked for every witness of every
event, whatever the type — useful for anything that is about the world rather than the act.

```json
{
  "Format": 1,
  "GlobalRules": [
    {
      "Name": "Friday night off",
      "When": { "Weekday": [ "Friday" ], "TimeFrom": 1600, "NotWitness": [ "Penny", "Shane" ] },
      "Multiply": { "confront": 0.5, "humor": 0.5, "tenderness": 0.5, "love": 0.5 }
    },
    {
      "Name": "Weekend off",
      "When": { "Weekday": [ "Saturday", "Sunday" ], "NotWitness": [ "Penny", "Shane" ] },
      "Multiply": { "confront": 0.5, "humor": 0.5, "tenderness": 0.5, "love": 0.5 }
    }
  ]
}
```

Two rules rather than one because `TimeFrom` applies to every day listed in the same rule — splitting them
keeps Friday time-limited and the weekend all day. They never overlap, so nothing is discounted twice.

`Multiply` only touches meters that already have weight, so listing all four is safe on every event type
regardless of which meters it actually uses.

## Worked examples from the shipped catalog

**A character reads the same act differently.** Three rules on `consume`, all matching on
`Payload: { "kind": "alcohol" }`:

```json
{ "Name": "Shane thinks it's a laugh",
  "When": { "Witness": [ "Shane" ], "Payload": { "kind": "alcohol" } },
  "Multiply": { "humor": 3, "confront": 0 },
  "Phrases": { "Full": "{actor} throwing back {p:item} — the kind of evening you know well" } }
```

```json
{ "Name": "Gus sees a good customer",
  "When": { "Witness": [ "Gus" ], "Payload": { "kind": "alcohol" } },
  "Multiply": { "humor": 1.5, "confront": 0 },
  "Reward": { "Item": "(O)346", "CountByTier": [ 1, 2, 3 ] } }
```

```json
{ "Name": "Penny minds the children",
  "When": { "Witness": [ "Penny" ], "Payload": { "kind": "alcohol" },
            "NearbyAge": { "Age": "child", "Tiles": 8 } },
  "Multiply": { "confront": 6, "humor": 0 } }
```

Note what each rule zeroes. A rule only overrides what it declares — everything else keeps the type's default.
Shane and Gus zero `confront` so they never get annoyed about drinking; Penny zeroes `humor` so there is
nothing funny about it with a child in the room. Leaving a meter out is a decision, not an omission.

**A condition that suppresses instead of amplifies.** On `gift`, a badly received present simply earns no
warmth:

```json
{ "Name": "a gift they didn't want",
  "When": { "Role": [ "target" ], "Payload": { "taste": "hated" } },
  "Set": { "tenderness": 0 } }
```

**`PlayerPartner`, and a rule the target must not also match.** On `marriage_proposal`, proposing to someone
while the farmer's own partner is standing right there is worth an outright confrontation — regardless of
whether the proposal was accepted:

```json
{ "Name": "proposing to someone else, right in front of you",
  "When": { "Role": [ "bystander", "relative" ], "PlayerPartner": true },
  "Set": { "confront": 20 },
  "Multiply": { "tenderness": 0, "humor": 0, "love": 0 } }
```

`Role` is restricted to `bystander`/`relative` on purpose. Without it, a farmer re-proposing to their own
existing partner would match this rule too — `PlayerPartner` alone doesn't know that the witness and the
event's target are the same person, only that a rule elsewhere already gave that witness the `target` role for
this specific event.
