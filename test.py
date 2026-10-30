"""Test Meta Model API: Muse Spark (text) + SAM 3.1 (floorplan segmentation).

Usage:
    set MODEL_API_KEY=your-key   (Windows: $env:MODEL_API_KEY="your-key")
    python test.py

Needs Pillow and the official meta-sam-parser package:
    pip install pillow meta-sam-parser

Request formats below follow the Meta docs exactly:
- Responses API: POST https://api.meta.ai/v1/responses with
  Authorization: Bearer $MODEL_API_KEY and JSON body
  {"model": "muse-spark-1.3", "input": "<string>"}.
  https://dev.meta.ai/docs/protocols/responses
- SAM 3.1: same endpoint, model "sam-3.1", input is one user message
  carrying input_text (ONE noun-phrase concept per request) plus
  input_image (public URL or base64 data URL). Docs: "One concept per
  request ... To segment several concepts, send one request each."
  Streaming is NOT required: "A non-streaming request (stream omitted
  or false) runs the same segmentation and returns the complete result
  in one response, in the same format."
  https://dev.meta.ai/docs/sam/segmenting
- Output: special-token text in one output_text lane, one line per
  frame; a single image is one <0f> line. An empty output_text is a
  valid "zero matches" result, not an error.
  https://dev.meta.ai/docs/sam/reading-segmentation
- Mask payloads are opaque base85; they are decoded ONLY with the
  official meta_sam_parser package, never by hand (per the SAM cookbook:
  "The example code never parses the model's text grammar itself").
  Each mask raster covers its record's box (official renderer source:
  "The raster covers the record's box"), so the overlay scales every
  mask to its API-returned box. Nothing is drawn except API geometry.
"""

import base64
import json
import mimetypes
import os
import sys
import urllib.error
import urllib.request

from PIL import Image, ImageDraw

try:
    from meta_sam_parser import (
        CompletedOutcome,
        decode_mask_to_raster,
        image_segmentation_format,
    )
except ImportError:  # Clear failure later; Spark call still works without it.
    CompletedOutcome = None
    decode_mask_to_raster = None
    image_segmentation_format = None

RESPONSES_URL = "https://api.meta.ai/v1/responses"
SPARK_MODEL = "muse-spark-1.3"
SAM_MODEL = "sam-3.1"

IMAGE_PATH = "floorplan.png"
OVERLAY_PATH = "overlay.png"

# One short noun phrase per request, per the SAM prompting docs.
# (concept, RGB color used for its boxes/masks in overlay.png)
CONCEPTS = (
    ("wall", (255, 0, 0)),
    ("door", (0, 0, 255)),
    ("exit", (0, 180, 0)),
)

MASK_FILL_ALPHA = 90  # ~0.35 like the official renderer's default fill opacity
BOX_WIDTH = 3


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


def parse_image_output(output_text):
    """Parse one SAM output_text line with the official parser.

    Returns the parser's ImageSegmentationResult (records + diagnostics).
    Raises SystemExit with an install hint if meta_sam_parser is missing:
    mask payloads are opaque and must not be decoded by hand.
    """
    if image_segmentation_format is None:
        sys.exit(
            "meta-sam-parser is not installed, so SAM masks cannot be "
            "decoded (the docs forbid parsing mask payloads by hand).\n"
            "Install it with: pip install meta-sam-parser"
        )
    parser = image_segmentation_format().create_parser()
    for _snapshot in parser.push(output_text):
        pass
    return parser.finish(CompletedOutcome()).result


