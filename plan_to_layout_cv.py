"""Floor plan layout via OpenCV (no API calls).

Usage:
    python plan_to_layout_cv.py

Reads floorplan.png and writes:
- layout.json: {"image_size": [w, h], "walls": [{"x","y","w","h"}],
  "exits": [{"x","y","w","h"}], "doors": [{"x","y","width"}]},
  all pixel coordinates, origin top-left. NOTE: this overwrites the
  layout.json written by plan_to_json.py, which uses a different schema.
- layout_check_cv.png: floor plan with walls in red, doors in blue,
  exits in green, for visual verification.

Method:
1. Walls: pixels with max(R,G,B) < 50 (black walls; gray labels and
   green exits/outlines excluded). Horizontal and vertical runs are
   separated with morphological opening (40x1 / 1x40 kernels), then one
   connected component = one axis-aligned wall rectangle. Short bits
   (e.g. the black title text) are dropped by a min-length filter.
2. Exits: green pixels (R<50, B<50, G>80 in RGB); connected components
   with area >= 1000 px become bounding boxes (drops the small dark
   green "EXIT n" labels).
3. Doors: gaps of 40-120 px between consecutive collinear wall runs.
   Gap centers falling inside an exit box are exit openings, not doors,
   and are skipped.
"""

import json
import os
import sys

import cv2
import numpy as np

IMAGE_PATH = "floorplan.png"
LAYOUT_PATH = "layout.json"
CHECK_PATH = "layout_check_cv.png"

DARK_MAX = 50        # max(R,G,B) below this = black wall pixel
RUN_KERNEL = 40      # opening kernel: shorter than any wall run (~90+ px),
                     # longer than wall thickness (~8 px) and title glyphs
RUN_MIN_LONG = 40    # keep runs whose long side is at least this (px)
RUN_MAX_SHORT = 24   # ... and whose short side is at most this (px)
LINE_TOL = 5         # same-wall-line grouping tolerance (px)

EXIT_R_MAX = 50      # exit green test (RGB): R and B low ...
EXIT_B_MAX = 50
EXIT_G_MIN = 80      # ... G clearly above gray/black
EXIT_MIN_AREA = 1000  # exit rectangles are ~40x80 = 3200 px; text is smaller

DOOR_GAP_MIN = 40    # doors are wall-line gaps roughly 40-120 px wide
DOOR_GAP_MAX = 120
EXIT_MARGIN = 4      # gap centers within an exit box + margin are exits


def wall_runs(dark, horizontal):
    """One pass of run detection: opening + connected components."""
    kernel = cv2.getStructuringElement(
        cv2.MORPH_RECT, (RUN_KERNEL, 1) if horizontal else (1, RUN_KERNEL))
    opened = cv2.morphologyEx(dark, cv2.MORPH_OPEN, kernel)
    count, _labels, stats, _centroids = cv2.connectedComponentsWithStats(
        opened, connectivity=8)
    runs = []
    for i in range(1, count):  # 0 = background
        x, y, w, h = (int(v) for v in stats[i, :4])
        if max(w, h) >= RUN_MIN_LONG and min(w, h) <= RUN_MAX_SHORT:
            runs.append({"x": x, "y": y, "w": w, "h": h,
                         "horizontal": horizontal})
    return runs


