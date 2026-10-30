"""Propose congestion-reducing layout changes with Muse Spark (no vision).

Usage:
    set MODEL_API_KEY=your-key   (Windows: $env:MODEL_API_KEY="your-key")
    python optimize.py

Reads layout.json (pixel geometry, origin top-left) and results.json
(hand-made evacuation summary), sends both to Muse Spark over the
Responses API, and requires back ONLY JSON:
{"changes": [{"type": "widen_door|add_exit|move_wall",
"target_id": "...", "new_geometry": {...}, "reason": "..."}],
"expected_effect": "..."}.
Validates the reply in code, applies at most 3 changes to produce
layout_v2.json, and draws layout_v2_check.png for visual verification.

Request format (verified against the Meta docs earlier this session):
- Text-only Responses request: POST https://api.meta.ai/v1/responses,
  Bearer $MODEL_API_KEY, {"model": "muse-spark-1.3", "input": "<string>"}.
  https://dev.meta.ai/docs/protocols/responses
- Structured output: text.format = {"type": "json_schema", "name": ...,
  "schema": ...}; the reply is guaranteed to match the schema.
  https://dev.meta.ai/docs/structured-output
  https://dev.meta.ai/docs/api-reference/responses/schemas

IDs: doors[i] is "door i", exits[i] is "exit i", walls[i] is "wall i"
(in layout.json order). Scale: 40 px = 1 m, so the 0.9 m minimum door
width is 36 px. Constraints enforced in code: <= 3 changes, every v2
door >= 36 px, at least 2 exits, moved walls must not overlap an exit.
"""

import copy
import json
import os
import sys
import urllib.error
import urllib.request

import cv2

RESPONSES_URL = "https://api.meta.ai/v1/responses"
SPARK_MODEL = "muse-spark-1.3"

LAYOUT_PATH = "layout.json"
RESULTS_PATH = "results.json"
IMAGE_PATH = "floorplan.png"
V2_PATH = "layout_v2.json"
CHECK_PATH = "layout_v2_check.png"

PX_PER_M = 40
MIN_DOOR_PX = 36  # 0.9 m minimum door width
MIN_EXITS = 2
MAX_CHANGES = 3

CHANGE_TYPES = ("widen_door", "add_exit", "move_wall")

REPLY_SCHEMA = {
    "type": "object",
    "properties": {
        "changes": {
            "type": "array",
            "maxItems": MAX_CHANGES,
            "items": {
                "type": "object",
                "properties": {
                    "type": {"type": "string",
                             "enum": list(CHANGE_TYPES)},
                    "target_id": {"type": "string"},
                    # Shape checked per type in code (widen: {width};
                    # add/move: {x,y,w,h}).
                    "new_geometry": {"type": "object"},
                    "reason": {"type": "string"},
                },
                "required": ["type", "target_id", "new_geometry", "reason"],
                "additionalProperties": False,
            },
        },
        "expected_effect": {"type": "string"},
    },
    "required": ["changes", "expected_effect"],
    "additionalProperties": False,
}

PROMPT_TEMPLATE = (
    "You are a building evacuation optimizer. Below are LAYOUT (pixel "
    "coordinates, origin top-left, 40 px = 1 m) and SIMULATION (evacuation "
    "summary with congested zones and bottlenecks).\n"
    "IDs: doors[i] is 'door i', exits[i] is 'exit i', walls[i] is 'wall i'.\n"
    "Propose at most 3 layout changes that reduce congestion, using only:\n"
    "- widen_door: target_id 'door N', new_geometry {{\"width\": px}};\n"
    "- add_exit: target_id 'new', new_geometry {{\"x\",\"y\",\"w\",\"h\"}} px;\n"
    "- move_wall: target_id 'wall N', new_geometry {{\"x\",\"y\",\"w\",\"h\"}} px.\n"
    "Constraints: every door must be at least 36 px wide (0.9 m); keep at "
    "least 2 exits; never place a wall over an exit. "
    "Return ONLY the JSON object.\n"
    "LAYOUT:\n{layout}\nSIMULATION:\n{results}\n"
)


