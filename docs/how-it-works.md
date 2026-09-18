# How it works

The path from something happening in the world to a line of text inside an AliveNpcs prompt. Seven stages,
each of which can be inspected with a console command while you play.

```
sensor → witnesses → grade → score → meters → saturation → delivery
```

## 1. A sensor records an event

A sensor is the one piece that has to be C#: it hooks a game event and describes what happened, without
deciding what it means.

```csharp
var draft = new EventDraft("consume", "Consume", location, farmer.TilePoint);
draft.Payload["item"] = item.DisplayName;
draft.Payload["kind"] = "alcohol";
_mod.Recorder.Record(draft);
```

The draft carries facts only: type, where, who was involved, and a payload of raw values. It never carries
wording, weights or feelings — those come from the catalog, keyed by the type string. Nothing downstream
contains a `switch` on the event type.

Shipped sensors use SpaceCore events (gifts, bombs, eating), SMAPI events (warps, shop menus, day start) and a
handful of small Harmony patches (locked doors, passing out, talking to an NPC).

## 2. Who witnessed it, and how sure are they

`World/Witnesses.cs` walks every villager on the relevant maps and works out a **visibility** from 0 to 1:

- **Distance** — configurable bands, falling off with tiles, then a floor beyond the last band.
- **Weather** — rain and storms cut visibility, sun does not.
- **Light** — indoor lighting and the time of day.
- **Attention** — whether the NPC was moving, facing your way, or busy.
- **Walls** — a villager inside a bedroom cannot see the hallway, and vice versa. Some event types pierce
  walls (`Perception.ThroughWalls`), because an explosion is heard through a door and tiptoeing is not.
- **Sleep** — a villager asleep in their own bedroom witnesses nothing, unless the type sets
  `Perception.WakesSleepers`.

Visibility becomes a **certainty level from 1 to 5** by comparing it against thresholds in the config. Two
shortcuts exist: `Perception.HeardFloor` sets a minimum for everybody on the map (how loud the thing is), and
people *directly* involved — the person you gifted, the shopkeeper you bought from, the owner of the bedroom —
are certain at level 5, within `Visibility.DirectRangeTiles`.

An event nobody witnessed is not stored at all.

Certainty is not just bookkeeping: it changes the wording the AI receives ("You clearly saw" vs "You think you
saw" vs "It looked like someone"), and every meter has a `MinCertainty` below which a villager will not speak
up about it at all.

Inspect with `as_where` and `as_witness <type>`.

## 3. Grade: how much this kind of thing matters

Grade starts from the type's `Grade` in the catalog and is multiplied by any **aggravators** the sensor
attached — circumstances that make the same act worse:

| Aggravator | Default | Meaning |
|---|---|---|
| `roomOwnerPresent` | ×1.5 | You entered their bedroom while they were standing in it |
| `roomAtNight` | ×1.3 | At night |
| `roomLongStay` | ×1.2 | You lingered |
| `bombNearHome` | ×1.3 | Bombs near where they live |
| `trashOwnerWitness` | ×1.5 | They saw you going through their own trash |
| `giftPartnerNearby` | ×2.0 | You gifted someone while their partner watched |
| `giftLovedOrHated` | ×1.3 | The gift landed strongly, either way |
| `lockedRepeated` | ×1.5 | You tried the door again |
| `lateNight` | ×1.2 | After hours |

All of them are configurable. Grade is stored on the memory once and never recomputed.

## 4. Score: how much this memory matters *to this villager, today*

```
score = grade × recency × involvement × visibility × witness bonus × repetition
```

- **Recency** decays on the type's `HalfLifeDays`.
- **Involvement** is higher when they were the target, the owner, the vendor — lower for a bystander.
- **Repetition** is `1 + RepeatStep × (times seen − 1)`, capped by `RepeatMaxMultiplier`. Doing the same thing
  in front of the same person keeps raising one memory instead of creating identical copies.

Score decides which memories get into a prompt at all (there is a character budget) and how fast the feelings
below fill.

Inspect with `as_score <NPC>`.

## 5. Meters: what they feel about it

The catalog says what each type is worth to each feeling, and for whom:

```json
"Meters": {
  "confront": { "Weight": 1, "RequiresPayload": "alcohol" },
  "humor":    { "Weight": 1, "RequiresPayload": "alcohol" }
}
```

Then **rules** adjust it per witness, per situation — the same drink is worth ×3 amusement to Shane, ×6 anger
to Penny when a child is nearby, and a free beer from Gus. Rules are evaluated once, at record time, on the
main thread, while the world can still be asked questions like "is a child within eight tiles" or "how many
hearts do they have". The answer is stored on the witness, so nothing downstream has to touch live game state.

Each feeling then fills, and drains on its own half-life. See [meters.md](meters.md) and [rules.md](rules.md).

Inspect with `as_saturation [NPC]`.

## 6. Saturation: who gets to speak

Each meter has three levels. Passing one makes that villager want a word: they show the meter's emote bubble
and join the queue.

Only one villager can hold a conversation at a time, so when several are ready the turn goes to the best
arbitration score:

```
proximity × how strongly they feel × what that feeling weighs
```

Ties break by whoever is absolutely closest. One event can only send `MaxReactionsPerEvent` people over
(default 3) because each one is an AI call; everyone else keeps it as an **aside** — no interruption, no emote,
just extra context merged into their next normal conversation, which costs nothing.

Two rules keep this from becoming noise:

- **One feeling per event type per day.** The first meter to reach level 1 for a given event type locks it for
  that villager that day. If they found your drinking funny this morning, the afternoon's beer does not turn
  into anger — and they do not walk over twice about the same thing.
- **Cooldowns and daily caps** per meter (`MaxPerNpcPerDay`), plus a global cooldown between confrontations.

When the strongest level fires, part of the meter survives the night (`GrudgeCarryover`) and seeds the next
day.

## 7. Delivery: three channels

**Conversation prompt.** When you talk to a villager, AliveSensor injects a block of what *that villager*
personally witnessed, worded from their point of view — their own name becomes "you", their bedroom becomes
"your room". The freshest strong memory is marked `[stands out]` and the AI is told to react to it first.

**Walk-up.** When a meter saturates, the villager approaches, AliveSensor clears AliveNpcs' cached opening
line, and opens a normal conversation with an extra block explaining why they came over and in what tone. You
answer or skip it like any other dialogue. If a rule promised a reward, it is handed over as they speak.

**Gossip.** At the end of the day, the day's most notable witnessed events go into AliveNpcs' nightly gossip
prompt, ranked with one-per-type diversity so three bombs do not crowd out everything else.

AliveSensor never writes friendship points. Everything it does reaches the game as text in a prompt.

Inspect with `as_prompt <NPC>` and `as_cycle`.

## Where things live

| Folder | What |
|---|---|
| `Sensors/` | The C# that detects raw game events |
| `World/` | Witness resolution, grading, rule evaluation |
| `Core/` | The catalog, meter and template models |
| `Delivery/` | Scoring, wording, the feeling engine, prompt blocks |
| `Integration/` | AliveNpcs and SpaceCore bridges |
| `Memory/` | Storage, per save, under `Data/<SaveFolderName>/` |
| `assets/` | The catalog and meters — the data everything above reads |
