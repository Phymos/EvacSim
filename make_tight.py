"""Narrow every door to ~40 px for a congested baseline.

Usage: python make_tight.py [layout.json] [layout_tight.json]
"""

import json
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else "layout.json"
DST = sys.argv[2] if len(sys.argv) > 2 else "layout_tight.json"

layout = json.load(open(SRC, encoding="utf-8"))
for door in layout["doors"]:
    door["width"] = 40
with open(DST, "w", encoding="utf-8") as f:
    json.dump(layout, f, indent=2)
    f.write("\n")
print("Saved %s with %d doors at 40 px." % (DST, len(layout["doors"])))