def post_responses(api_key, body):
    """POST one JSON body to /v1/responses, return parsed JSON."""
    req = urllib.request.Request(
        RESPONSES_URL,
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Authorization": "Bearer " + api_key,
            "Content-Type": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", "replace")
        sys.exit("API request failed: HTTP %s\n%s" % (exc.code, detail))


def output_text_of(response):
    """Pull concatenated output_text from a Responses payload."""
    texts = []
    for item in response.get("output", []):
        if not isinstance(item, dict):
            continue
        for part in item.get("content", []) or []:
            if isinstance(part, dict) and "text" in part:
                texts.append(part["text"])
        if "text" in item and not item.get("content"):
            texts.append(item["text"])
    if not texts and isinstance(response.get("output_text"), str):
        texts.append(response["output_text"])
    return "\n".join(texts)


def parse_target(target_id, kind, count):
    """'door N' -> N, validated against the layout array length."""
    prefix = kind + " "
    if not target_id.startswith(prefix):
        raise ValueError("target_id %r is not a %s id (expected %rN)"
                         % (target_id, kind, prefix))
    try:
        index = int(target_id[len(prefix):])
    except ValueError:
        raise ValueError("target_id %r has no integer index" % target_id)
    if not 0 <= index < count:
        raise ValueError("target_id %r out of range (%d %ss)"
                         % (target_id, count, kind))
    return index


def check_rect(geom, width, height, what):
    """Validate an {x,y,w,h} pixel rect inside the image."""
    if set(geom) != {"x", "y", "w", "h"}:
        raise ValueError("%s geometry needs {x,y,w,h}, got %s"
                         % (what, sorted(geom)))
    if not all(isinstance(v, int) and not isinstance(v, bool)
               for v in geom.values()):
        raise ValueError("%s geometry must be integers: %r" % (what, geom))
    x, y, w, h = (geom[k] for k in ("x", "y", "w", "h"))
    if w <= 0 or h <= 0:
        raise ValueError("%s must have positive size: %r" % (what, geom))
    if not (0 <= x and 0 <= y and x + w <= width and y + h <= height):
        raise ValueError("%s is outside the %dx%d image: %r"
                         % (what, width, height, geom))
    return geom


def overlaps(a, b):
    """True if two {x,y,w,h} rects share a positive area."""
    return (a["x"] < b["x"] + b["w"] and b["x"] < a["x"] + a["w"]
            and a["y"] < b["y"] + b["h"] and b["y"] < a["y"] + a["h"])


def validate_reply(reply, layout):
    """Validate the model's reply object; return its changes list.

    Raises ValueError with a plain explanation for anything invalid.
    """
    if not isinstance(reply, dict) or set(reply) != {"changes",
                                                    "expected_effect"}:
        raise ValueError("reply must be {changes, expected_effect}")
    changes = reply["changes"]
    if not isinstance(changes, list) or len(changes) > MAX_CHANGES:
        raise ValueError("at most %d changes allowed" % MAX_CHANGES)
    if not isinstance(reply["expected_effect"], str) \
            or not reply["expected_effect"].strip():
        raise ValueError("expected_effect must be a non-empty string")
    width, height = layout["image_size"]
    seen_targets = set()
    for change in changes:
        if set(change) != {"type", "target_id", "new_geometry", "reason"}:
            raise ValueError("bad change keys: %s" % sorted(change))
        kind, target, geom = (change["type"], change["target_id"],
                              change["new_geometry"])
        if kind not in CHANGE_TYPES:
            raise ValueError("bad change type: %r" % kind)
        if not isinstance(change["reason"], str) \
                or not change["reason"].strip():
            raise ValueError("change %r needs a non-empty reason" % target)
        if not isinstance(geom, dict):
            raise ValueError("change %r needs an object geometry" % target)
        if (kind, target) in seen_targets:
            raise ValueError("duplicate change for %r" % target)
        seen_targets.add((kind, target))
        if kind == "widen_door":
            index = parse_target(target, "door", len(layout["doors"]))
            if set(geom) != {"width"} or not isinstance(geom["width"], int) \
                    or isinstance(geom["width"], bool):
                raise ValueError("widen_door %r needs {width: int}" % target)
            old = layout["doors"][index]["width"]
            if geom["width"] < old:
                raise ValueError("widen_door %r narrows %d to %d px"
                                 % (target, old, geom["width"]))
            if geom["width"] < MIN_DOOR_PX:
                raise ValueError("widen_door %r below 0.9 m (%d px)"
                                 % (target, geom["width"]))
        elif kind == "move_wall":
            parse_target(target, "wall", len(layout["walls"]))
            check_rect(geom, width, height, "move_wall %r" % target)
            if any(overlaps(geom, ex) for ex in layout["exits"]):
                raise ValueError("move_wall %r would block an exit" % target)
        else:  # add_exit
            if target != "new":
                raise ValueError("add_exit target_id must be 'new'")
            check_rect(geom, width, height, "add_exit")
    return changes


def apply_changes(layout, changes):
    """Apply validated changes; return (v2 layout, highlight descriptors)."""
    v2 = copy.deepcopy(layout)
    highlights = []
    for change in changes:
        kind, target, geom = (change["type"], change["target_id"],
                              change["new_geometry"])
        if kind == "widen_door":
            index = parse_target(target, "door", len(v2["doors"]))
            v2["doors"][index]["width"] = geom["width"]
            highlights.append(("door", index))
        elif kind == "move_wall":
            index = parse_target(target, "wall", len(v2["walls"]))
            v2["walls"][index] = {k: geom[k] for k in ("x", "y", "w", "h")}
            highlights.append(("wall", index))
        else:
            v2["exits"].append({k: geom[k] for k in ("x", "y", "w", "h")})
            highlights.append(("exit", len(v2["exits"]) - 1))
    # Global constraint re-check on the final layout.
    if any(d["width"] < MIN_DOOR_PX for d in v2["doors"]):
        raise ValueError("result has a door narrower than 0.9 m")
    if len(v2["exits"]) < MIN_EXITS:
        raise ValueError("result keeps fewer than %d exits" % MIN_EXITS)
    return v2, highlights


def draw_check(base_bgr, layout, highlights):
    """Draw the v2 layout (walls red, doors blue, exits green, BGR).

    Changed/new elements get an extra yellow outline. Draws only data.
    """
    img = base_bgr.copy()
    changed = set(highlights)
    for i, run in enumerate(layout["walls"]):
        cv2.rectangle(img, (run["x"], run["y"]),
                      (run["x"] + run["w"] - 1, run["y"] + run["h"] - 1),
                      (0, 0, 255), 2)
        if ("wall", i) in changed:
            cv2.rectangle(img, (run["x"] - 3, run["y"] - 3),
                          (run["x"] + run["w"] + 2, run["y"] + run["h"] + 2),
                          (0, 255, 255), 2)
    for i, door in enumerate(layout["doors"]):
        center = (door["x"], door["y"])
        rad = max(door["width"] // 2, 2)
        cv2.circle(img, center, rad, (255, 0, 0), 2)
        cv2.circle(img, center, 2, (255, 0, 0), -1)
        if ("door", i) in changed:
            cv2.circle(img, center, rad + 5, (0, 255, 255), 2)
            cv2.putText(img, "widened", (center[0] + rad + 8, center[1]),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 255), 1)
    for i, ex in enumerate(layout["exits"]):
        cv2.rectangle(img, (ex["x"], ex["y"]),
                      (ex["x"] + ex["w"] - 1, ex["y"] + ex["h"] - 1),
                      (0, 255, 0), 2)
        if ("exit", i) in changed:
            cv2.rectangle(img, (ex["x"] - 3, ex["y"] - 3),
                          (ex["x"] + ex["w"] + 2, ex["y"] + ex["h"] + 2),
                          (0, 255, 255), 2)
            cv2.putText(img, "new", (ex["x"], ex["y"] - 6),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 255), 1)
    return img


