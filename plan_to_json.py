"""Convert floorplan.png to JSON with Muse Spark (Responses API + vision).

Usage:
    set MODEL_API_KEY=your-key   (Windows: $env:MODEL_API_KEY="your-key")
    python plan_to_json.py

Sends floorplan.png to Muse Spark, gets back walls/doors/exits, saves
pixel coordinates as layout.json, and draws them on layout_check.png
for visual verification.

Request format follows the Meta docs exactly:
- Responses API image input: one user message with an input_text block
  plus an input_image block whose image_url is a plain string (public
  URL or base64 data: URL). Images only in user messages.
  https://dev.meta.ai/docs/image-understanding
- Perception grounding: coordinates are requested on the documented
  normalized 0-1000 grid ((0,0) top-left, x right, y down), then
  converted client-side with the docs' to_pixels helper
  (round(v * size / 1000)) using the exact sent-image dimensions.
  Out-of-range edge values are clamped to the image bounds.
  https://dev.meta.ai/docs/image-understanding#perception-grounding
- Structured output on the Responses API: text.format = {"type":
  "json_schema", "name": ..., "schema": ...} so the reply is
  guaranteed to match the schema (strict omitted = default false;
  output is still schema-constrained).
  https://dev.meta.ai/docs/structured-output
  https://dev.meta.ai/docs/api-reference/responses/schemas

Judgment call (docs are silent here): door/exit "width" is a single
orientation-free number, so its normalized value is converted to pixels
with the horizontal scale (round(w * image_width / 1000)), and
layout_check.png draws it as a circle of that diameter centered on the
position. Judge it on the check image.
"""

import base64
import json
import mimetypes
import os
import sys
import urllib.error
import urllib.request

from PIL import Image, ImageDraw

RESPONSES_URL = "https://api.meta.ai/v1/responses"
SPARK_MODEL = "muse-spark-1.3"

IMAGE_PATH = "floorplan.png"
LAYOUT_PATH = "layout.json"
CHECK_PATH = "layout_check.png"

GRID_MAX = 1000

WALL_COLOR = (255, 0, 0)
DOOR_COLOR = (0, 0, 255)
EXIT_COLOR = (0, 180, 0)

# JSON Schema for text.format: exactly the three requested keys, using the
# documented normalized 0-1000 integer grid. Positions convert to pixels
# client-side; layout.json itself holds pixels only.
LAYOUT_SCHEMA = {
    "type": "object",
    "properties": {
        "walls": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "start": {"$ref": "#/$defs/point"},
                    "end": {"$ref": "#/$defs/point"},
                },
                "required": ["start", "end"],
                "additionalProperties": False,
            },
        },
        "doors": {
            "type": "array",
            "items": {"$ref": "#/$defs/opening"},
        },
        "exits": {
            "type": "array",
            "items": {"$ref": "#/$defs/opening"},
        },
    },
    "required": ["walls", "doors", "exits"],
    "additionalProperties": False,
    "$defs": {
        "point": {
            "type": "array",
            "items": {"type": "integer"},
            "minItems": 2,
            "maxItems": 2,
        },
        "opening": {
            "type": "object",
            "properties": {
                "position": {"$ref": "#/$defs/point"},
                "width": {"type": "integer"},
            },
            "required": ["position", "width"],
            "additionalProperties": False,
        },
    },
}

