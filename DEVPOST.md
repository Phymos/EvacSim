# Devpost draft (paste-ready, FILL the ? marks)

## TONIGHT checklist (in order)

1. [ ] Start Unity (Play, then O) + `python loop.py layout_bad.json 4 42,43` (~1–2 h).
2. [ ] Screen-record: bad-layout run, loop proposing, best-layout run (~3 min raw).
3. [ ] `python report.py` → copy headline number + images below.
4. [ ] Cut a 2–3 min video, upload (YouTube unlisted), paste link.
5. [ ] Paste this page into Devpost, submit with ≥30 min to spare.

## Title (pick one)

- EvacLoop: Muse Spark redesigns buildings to evacuate faster
- EvacLoop: closed-loop AI evacuation optimizer

## Tagline

A Unity crowd sim + Muse Spark in a loop: simulate, find bottlenecks, redesign the floor plan, repeat — ? s → ? s (-?%).

## Inspiration

Evacuation plans are validated by hand and rarely re-tested after a redesign. We wanted an agent loop that treats a building like code: simulate, profile the bottlenecks, patch the layout, re-run.

## What it does

Unity evacuates 300 agents through a 4-room building and reports time, per-zone congestion, and bottleneck waits. `loop.py` scores each layout by mean time over multiple seeds, keeps the best, and asks **Muse Spark** for up to 3 new fixes per round (widen a door, add an exit, move a wall) — with full history and a tabu list of changes that made things worse. Unity runs unattended in auto mode, picking up each new layout. Result: **? s → ? s (-?%)**, confirmed on a held-out seed.

## How we built it

- Unity (URP, NavMesh, Input System): procedural floor-plan builder, crowd sim, congestion/heatmap tracking, file-based auto-mode loop.
- Python: `optimize.py` (Muse Spark via Responses API + JSON-schema structured output, code-validated and applied), `loop.py` (autonomous campaign: multi-seed scoring, revert-to-best, retry, tabu), `report.py` (charts + report).
- OpenCV for plan parsing (SAM 3.1 returned 0 matches on our plan, so we went classical).

## Challenges

- Comparable runs: fixed-seed spawning so layout diffs, not RNG, explain time diffs.
- Trust: multi-seed means, held-out confirmation, invalid-reply retries with the error fed back.
- Unattended Unity: file handshake (`results.json`/`layout_next.json`/`status.json`) with timeouts and stale-state guards.

## Accomplishments

- Fully autonomous sim↔LLM loop; start it and come back to an optimized building.
- Measured ?% evacuation-time reduction with per-round history and visual diffs.
- Deterministic, re-runnable pipeline from PNG floor plan to optimized JSON layout.

## What's next

Stress scenarios (blocked exit mid-run), heterogeneous agents, cost-capped proposals, live dashboard.

## Built with

`muse-spark` `responses-api` `structured-output` `unity` `navmesh` `python` `opencv`

## Try it out

```bash
$env:MODEL_API_KEY="your-key"
python loop.py layout_bad.json 4 42,43
# Unity: open unity/EvacSim, Play, then O. Then: python report.py
```

Repo: <!-- FILL: GitHub URL --> Video: <!-- FILL: video URL -->
