# Auto Mode — design

One orange **automode** button next to the toolbar's Close item. Pressing it fetches a
few subscription sources, finds the servers that actually work, and leaves the best ten
sitting in a dedicated group that is visible under **All**. A note icon beside it opens
the source list so the user can paste in as many subscription links as they like.

Everything below that is a number was measured against the user's real `sub.txt`, not
guessed. The probes live in the scratchpad (`probe.py`, `realprobe.py`).

## What the measurements changed

Probe 1 — fetch and parse all 77 unique links:

| | |
|---|---|
| sources reachable | 67 / 77 |
| configs parsed | 374,200 |
| unique endpoints | 84,982 (4.4x duplication across sources) |
| TCP-alive on a 600 sample | 79.7% |

Probe 2 — same pool, but every candidate driven through a real xray tunnel to
`https://www.google.com/generate_204`, which is exactly what v2rayN's real-ping does:

| | |
|---|---|
| **real-ping pass rate** | **9 / 189 = 4.8%** |
| arm A: the 100 lowest tcping | 2.1% pass |
| arm B: 100 random alive | **7.5% pass** |
| median tcping of passers | 64 ms |
| median tcping of failures | 12 ms |
| by protocol | vless 6/153 · ss 2/3 · vmess 1/15 · trojan 0/18 |

Three findings drive the design:

1. **TCP reachability means almost nothing.** 80% of endpoints accept a connection;
   5% actually tunnel. A bare TCP connect is not evidence of a working proxy.
2. **Sorting by lowest tcping is worse than not sorting at all** — 2.1% vs 7.5%. The
   fastest-answering endpoints are CDN edges that ACK any SYN while the proxy behind
   them is dead. So tcping is used *only* to drop endpoints that answer nothing; it is
   never used to rank.
3. **The source is the highest-leverage variable.** Pass rates ran from 0% (over 37–59
   samples) to 50%, and two big sources returned byte-identical content. Picking which
   sources to spend a run on matters far more than picking which config to test.

Caveat: probe 2's own URI→outbound conversion is cruder than v2rayN's, and 11 of 200
configs were rejected outright by xray, so the true pass rate inside v2rayN is likely
somewhat above 4.8%. The funnel therefore adapts to the observed rate instead of
hard-coding a width.

## Pipeline

```
select sources ──▶ fetch ──▶ sample configs ──▶ import to pool group
                                                        │
                        ┌───────────────────────────────┘
                        ▼
              tcping (liveness only, no ranking)
                        │
                        ▼
     real-ping in batches, stop when enough passers or budget spent
                        │
                        ▼
              speed test the passers (+ current champions)
                        │
                        ▼
        keep best 10 by speed ──▶ "Auto Mode" group, visible in All
                        │
                        ▼
              write source health back to JSON
```

**Adaptive stage.** Because the pass rate is uncertain (5–15% depending on source mix),
the real-ping stage is a loop, not a fixed width: it keeps pulling batches of ~120 from
the live pool until it has `TopCount * 2` passers or reaches a budget of ~600 tests.
A good source mix finishes in one or two batches; a decayed one spends the whole budget
and reports honestly that it found little.

**Champions are re-tested, never grandfathered.** The servers currently in the top-10
group enter the speed-test stage alongside the new candidates. A champion keeps its slot
only by beating the newcomers again, so a server that died since last run drops out on
its own and the list quality never silently rots. This is what makes "always 10 good
ones" true rather than "10 that were good once".

## Choosing sources — Thompson sampling with decay

Each source carries Beta evidence `(alpha, beta)`. Per run:

1. Decay all evidence toward the prior: `alpha ← 1 + (alpha-1)·0.92`, same for beta.
   Evidence from months ago fades, which is precisely the "sub works for a week, then
   stops" behaviour the user described.
2. Draw `theta_i ~ Beta(alpha_i, beta_i)` and take the top K (default 8, adaptive).
3. Force in up to 2 never-tried or longest-untried sources so new links get a chance.
4. Every 5th run, add one resurrection slot for an auto-disabled source — a link that
   died in March may be alive again in July.

