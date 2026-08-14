# Community Ratings (Phase 2)

Design and implementation notes for the peer-review feature. The server lives in a separate
repo — see `PfRatingsApi/README.md` for the backend half.

---

## A vote is never thrown away, on either side

**The client sends. The server decides. Neither of them deletes.**

Read this before touching anything in the voting path. Every vote-loss incident this feature has
had was one of these two rules being broken by code that looked fine on its own, and half of them
were in *this* repo rather than the server.

What it cost, measured against one player's own `ratings-given.json`: 19 cast / 7 landed,
27 / 2, 17 / 1 — **57 of 100 votes existed in no table at all.** Across the life of the system,
more than half the votes it was given were destroyed by code that had decided they did not count.

The four places it happened:

1. `FlushVotesAsync` built `SubmitRatingRequest` with no `Evidence` field, so ~1,700 votes were
   refused as unreadable and then deleted as `Failed(permanent)`.
2. A first attempt at fixing that had the client *drop* votes whose evidence would not build —
   the same mistake wearing a fix's clothes.
3. Evidence was built at **send** time, after the over-allowance early return, so a queued vote was
   stored with no evidence and could never have had any.
4. `VoteQueue.Next()` silently `RemoveAll`'d anything queued for over an hour, in a debug line,
   without sending. A vote nobody refused and nobody counted existed in **no place at all**.

**Concretely, in this codebase:**

- The client's job is to deliver a vote and keep delivering it. It does not get to decide a vote is
  invalid, stale, over quota, unsendable, or not worth the bandwidth.
- Never `Remove`, `RemoveAll`, `Clear`, or skip an entry in the vote queue because of its *content*
  or its *age*. Delivery may be retried, deferred, or bounded — the vote itself stays.
- Build everything a vote needs at **cast** time, not send time. Anything built later is built after
  the early returns that might skip it.
- A local record of what was cast (`ratings-given.json`, 100 entries) is not a cache. It is the only
  copy that survives a server-side loss, and it is what any future audit is reconciled against.

**The server half of the rule** — anything that arrives is written before it is judged and is never
removed by any automatic process — lives in `PfRatingsApi/README.md` under *"Nothing that arrives
here is ever erased"*, and is enforced by the `vote_archive` table (migration 036).

---

## Build flag

All of this sits behind the `PFP_RATINGS` compile symbol, **off by default**:

```bash
bash build.sh                # ratings compiled in  (third-party repo build)
bash build.sh --no-ratings   # ratings absent       (official repo build)
dotnet build -p:EnableRatings=true ...
```

The reason is distribution, not taste. The official Dalamud repo's rules bar plugins that collect
and share data about other players, and a searchable reputation database is squarely that. A
`--no-ratings` build contains none of the rating code — verified by checking the DLL for the
symbols — so PF Presets can stay submittable there while the full feature ships through
`Marobotic/PfPresets`.

---

## Identity model

Three keyed hashes, three separate peppers, all held server-side only:

```
target_key = HMAC-SHA256(pepper_target, lower(name) + "@" + lower(world))
voter_key  = HMAC-SHA256(pepper_voter,  lower(name) + "@" + lower(world))
ip_bucket  = HMAC-SHA256(pepper_ip,     ip_prefix + window)
```

The database therefore holds no character names. Search still works because the client sends
`name@world` in a request body and the server hashes it — the server never needs to *return* a
name, since the client already knows what it asked for.

`CharacterIdentity.Key` in `Data/RatingModels.cs` and `normaliseCharacter` in the backend's
`identity.js` **must** normalise identically, or the two sides will disagree about what counts as
the same character.

---

## Votes

A vote is a direction, not a score: thumbs up or thumbs down, one click, submitted immediately.
There is no star scale and no confirm step. After one duty with a stranger you know whether you'd
play with them again and little else, and a five-point scale invited deliberation the situation
doesn't support. Two options also give a brigade far less room to nudge a score without it being
obvious.

Scores are reported as the **share of votes that are positive**, raw and weighted — see
`presentAggregate` in the backend's `weights.js`.

## Trust weighting

```
weight = 1.0 × social × repeat

  social  0.5 for a friend or FC member, else 1.0
  repeat  0.1 for the 2nd+ rating of the same target by the same voter, else 1.0
```

Multiplicative, so a friend's repeat vote lands at 0.05. The stacking is intentional: a friend
group rating each other repeatedly is the exact pattern the engine exists to flatten.

The weight is always computed **server-side** from the server's own ledger. The client sends a
social-link claim, never a weight.

The friend/FC link is detected by `Game/SocialLinkResolver.cs` **while the duty is still running**
and stored on the encounter. It cannot be worked out afterwards — once the duty ends the party is
gone and with it the members' FC tags.

---

## Where things live

