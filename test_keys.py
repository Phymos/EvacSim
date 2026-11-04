"""Regression test: EvacManager action keys must not collide with camera keys.

Usage:
    python test_keys.py

Offline check over the Unity sources: every `kb.<x>Key.wasPressedThisFrame`
binding in EvacManager.cs (mode/layout actions) must be disjoint from every
`kb.<x>Key.isPressed` binding in LayoutLoader.cs (camera movement), so one
keypress can never both steer the camera and trigger an action (this exact
overlap once made Key A restart the sim into auto mode while panning).
Exits nonzero with the collision if any overlap is found.
"""

import os
import re
import sys

MANAGER = os.path.join("unity", "EvacSim", "Assets", "Scripts", "EvacManager.cs")
LOADER = os.path.join("unity", "EvacSim", "Assets", "Scripts", "LayoutLoader.cs")


def keys_bound(path, prop):
    """All `<x>Key` names used as kb.<x>Key.<prop> in a C# file."""
    found = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            for m in re.finditer(r"kb\.(\w+Key)\." + prop + r"\b", line):
                found.add(m.group(1))
    return found


def main():
    for path in (MANAGER, LOADER):
        if not os.path.isfile(path):
            sys.exit("Missing source file: %s" % path)
    actions = keys_bound(MANAGER, "wasPressedThisFrame")
    camera = keys_bound(LOADER, "isPressed")
    clash = sorted(actions & camera)
    if clash:
        sys.exit("Key collision (action + camera): %s" % ", ".join(clash))
    print("OK: %d action key(s) %s disjoint from %d camera key(s)."
          % (len(actions), sorted(actions), len(camera)))


if __name__ == "__main__":
    main()
