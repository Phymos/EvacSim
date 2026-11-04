# EvacLoop — AI evacuation optimizer

Bad floor plan in, safer floor plan out: a Unity crowd sim evacuates 300 agents while **Muse Spark** redesigns the building in a closed loop — sim → bottleneck analysis → layout fix → sim again — until evacuation time stops improving.

<!-- FILL: demo video link -->
<!-- FILL: report/progress.png and report/comparison.png once you run the loop -->

## Results

<!-- FILL after your campaign run (or paste from report/REPORT.md) -->

| round | layout | time_s | vs round 0 | changes |
| ----- | ------ | ------ | ---------- | ------- |
| 0 | layout_bad.json | ? | — | baseline |
| ... | ... | ... | ... | ... |

Headline: **? s → ? s (-?%)**, confirmed on a held-out seed.

## How it works

```
floorplan.png → plan_to_layout_cv.py → layout.json ─┐
                                                    v
loop.py ←── results.json + heatmap.png ←── Unity EvacSim (300 agents, NavMesh)
   │  scores each layout by mean time over 2+ seeds, keeps the best
   └── layout_next.json ←── Muse Spark (structured-output proposals:
                            widen_door / add_exit / move_wall, ≤3 per round)
```

- `optimize.py`: single-shot Spark call — layout + results in, validated JSON changes out, applied to `layout_v2.json` with a `_check.png` overlay.
- `loop.py`: autonomous campaign — waits for each Unity run, tracks `history.json` (times, changes, reasons), reverts worse rounds to the best, retries invalid replies, stops after N rounds or two straight rounds improving < 3%, then confirms the winner on a held-out seed.
- `report.py`: turns `history.json` into `report/` — progress chart, round-0-vs-best comparison, and a markdown summary.
- Unity auto mode (press `O`): seeded spawns for comparable runs, writes `results.json` + `heatmap.png` per run, picks up `layout_next.json` automatically. The HUD shows what it is waiting for.

## How to run

```bash
set MODEL_API_KEY=your-key   # Windows: $env:MODEL_API_KEY="your-key"
python loop.py layout_bad.json 4 42,43   # layout, max rounds, seeds
python report.py history.json report     # after the campaign
```

Open `unity/EvacSim` in Unity, press Play, then `O` for auto mode (`L` v2, `R` tight, `B` bad, `H` heatmap for manual runs).

## Meta tools used

- **Muse Spark** via Responses API with JSON-schema structured output for layout fixes (single-shot and in-loop with history + tabu list).
- SAM 3.1 was tested on the plan and returned 0 matches, so plan parsing uses OpenCV.

## Limits

Prototype, not a validated evacuation model. One building type (4 rooms + corridor). Fixed agent behavior unless `speedVar` is set; congestion metric is simple (slow-agent fraction per zone).