def find_doors(walls, exits):
    """Gaps of 40-120 px between consecutive collinear wall runs."""
    doors = []
    for horizontal in (True, False):
        runs = [r for r in walls if r["horizontal"] is horizontal]
        # Group runs on the same wall line (same y / same x).
        lines = []
        for run in sorted(runs, key=lambda r: r["y" if horizontal else "x"]):
            pos = run["y" if horizontal else "x"]
            if lines and abs(pos - lines[-1][0]) <= LINE_TOL:
                lines[-1][1].append(run)
            else:
                lines.append([pos, [run]])
        for _pos, line in lines:
            axis = "x" if horizontal else "y"
            line.sort(key=lambda r: r[axis])
            for prev, nxt in zip(line, line[1:]):
                gap_start = prev[axis] + prev["w" if horizontal else "h"]
                gap_end = nxt[axis]
                width = gap_end - gap_start
                if not (DOOR_GAP_MIN <= width <= DOOR_GAP_MAX):
                    continue
                cx = gap_start + width / 2
                if horizontal:
                    cy = prev["y"] + prev["h"] / 2
                else:
                    cy = cx
                    cx = prev["x"] + prev["w"] / 2
                # A gap whose end meets a perpendicular wall is a junction
                # (e.g. the corridor crossing a divider), not a door.
                perp = [r for r in walls if r["horizontal"] is not horizontal]
                if horizontal:
                    ends = [(gap_start, cy), (gap_end, cy)]
                else:
                    ends = [(cx, gap_start), (cx, gap_end)]
                if any(p["x"] - LINE_TOL <= ex <= p["x"] + p["w"] + LINE_TOL
                       and p["y"] - LINE_TOL <= ey <= p["y"] + p["h"] + LINE_TOL
                       for ex, ey in ends for p in perp):
                    continue
                # A gap at an exit opening is an exit, not a door.
                if any(e["x"] - EXIT_MARGIN <= cx <= e["x"] + e["w"] + EXIT_MARGIN
                       and e["y"] - EXIT_MARGIN <= cy <= e["y"] + e["h"] + EXIT_MARGIN
                       for e in exits):
                    continue
                doors.append({"x": int(round(cx)), "y": int(round(cy)),
                              "width": int(width)})
    return doors


def main():
    if not os.path.isfile(IMAGE_PATH):
        sys.exit("Missing input image: %s" % IMAGE_PATH)
    img = cv2.imread(IMAGE_PATH, cv2.IMREAD_COLOR)
    if img is None:
        sys.exit("Could not decode image: %s" % IMAGE_PATH)
    height, width = img.shape[:2]

    # --- 1. Walls: near-black pixels -> H/V runs -> rectangles. ---
    dark = (img.max(axis=2) < DARK_MAX).astype(np.uint8) * 255
    walls = wall_runs(dark, horizontal=True) + wall_runs(dark, horizontal=False)
    walls.sort(key=lambda r: (r["y"], r["x"]))

    # --- 2. Exits: green pixels -> large components -> boxes. ---
    b, g, r = cv2.split(img)
    green = ((r < EXIT_R_MAX) & (b < EXIT_B_MAX)
             & (g > EXIT_G_MIN)).astype(np.uint8) * 255
    count, _labels, stats, _centroids = cv2.connectedComponentsWithStats(
        green, connectivity=8)
    exits = []
    for i in range(1, count):
        x, y, w, h, area = (int(v) for v in stats[i])
        if area >= EXIT_MIN_AREA:
            exits.append({"x": x, "y": y, "w": w, "h": h})
    exits.sort(key=lambda e: (e["x"], e["y"]))

    # --- 3. Doors: 40-120 px gaps in wall lines (minus exit openings). ---
    doors = find_doors(walls, exits)
    doors.sort(key=lambda d: (d["y"], d["x"]))

    layout = {
        "image_size": [width, height],
        "walls": [{k: run[k] for k in ("x", "y", "w", "h")} for run in walls],
        "exits": exits,
        "doors": doors,
    }
    with open(LAYOUT_PATH, "w", encoding="utf-8") as f:
        json.dump(layout, f, indent=2)
        f.write("\n")
    print("Saved %s: %d wall(s), %d exit(s), %d door(s)."
          % (LAYOUT_PATH, len(walls), len(exits), len(doors)))

    # --- Check image: walls red, doors blue, exits green (BGR). ---
    check = img.copy()
    for run in walls:
        cv2.rectangle(check, (run["x"], run["y"]),
                      (run["x"] + run["w"] - 1, run["y"] + run["h"] - 1),
                      (0, 0, 255), 2)
    for i, door in enumerate(doors):
        center = (door["x"], door["y"])
        cv2.circle(check, center, max(door["width"] // 2, 2), (255, 0, 0), 2)
        cv2.circle(check, center, 2, (255, 0, 0), -1)
        cv2.putText(check, "door %d" % i, (door["x"] + door["width"] // 2 + 3,
                                           door["y"] - 6),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 0, 0), 1)
    for i, ex in enumerate(exits):
        cv2.rectangle(check, (ex["x"], ex["y"]),
                      (ex["x"] + ex["w"] - 1, ex["y"] + ex["h"] - 1),
                      (0, 255, 0), 2)
        cv2.putText(check, "exit %d" % i, (ex["x"], ex["y"] - 6),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 1)
    cv2.imwrite(CHECK_PATH, check)
    print("Saved %s for visual verification." % CHECK_PATH)


if __name__ == "__main__":
    main()
