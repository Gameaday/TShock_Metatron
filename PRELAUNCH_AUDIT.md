# Pre-Launch Audit — TShock Server Suite (2026-10-24)

> Companion to [ROADMAP](./ROADMAP.md) (suite plan). Phase 4 (Aug–Oct 2027), optional by design. Written now so the suite decision is on record.

## Verdict summary

| Area | Status | One-liner |
|---|---|---|
| Core focus | ✅ clear | "One install, your Terraria server becomes a show" |
| Scope sprawl | ⚠️ structural | Five repos is a portfolio, not a product |
| Overlap | ❌ found | Metatron's Gate duplicates Cerberus's stated role |
| Tier design | ✅ reasonable | Core free, $19.99/server modules, Havoc free for sub-only |

## Core promise & scope check

The promise: server owners install once and get a Discord-gated, event-rich, stream-friendly server. The suite framing is right, but five repos × one person × 2 hrs/week does not divide. **Recommended suite v1.0:** Metatron (installer + core) + **Havoc** (the flagship, the reason anyone buys). Herald/Cerberus/Chronical are modules *only if* users ask — the ROADMAP's honesty section already says this; the audit makes it concrete: **ship two things well, not five shells.**

## Overlap finding — Metatron vs Cerberus

Metatron's README defines its two pillars as **The Gate** (Discord-gated access with `!link` PINs, Limbo quarantine, role enforcement) and **The Scribe** (auto-registration). Cerberus's stated suite role is "Discord role-based access" — **the same job.** Before Phase 4 starts, decide:

- **Option 1 (recommended):** Cerberus becomes the *Discord-side bot half* only (bot process, slash commands, role sync) with Metatron owning everything in-game. Clear seam, no duplicated logic.
- **Option 2:** fold Cerberus entirely into Metatron (it may already be there) and repoint the repo to a plugin-notes/faq role.

## Tier design review

- Core free + $19.99 one-time per-server modules is right for this audience (small, passionate, allergic to subscriptions).
- **Licensing reality check:** .NET plugins are trivially copyable. Per-server license keys need an acceptance decision now: (a) honor-system keys (cheapest, matches ethos, some piracy), or (b) online check on server start (opinionated; some server owners will refuse). Recommend (a) with a unique-per-server key that unlocks updates — keep it friendly, this market rewards trust.
- Havoc free for sub-only channels: goodwill + distribution — keep.

## Professionalism & polish bar

| Item | Bar |
|---|---|
| Suite branding | one name, one logo, one docs site — decided in month 1, applied everywhere |
| Installer | single download; `install` and `update` must both be one command |
| Docs | one "suite home" page with a module matrix (what does what, what costs) |
| Marketplace | TShock forums thread + Tebex-style listing; GitHub as source + support |
| Support funnel | pinned Discord invite in every README; issues templates on Metatron |

## "As-is" vs 1.0

- **As-is:** five working-but-separate plugins with independent READMEs. Users must self-assemble — that is the gap.
- **Suite 1.0 (Oct 2027 bar):** one installer, Metatron + Havoc integration, one docs site, per-server licenses live, 10 streamers pitched, holiday launch.

## Ordered steps at phase start

1. Decide Cerberus seam (option 1 or 2 above).
2. Installer + shared config (month 1 scope).
3. Havoc v1 polish + streamer outreach (month 2).
4. Licensing decision + marketplace listing (month 3).