def draw_overlay(base_image, findings):
    """Draw ONLY API-returned geometry: mask fills + box outlines per concept.

    findings: list of (concept, color, result) where result is the parser's
    ImageSegmentationResult. Each mask raster is scaled to its own record's
    box, mirroring the official renderer's placement rule.
    """
    overlay = base_image.convert("RGBA")
    masks_layer = Image.new("RGBA", overlay.size, (0, 0, 0, 0))

    for concept, color, result in findings:
        boxes = {}
        for record in result.records:
            if record.kind == "box":
                boxes[record.object_id] = record
        for record in result.records:
            if record.kind != "mask":
                continue
            box = boxes.get(record.object_id)
            if box is None:
                continue
            raster = decode_mask_to_raster(record.mask)
            w, h = record.mask.width, record.mask.height
            mask_img = Image.new("L", (w, h))
            mask_img.putdata([255 if v else 0 for v in raster])
            bw, bh = box.right - box.left, box.bottom - box.top
            if bw <= 0 or bh <= 0:
                continue
            mask_img = mask_img.resize((bw, bh), Image.NEAREST)
            fill = Image.new("RGBA", overlay.size, (0, 0, 0, 0))
            fill.paste(Image.new("RGBA", (bw, bh), color + (MASK_FILL_ALPHA,)),
                       (box.left, box.top), mask_img)
            masks_layer = Image.alpha_composite(masks_layer, fill)
        overlay = Image.alpha_composite(overlay, masks_layer)
        masks_layer = Image.new("RGBA", overlay.size, (0, 0, 0, 0))
        draw = ImageDraw.Draw(overlay)
        for object_id, box in sorted(boxes.items()):
            draw.rectangle([box.left, box.top, box.right - 1, box.bottom - 1],
                           outline=color, width=BOX_WIDTH)
            label = "%s %s" % (concept, object_id)
            lx, ly = box.left + 2, box.top - 12
            if ly < 0:
                ly = box.top + 2
            draw.text((lx, ly), label, fill=color)

    return overlay.convert("RGB")


def main():
    api_key = os.environ.get("MODEL_API_KEY")
    if not api_key:
        sys.exit("Missing MODEL_API_KEY environment variable.")

    # --- Call 1: Muse Spark, minimal string input (Responses API basic usage). ---
    spark_body = {"model": SPARK_MODEL, "input": "Reply with the word: ready"}
    print("== Muse Spark request ==", json.dumps(spark_body))
    spark_resp = post_responses(api_key, spark_body)
    print("Spark output:", output_text_of(spark_resp) or spark_resp)

    # --- Calls 2-4: SAM 3.1, one concept per request, non-streaming. ---
    if not os.path.isfile(IMAGE_PATH):
        sys.exit("Missing input image: %s" % IMAGE_PATH)
    base = Image.open(IMAGE_PATH)
    print("== SAM 3.1: %s (%dx%d), %d one-concept requests =="
          % (IMAGE_PATH, base.width, base.height, len(CONCEPTS)))
    data_url = image_to_data_url(IMAGE_PATH)

    findings = []
    for concept, color in CONCEPTS:
        sam_body = {
            "model": SAM_MODEL,
            "input": [
                {
                    "type": "message",
                    "role": "user",
                    "content": [
                        {"type": "input_text", "text": concept},
                        {"type": "input_image", "image_url": data_url},
                    ],
                }
            ],
        }
        print("--- concept %r ---" % concept)
        sam_resp = post_responses(api_key, sam_body)
        raw = output_text_of(sam_resp)
        print("Raw output_text (%d chars):" % len(raw))
        print(raw if raw else "(empty: zero matches, a normal result)")
        result = parse_image_output(raw + ("\n" if raw else ""))
        boxes = [r for r in result.records if r.kind == "box"]
        masks = [r for r in result.records if r.kind == "mask"]
        print("Parsed: %d box(es), %d mask(s)" % (len(boxes), len(masks)))
        for record in boxes:
            print("  box id=%s left=%d top=%d right=%d bottom=%d"
                  % (record.object_id, record.left, record.top,
                     record.right, record.bottom))
        for item in result.diagnostics:
            print("  diagnostic %s %s line %s: %s"
                  % (item.severity, item.code, item.line, item.message))
        findings.append((concept, color, result))

    overlay = draw_overlay(base, findings)
    overlay.save(OVERLAY_PATH)
    total_boxes = sum(1 for _, _, r in findings
                      for rec in r.records if rec.kind == "box")
    print("Saved %s with %d API-returned box(es) across %d concept(s)."
          % (OVERLAY_PATH, total_boxes, len(findings)))


if __name__ == "__main__":
    main()
