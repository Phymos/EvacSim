# EvacLoop

## What it does

Floor plan image to 3D evacuation sim. Unity evacuates 250 agents and writes `results.json`; Muse Spark proposes layout fixes in a closed loop (`layout.json` -> sim -> `layout_v2.json` -> sim).

## Architecture

Python + Unity, joined by JSON files. `plan_to_layout_cv.py` parses the plan to `layout.json`; Unity (`LayoutLoader`, `EvacManager`) builds the 3D scene and measures `results.json`; `optimize.py` sends layout + results to the model and applies the returned changes.

## How to run

```bash
python plan_to_layout_cv.py
python make_tight.py
python optimize.py layout.json results.json layout_v2.json
```

Open `unity/EvacSim` in Unity, press Play. `L` loads `layout_v2.json`, `R` loads `layout_tight.json`.

## Meta tools used

Muse Spark via Responses API (structured JSON output) for layout fixes. SAM 3.1 was tested on the plan and returned 0 matches, so plan parsing uses OpenCV.

## Limits

Prototype, not a validated evacuation model. One building type (4 rooms + corridor). Fixed agent speed/behavior, simple congestion metric.

Built during the hackathon.