| Concern | File |
|---|---|
| Wire types, tags, `CharacterIdentity` | `Data/RatingModels.cs` |
| HTTP transport, session, backoff, breaker | `Core/PfApiClient.cs` |
| Cache, batching, cooldowns, submit flow | `Core/RatingService.cs` |
| Duty lifecycle, party sampling | `Core/DutyTracker.cs` |
| Local duty log (sidecar file) | `Data/EncounterStore.cs` |
| Friend / FC detection | `Game/SocialLinkResolver.cs` |
| World names, FFLogs region | `Data/WorldHelper.cs` |
| External URLs | `Data/CharacterLinks.cs` |
| Shared vote row, job icons, score chip | `UI/PluginUI.Voting.cs` |
| Post-duty vote window | `UI/PluginUI.RatingPrompt.cs` |
| Ratings tab, eligible list, nav strip | `UI/PluginUI.Ratings.cs` |
| Contacts tab (read-only) | `UI/PluginUI.Contacts.cs` |
| Party member list, kick, report buttons | `UI/PluginUI.PartyPanel.cs` |
| Report dialog | `UI/PluginUI.ReportDialog.cs` |
| Native kick call | `Game/PartyCommands.cs` |
| Settings + privacy disclosure | `UI/PluginUI.RatingSettings.cs` |

### Where you can see a score, and where you can cast a vote

These are two different questions and the answers differ.

**Scores are shown** on your party members, in Contacts, and on any character you look up in the
Ratings tab. They are deliberately **not** shown while browsing the Party Finder: a score attached
to a listing you haven't joined is a screening tool for strangers, which turns the feature into a
blacklist.

**Votes can only be cast** in two places — the Ratings tab's eligible list, and the post-duty
prompt. Contacts is read-only by design; it's a record of who you met, and folding an action into
it would mean two surfaces to keep in step.

Eligibility is "met in a duty within `EncounterStore.VotingWindow` (24h) and not yet voted on".
The gate lives in `RatingService.SubmitAsync` and `EncounterStore.HasMet`, **not** in the UI,
because a gate that lives in the UI is one every new screen can forget. The lookup can therefore
show you anyone's score while being able to rate nobody.

### Two deliberate structural choices

**The duty log is a sidecar file, not part of `Configuration`.** Dalamud rewrites the entire
config blob on every `Save()`, so a per-duty log in there would re-serialise every preset the user
owns each time somebody joins a party. It also means the one piece of data the plugin holds about
other people is a single file that can be pointed at and deleted.

**Row heights are measured, never hard-coded.** `VoteRowHeight()` derives from
`ImGui.GetTextLineHeight()` and `GetFrameHeight()`. The first version used fixed pixel heights and
clipped its own controls; anything that depends on the font or UI scale has to be asked for at
draw time.

**Lookups never touch the draw thread.** `RatingService.Get()` answers from cache and queues a
refresh; a background pump folds queued lookups into batch requests. A full party plus a screen of
contacts therefore costs one request rather than thirty, and a dead server costs nothing but a
stale chip.

---

## Party actions

The party panel gives the leader a **Kick** button per member, behind a two-step confirmation.
Kicking is immediate and irreversible, and a misclick mid-pull is a real way to ruin someone's
evening, so it takes a second deliberate press.

It calls `AgentPartyMember.Kick` — the same native function the game's own party-list context menu
uses — rather than synthesising a `/kick` chat command. Chat synthesis would be fragile (names with
spaces, localisation) and a much worse thing to be doing programmatically.

## Reports

Reports go to the plugin author, via the API, and are **not** anonymous — see the header of
`migrations/002_reports.sql`. An anonymous report cannot be followed up and is trivial to abuse, so
the reporter is recorded and their running report count is shown alongside each one. The plugin's
UI says plainly, twice, that this is not a Square Enix report.

The Discord webhook URL lives **only** on the server (`DISCORD_REPORT_WEBHOOK`). It must never be
put in the plugin: a webhook shipped inside a DLL can be extracted by anyone who unzips it, and
from then on the channel can be flooded with no fix short of rotating the URL and pushing an update
to every user. Reports are written to Postgres first and notified second, so a Discord outage
delays the notification rather than losing the report; the `delivered` column records which ones
still need chasing.

Report notes are attacker-controlled text shown to a human, so `discord.js` escapes Discord markup,
strips mentions and links, and sends `allowed_mentions: { parse: [] }`.

---

## Known gaps

**The Tomestone URL is unverified.** `CharacterLinks.Tomestone` was written without being confirmed
against the live site, which blocks automated requests. Check it once in-game; if it's wrong it is
a one-line fix in `Data/CharacterLinks.cs`. The FFLogs and Lodestone links are on solid ground.

**There is no working opt-out yet.** The server's `/optout` endpoint refuses every request because
its ownership check is a stub. Until that is implemented via Lodestone bio verification, a rated
player has no way to remove themselves. This shipped in 3.2.0 regardless, as a deliberate release
decision — so it is now a live gap with real users behind it, not a pre-release one, and it is the
first thing to close in the next release.
