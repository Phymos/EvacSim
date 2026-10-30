"""Small test for Meta Model API: Muse Spark (text) + SAM 3.1 (image seg).

Usage:
    set MODEL_API_KEY=your-key   (Windows)
    python test_model_api.py [image-path-or-url] [concept]

Defaults to a docs-example image (halved avocado) with concept "avocado".
Needs only the Python standard library; no pip install required.
"""

import base64
import json
import mimetypes
import os
import sys
import urllib.request

# Base URL and endpoint shared by all Model API Responses calls.
BASE_URL = "https://api.meta.ai"
RESPONSES_URL = BASE_URL + "/v1/responses"

# Latest Muse Spark checkpoint seen in the docs; 1.1/1.2 also exist.
SPARK_MODEL = "muse-spark-1.3"
# Segmentation model id per the Model API model list.
SAM_MODEL = "sam-3.1"

# Public image used in Meta's own docs example (avocado photo).
DEFAULT_IMAGE_URL = "https://upload.wikimedia.org/wikipedia/commons/e/eb/Halved_Avocado.jpg"
DEFAULT_CONCEPT = "avocado"


def post_responses(api_key, body):
    """POST one JSON body to /v1/responses, return parsed JSON."""
    req = urllib.request.Request(
        RESPONSES_URL,
        data=json.dumps(body).encode("utf-8"),
        headers={
            # Docs: auth is always Bearer $MODEL_API_KEY.
            "Authorization": "Bearer " + api_key,
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read().decode("utf-8"))


def image_to_url(source):
    """Pass through http(s) URLs; encode a local file as a data: URL."""
    if source.startswith(("http://", "https://", "data:")):
        return source
    mime, _ = mimetypes.guess_type(source)
    with open(source, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    return "data:%s;base64,%s" % (mime or "image/jpeg", b64)


def output_text_of(response):
    """Pull concatenated output_text from a Responses payload."""
    texts = []
    for item in response.get("output", []):
        # Message items carry content parts; one part holds output_text.
        for part in item.get("content", []) or []:
            if isinstance(part, dict) and "text" in part:
                texts.append(part["text"])
        # Some SDKs/payloads flatten it to a top-level output_text field.
        if isinstance(item, dict) and "text" in item and not item.get("content"):
            texts.append(item["text"])
    # Convenience fallback some servers include at top level.
    if not texts and isinstance(response.get("output_text"), str):
        texts.append(response["output_text"])
    return "\n".join(texts)


def main():
    # Key comes only from the environment, never hardcoded.
    api_key = os.environ.get("MODEL_API_KEY")
    if not api_key:
        sys.exit("Missing MODEL_API_KEY environment variable.")

    image_src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_IMAGE_URL
    concept = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_CONCEPT

    # --- Call 1: Muse Spark, minimal string input. ---
    spark_body = {"model": SPARK_MODEL, "input": "Reply with the word: ready"}
    print("== Muse Spark request ==", json.dumps(spark_body)[:120], "...")
    spark_resp = post_responses(api_key, spark_body)
    print("Spark output:", output_text_of(spark_resp) or spark_resp)

    # --- Call 2: SAM 3.1, noun phrase + image in one user message. ---
    sam_body = {
        "model": SAM_MODEL,
        "input": [
            {
                "type": "message",
                "role": "user",
                "content": [
                    # What to segment: a short noun phrase per the SAM docs.
                    {"type": "input_text", "text": concept},
                    # Public URL or base64 data: URL (local files encoded above).
                    {"type": "input_image", "image_url": image_to_url(image_src)},
                ],
            }
        ],
    }
    print("== SAM 3.1 request == model=sam-3.1 concept=%r" % concept)
    sam_resp = post_responses(api_key, sam_body)
    raw = output_text_of(sam_resp)
    # Raw SAM output is special-token text (one line per frame); decode
    # boxes/masks with the official meta-sam-parser package, not by hand.
    print("SAM raw output_text:")
    print(raw if raw else json.dumps(sam_resp, indent=2)[:2000])


if __name__ == "__main__":
    main()