# Direct grounding instruction in the user message next to the image, with a
# minimal system side (no instructions field), per the grounding docs.
PROMPT = (
    "You are an object grounding expert. This is a floor plan. Locate ALL "
    "walls, doors, and exits. Do not miss any.\n"
    "Report every coordinate as an integer on the normalized 0-1000 grid: "
    "(0,0) is the top-left corner, x increases left to right, y increases "
    "top to bottom.\n"
    "walls: one entry per straight wall run between corners and openings; "
    "each has start [x, y] and end [x, y] endpoints of the wall centerline.\n"
    "doors: one entry per door opening (gap in a wall); position [x, y] is "
    "the gap center and width is the gap extent along the wall.\n"
    "exits: one entry per marked exit; position [x, y] is the exit center "
    "and width is the exit extent along the wall.\n"
    "If you cannot find an object, omit it. Return ONLY the JSON object."
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


def image_to_data_url(path):
    """Encode a local image file as a base64 data: URL (docs-allowed form)."""
    mime, _ = mimetypes.guess_type(path)
    with open(path, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    return "data:%s;base64,%s" % (mime or "image/png", b64)


def to_pixels(value_norm, size):
    """Docs' helper: normalized 0-1000 value -> pixels, clamped to bounds."""
    clamped = min(max(value_norm, 0), GRID_MAX)
    return min(max(round(clamped * size / GRID_MAX), 0), size)


def to_layout_pixels(raw, width, height):
    """Convert the model's normalized layout to pixel layout.json content."""
    layout = {"walls": [], "doors": [], "exits": []}
    for wall in raw.get("walls", []):
        (x1, y1), (x2, y2) = wall["start"], wall["end"]
        layout["walls"].append({
            "start": [to_pixels(x1, width), to_pixels(y1, height)],
            "end": [to_pixels(x2, width), to_pixels(y2, height)],
        })
    for key in ("doors", "exits"):
        for opening in raw.get(key, []):
            (x, y), w = opening["position"], opening["width"]
            layout[key].append({
                "position": [to_pixels(x, width), to_pixels(y, height)],
                # Orientation-free scalar: horizontal scale (see docstring).
                "width": max(round(max(w, 0) * width / GRID_MAX), 0),
            })
    return layout


def draw_check(base_image, layout):
    """Draw ONLY the returned layout on a copy of the floor plan."""
    img = base_image.convert("RGB")
    draw = ImageDraw.Draw(img)
    for wall in layout["walls"]:
        draw.line([tuple(wall["start"]), tuple(wall["end"])],
                  fill=WALL_COLOR, width=3)
    for key, color in (("doors", DOOR_COLOR), ("exits", EXIT_COLOR)):
        for i, opening in enumerate(layout[key]):
            (x, y), w = opening["position"], opening["width"]
            r = w / 2
            draw.ellipse([x - r, y - r, x + r, y + r], outline=color, width=3)
            draw.ellipse([x - 2, y - 2, x + 2, y + 2], fill=color)
            draw.text((x + r + 2, y - 8), "%s %d" % (key[:-1], i), fill=color)
    return img


def main():
    api_key = os.environ.get("MODEL_API_KEY")
    if not api_key:
        sys.exit("Missing MODEL_API_KEY environment variable.")
    if not os.path.isfile(IMAGE_PATH):
        sys.exit("Missing input image: %s" % IMAGE_PATH)

    base = Image.open(IMAGE_PATH)
    width, height = base.size
    print("Image: %s (%dx%d)" % (IMAGE_PATH, width, height))

    body = {
        "model": SPARK_MODEL,
        "input": [
            {
                "type": "message",
                "role": "user",
                "content": [
                    {"type": "input_text", "text": PROMPT},
                    {"type": "input_image",
                     "image_url": image_to_data_url(IMAGE_PATH)},
                ],
            }
        ],
        "text": {
            "format": {
                "type": "json_schema",
                "name": "floorplan_layout",
                "schema": LAYOUT_SCHEMA,
            }
        },
    }
    print("== Muse Spark request == model=%s image=%s (data URL, %d bytes) =="
          % (SPARK_MODEL, IMAGE_PATH, os.path.getsize(IMAGE_PATH)))
    response = post_responses(api_key, body)
    raw_text = output_text_of(response)
    print("Raw reply (%d chars):" % len(raw_text))
    print(raw_text)

    try:
        raw = json.loads(raw_text)
    except json.JSONDecodeError as exc:
        sys.exit("Reply was not valid JSON (%s); layout.json not written."
                 % exc)

    layout = to_layout_pixels(raw, width, height)
    with open(LAYOUT_PATH, "w", encoding="utf-8") as f:
        json.dump(layout, f, indent=2)
        f.write("\n")
    print("Saved %s: %d wall(s), %d door(s), %d exit(s)."
          % (LAYOUT_PATH, len(layout["walls"]),
             len(layout["doors"]), len(layout["exits"])))

    draw_check(base, layout).save(CHECK_PATH)
    print("Saved %s for visual verification." % CHECK_PATH)


if __name__ == "__main__":
    main()