def main():
    api_key = os.environ.get("MODEL_API_KEY")
    if not api_key:
        sys.exit("Missing MODEL_API_KEY environment variable.")
    for path in (LAYOUT_PATH, RESULTS_PATH, IMAGE_PATH):
        if not os.path.isfile(path):
            sys.exit("Missing input file: %s" % path)
    layout = json.load(open(LAYOUT_PATH, encoding="utf-8"))
    results = json.load(open(RESULTS_PATH, encoding="utf-8"))

    prompt = PROMPT_TEMPLATE.format(
        layout=json.dumps(layout), results=json.dumps(results))
    body = {
        "model": SPARK_MODEL,
        "input": prompt,
        "text": {
            "format": {
                "type": "json_schema",
                "name": "layout_changes",
                "schema": REPLY_SCHEMA,
            }
        },
    }
    print("== Muse Spark request == model=%s prompt=%d chars =="
          % (SPARK_MODEL, len(prompt)))
    raw_text = output_text_of(post_responses(api_key, body))
    print("Raw reply (%d chars):" % len(raw_text))
    print(raw_text)
    try:
        reply = json.loads(raw_text)
    except json.JSONDecodeError as exc:
        sys.exit("Reply was not valid JSON (%s); %s not written."
                 % (exc, V2_PATH))
    try:
        changes = validate_reply(reply, layout)
        v2, highlights = apply_changes(layout, changes)
    except ValueError as exc:
        sys.exit("Invalid proposal (%s); %s not written." % (exc, V2_PATH))

    with open(V2_PATH, "w", encoding="utf-8") as f:
        json.dump(v2, f, indent=2)
        f.write("\n")
    for change in changes:
        print("Change %s %s -> %s (%s)"
              % (change["type"], change["target_id"],
                 json.dumps(change["new_geometry"]), change["reason"]))
    print("Expected effect: %s" % reply["expected_effect"])
    print("Saved %s with %d change(s)." % (V2_PATH, len(changes)))

    base = cv2.imread(IMAGE_PATH, cv2.IMREAD_COLOR)
    cv2.imwrite(CHECK_PATH, draw_check(base, v2, highlights))
    print("Saved %s for visual verification." % CHECK_PATH)


if __name__ == "__main__":
    main()
