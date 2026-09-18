# Phrase templates

Every line a villager "remembers" is rendered from a template in the catalog. Templates are small, but they
have to do real work: the same event has to read naturally whether the witness saw it plainly or barely caught
a glimpse, whether it happened to them or to someone else, and whether it is being told now or retold as
gossip tonight.

## Phrase sets

```json
"Phrases": {
  "Full":    "{actor} give {target} {a:item}[ — {tasteText}][, {nearPhrase}]",
  "Reduced": "{actor} hand {target} something",
  "Glimpse": "handing something to {target}",
  "Past":    "{actor} gave {target} {a:item}[, and {target} {tasteVerb} it]"
}
```

| Slot | Used when |
|---|---|
| `Full` | Certainty 5 and 4 — what someone who saw it plainly can describe |
| `Reduced` | Certainty 3 and 2 — what an unsure witness makes out. Falls back to `Full`. |
| `Glimpse` | Certainty 1 — the barest impression. Falls back to `Reduced`. |
| `Past` | The night's gossip, told in the third person |
| `Levels` | `{"5": "...", "3": "..."}` — replaces the whole wrapper for one level |

The engine wraps the slot in a certainty phrase automatically:

| Level | Rendered as |
|---|---|
| 5 | You clearly saw *&lt;Full&gt;* |
| 4 | You saw *&lt;Full&gt;* |
| 3 | You think you saw *&lt;Reduced&gt;* |
| 2 | You think you saw *&lt;Reduced&gt;*, but you're not sure |
| 1 | You noticed someone who looked like *&lt;actor&gt;* *&lt;Glimpse&gt;*, but you're not sure |

Because of that wrapper, write slots as a continuation of "you saw …": `"{actor} give {target} {a:item}"`, not
`"{actor} gave {target} an item."` — no leading capital, no trailing period.

`Levels` exists for types where that wrapper is wrong. An explosion is heard, not seen, so `bomb` supplies its
own line for the levels where "You clearly saw" would be nonsense.

## Variants

When the wording depends on a value rather than a certainty, select a phrase set with `VariantBy`:

```json
"VariantBy": "p:action",
"Variants": {
  "enter": { "Full": "{actor} go into {place}" },
  "leave": { "Full": "{actor} leave {place}" }
},
"Phrases": { "Full": "{actor} at {place}" }
```

The token's value picks the set; a `default` variant catches anything unmatched; missing slots fall back to the
base `Phrases`.

## Template language

### Tokens

`{token}` inserts a value. If the value is missing or empty, any optional group containing it disappears.

### Optional groups

`[ ... ]` renders only if everything inside resolves. This is how one template handles events with and without
extra detail:

```
{actor} drinking [{p:item}][, {nearPhrase}]
```

With both: "the farmer drinking Beer, near the counter". With neither: "the farmer drinking".

`[ ... | ... ]` adds a fallback — the part after the `|` is used when the first part does not resolve. Groups
nest:

```
[{p:item} {count?|#} times in a row|{a:item}]
```

### Plurals

`{token?singular|plural}` picks a form based on the token's number, and `#` inside the chosen form becomes that
number:

```
{p:nearbychildCount?a child|# children}   →   "a child"   /   "3 children"
{count?|# times in a row}                 →   ""          /   "4 times in a row"
```

An empty chosen form drops the group it sits in, which is how "once" renders as nothing at all.

The separator is `?`, not `:`, because `:` is already the namespace separator inside a token name.

### Escapes

`{{` `}}` `[[` `]]` produce literal braces and brackets.

### Tidying

After rendering, double spaces collapse and spaces before punctuation are removed, so optional groups can be
written with natural spacing without leaving gaps when they drop out.

## Built-in tokens

These work in every template of every type, with no declaration.

| Token | Value |
|---|---|
| `{actor}` | Who did it, from the witness's point of view — "you" if it was them |
| `{target}` | The person it was done to, if any |
| `{targetPossessive}` | "Abigail's", or "your" when the witness is the target |
| `{owners}` | Possessive of the place's owners — "Shane's", "your" if the witness lives there |
| `{ownersPlain}` | The owners' names without the possessive |
| `{place}` | The place's name, or "your home" when the witness lives there |
| `{location}` | The map's display name |
| `{nearPhrase}` | The nearest landmark — "near the counter", "by Shane's bedroom door" |
| `{time}` | The time in 12-hour format |
| `{count}` | `payload["count"]` |
| `{seen}` | How many times this witness has seen this — the repetition counter |
| `{attempt}` | `payload["attempt"]` |
| `{witnessName}` | The witness's display name |
| `{secretMode}` | `secret` or `open`, from the config and `payload["secret"]` |

### Payload prefixes

| Prefix | Meaning |
|---|---|
| `{p:key}` | The raw payload value |
| `{a:key}` | The payload value with an article — "a Beer", "an Amethyst" |
| `{n:key}` | The payload value treated as an NPC name — becomes "you" if it is the witness |
| `{you:key}` | Literally "you" when `payload[key]` is the witness, otherwise nothing |

`{n:}` and `{you:}` are what keep a line from telling someone "you saw Penny watching you" when Penny *is* the
reader.

## Computed tokens

A type can define its own tokens, mapping a payload value to a fragment. This keeps branching out of the
phrase itself:

```json
"Tokens": {
  "tasteText": {
    "From": "taste",
    "Map": {
      "loved":    "{target} loved it",
      "liked":    "{target} liked it",
      "disliked": "{target} didn't like it",
      "hated":    "{target} hated it"
    },
    "Default": ""
  }
}
```

| Field | Meaning |
|---|---|
| `From` | The token to read. A bare name is read from the payload. |
| `Map` | Value → fragment. Fragments may use tokens themselves, including other computed ones. |
| `Default` | Used when the value is not in the map. Empty by default, which drops optional groups that contain it. |

Computed tokens are scoped to the type that declares them. Nesting is depth-limited, so a token cannot
recurse into itself forever.

## Rule phrases

A rule can replace the wording for the witnesses it matches, which is how the same act reads differently to
different people:

```json
{ "Name": "Shane thinks it's a laugh",
  "When": { "Witness": [ "Shane" ], "Payload": { "kind": "alcohol" } },
  "Phrases": { "Full": "{actor} throwing back {p:item} — the kind of evening you know well" } }
```

Only the slots the rule declares are replaced; the rest fall back to the type's own set.

## Checking your work

The catalog compiles every template at load and reports problems in the log: unbalanced markup degrades to
literal text rather than throwing, unknown tokens render as nothing, and a type with no phrases at all is
reported as an error (its memories would read as "did something").

`as_list <NPC>` shows finished lines for a real villager. `as_simulate <type>` records a test event at your
position so you can see a new template render immediately.