Credit after a run, per source:

| outcome | update |
|---|---|
| a config passed real-ping | `alpha += 1` |
| a config reached real-ping and failed | `beta += 1` |
| a config reached the final top-10 | `alpha += 2` |
| contributed configs but none survived | `beta += 1`, `deadStreak++` |
| fetch failed | `beta += 2`, `deadStreak++` |
| body identical to last fetch | `staleRuns++` (shown in the UI, no penalty on its own) |

`deadStreak >= 5` auto-disables the source; it still gets resurrection probes.

K adapts: fewer passers than target next time means more sources, plenty means fewer.

## Storage

`guiConfigs/autoMode.json` — the source list and its health. JSON rather than a new
SQLite table: no schema migration against an existing 3.5 MB database, trivially
hand-editable, and it rides along with the existing guiConfigs backup.

Servers live in normal `ProfileItem` rows in two groups, so the rest of v2rayN — the
grid, sorting, connecting, All — works on them unchanged:

- **pool group** — the working set, wiped and refilled each run.
- **Auto Mode group** — the surviving top ten. `Enabled = false` so a normal
  "update subscription" never overwrites it.

## Reuse, not reimplementation

The stages are v2rayN's own: `ConfigHandler.AddBatchServers` parses every format
already supported (base64, the ten URI schemes, Clash YAML, SIP008), and
`SpeedtestService` runs tcping / real-ping / speed test with core lifecycle, batching
and its retry-on-poisoned-batch logic intact. The only change to existing code is one
awaitable wrapper on `SpeedtestService`, because the current entry point is
fire-and-forget and the pipeline has to know when a stage finished.

That retry logic matters more than it looks: probe 2 confirmed that **a single
malformed config aborts the entire xray process**, taking its whole batch down with it.
v2rayN already halves the batch and retries, which is why the pipeline routes through it
rather than starting cores itself.

## Measured cost of a run

Three consecutive runs against the real 76-link list, on the user's machine:

| | |
|---|---|
| wall clock | 211–259 s |
| CPU | ~35 CPU-seconds (≈14% of one core for the duration) |
| memory, idle → peak → idle | 202 → 475 → 231 MB |
| concurrent core processes | 6 at peak |
| servers left in the database | 10 |

The memory figures are after fixing three retentions found by measuring rather than
guessing: the downloaded bodies were held for the whole run when only their hash was
still needed, `ProfileExManager`'s in-memory cache kept a result row for every deleted
candidate, and the burst of short-lived allocations left the working set inflated on a
process that then sits in the tray for hours. Before those fixes a run settled at
726 MB and never gave it back; it now returns to ~30 MB above where it started.

## Does it keep working?

Verified across runs, not asserted:

- **Servers rot, and the list notices.** Run 1's fastest server (7.5 MB/s) was re-tested
  in run 2, measured at 2.7 MB/s, and fell from #1 to #7. Five champions defended their
  slots; five were replaced.
- **The list improves rather than drifts.** The weakest member went from 0.2 MB/s in run
  1 to 1.2 MB/s in run 2, because every slot has to be re-won against fresh candidates.
- **Sources rot, and the sampler notices.** After one run, a link that produced 107
  tested configs and zero working ones sat at `alpha=1, beta=108`; one that produced 3
  of the top ten sat at `alpha=12`. Five such runs auto-disable a link.
- **Effort self-adjusts.** `SourcesPerRun` moved 8 → 6 → 4 → 8 across three runs,
  narrowing when survivors were plentiful and widening when they were not.

The one thing it cannot do is invent working links. If every source in the list dies,
the run reports that honestly instead of silently returning stale servers, and the
source window shows which links rotted so they can be replaced.

## UI

- **automode** button — orange, in the toolbar. Disabled while a run is in progress;
  progress lands in the existing message panel via `NoticeManager`.
- **note icon** beside it — opens a window with one textbox (one URL per line, commas
  also accepted, thousands of lines fine) and a grid showing per-source quality, last
  success, config count and stale/dead state, so it is obvious which links have rotted.
