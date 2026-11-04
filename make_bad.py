"""Make a bad baseline: one exit only, narrow doors.

Usage: python make_bad.py [layout.json] [layout_bad.json]

Keeps only the left exit, closes the right corridor end with a wall,
and narrows every door to 36 px (0.9 m). Same schema as layout.json.
"""

import json
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else "layout.json"
DST = sys.argv[2] if len(sys.argv) > 2 else "layout_bad.json"

layout = json.load(open(SRC, encoding="utf-8"))
exits = sorted(layout["exits"], key=lambda e: e["x"])
layout["exits"] = exits[:1]
removed = exits[-1]
# Wall plug aligned with the side walls, spanning the removed exit gap.
layout["walls"].append({
    "x": removed["x"] + 7,
    "y": removed["y"] - 3,
    "w": 8,
    "h": removed["h"] + 6,
})
for door in layout["doors"]:
    door["width"] = 36
with open(DST, "w", encoding="utf-8") as f:
    json.dump(layout, f, indent=2)
    f.write("\n")
print("Saved %s: 1 exit, %d walls, %d doors at 36 px."
      % (DST, len(layout["walls"]), len(layout["doors"])))
