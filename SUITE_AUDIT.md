# TShock Suite Audit (2026-10-24)

> What each repo actually is, what belongs in the suite, and what to archive. Companion to [ROADMAP](./ROADMAP.md) and [PRELAUNCH_AUDIT](./PRELAUNCH_AUDIT.md).

## History, reconstructed

Metatron was the original mega-plugin. It grew too big, and the split produced three specialist plugins: **Cerberus** (the Gate — Discord access control), **Herald** (the Voice — events and comms), and **Chronical** (the Memory — stateful gameplay; design only, never built). **Havoc** came later as the Twitch-interaction flagship. The split was the right call — this audit finishes it.

## Inventory

| Repo | What it actually is | State | Suite verdict |
|---|---|---|---|
| **Havoc** | Stateless Twitch bridge + semantic query engine | Built, has release CI | ✅ **Flagship** — the suite's reason to exist |
| **Cerberus** | Discord auth bridge: TShock plugin + Discord bot + shared SQLite + Docker | Built, most product-like | ✅ Core — the Gate |
| **Herald** | Event/broadcast engine, Discord webhooks, streamer mode | Built | ✅ Core — the Voice |
| **Metatron** (here) | The original monolith: the Gate + the Scribe (auto-registration) | Built | ⚠️ **Split incomplete** — its Gate duplicates Cerberus |
| **Chronical** | Stateful gameplay engine | **Design only — no code exists** | ⚠️ Build-or-archive decision required |

## Finding 1 — Metatron vs Cerberus: the split was never finished

Metatron still carries the full Gate (Discord `!link` PINs, Limbo quarantine, role enforcement) while Cerberus implements the same job with a better architecture (dedicated bot, SQLite single-source-of-truth, fail-closed, multi-server). Two Gates means double maintenance and confused users.

**Recommended resolution:** re-scope Metatron to what only it does — **the Scribe**: invisible registration, auto-login, account provisioning. Its README/docs should say "for Discord gating, use Cerberus." Alternative: fold the Scribe into Cerberus and archive this repo. Do **not** keep both Gates alive.

## Finding 2 — Chronical: unbuilt since the split

The repo contains a design conversation, not code. The design itself is good (SQLite-backed key-value ledger, JSON logic blocks, pub/sub evaluation, and the Havoc→Chronicle→Herald synergy). Decide at the Phase 3 gate:

- **Build** — it is the suite's stateful layer and the "crown jewel" of the synergy story; or
- **Archive** — if Phase 4 shrinks to Metatron + Havoc per the pre-launch audit, an unbuilt repo is just surface area.

Either way, its README is now a proper design doc rather than a raw AI conversation (rewritten 2026-10-24).

## Finding 3 — Consistency gaps

| Gap | Fix |
|---|---|
| Only Havoc has release CI | Port Havoc's workflow to Herald, Cerberus, Metatron |
| Repo naming mixes `TShock_Herald` (underscore) and `TShock-Cerberus` (hyphen) | Standardize display names: Havoc, Herald, Cerberus, Chronicle, Metatron; renaming repos is breaking — fix in docs/branding, not repo names |
| "Chronical" is a misspelling of "Chronicle" (its own design doc says Chronicle) | Use **Chronicle** in all display text going forward |
| Havoc README carries its own roadmap section | Point READMEs at each repo's ROADMAP.md — one planning surface |
| Cerberus README has a broken code fence in Installation | Fix during next touch |
| No example broadcast pack for Herald | Ship `Broadcasts/examples/` — first-run experience matters for a config-heavy plugin |

## The suite story (for marketing)

Already designed and worth keeping verbatim: *Havoc (the Chaos) triggers events from Twitch; Chronicle (the Memory) tallies state and fires milestones; Herald (the Voice) announces to Discord; Cerberus (the Gate) decides who may enter.* One install, and your Terraria server becomes a show.

## What is / is not part of the suite going forward

- **In:** Havoc (flagship), Cerberus, Herald, and Chronicle **if** the build decision is made; Metatron only as the Scribe module or a legacy archive.
- **Out / archive candidates:** any repo that duplicates another's job (currently: one of the two Gates). Archiving is reversible; maintaining duplicates is not free.

*(Repo archiving is a Carl-only action — these are recommendations, not changes.)*
