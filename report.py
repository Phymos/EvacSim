"""Generate hackathon report assets from a loop.py campaign.

Usage:
    python report.py [history.json] [out_dir]   (defaults: history.json report)

Reads history.json (+ the layout files it names) and writes into out_dir:
    progress.png    - evacuation time per round (cv2 bar chart, lower better)
    comparison.png  - round-0 vs best layout side by side (needs floorplan.png)
    REPORT.md       - headline number, results table, and image links
"""

import json
import os
import sys

import cv2

import optimize as opt

WHITE = (255, 255, 255)
BLACK = (0, 0, 0)
GRAY = (90, 90, 90)
GREEN = (0, 180, 0)
ORANGE = (0, 140, 255)
FONT = cv2.FONT_HERSHEY_SIMPLEX


def draw_progress(items, path):
    """Bar chart of [(label, time_s, kind)]; kind: round|best|confirm."""
    W, H = 920, 520
    import numpy as np
    img = np.full((H, W, 3), 255, np.uint8)
    L, R, T, B = 80, 30, 70, 80
    vmax = max(t for _, t, _ in items) * 1.18 or 1.0
    n = len(items)
    slot = (W - L - R) / n
    colors = {"round": GRAY, "best": GREEN, "confirm": ORANGE}
    for i in range(6):  # gridlines + y labels
        v = vmax * i / 5
        y = int(T + (H - T - B) * (1 - i / 5))
        cv2.line(img, (L, y), (W - R, y), (225, 225, 225), 1)
        cv2.putText(img, "%.0f" % v, (L - 55, y + 7), FONT, 0.6, GRAY, 1)
    for j, (label, t, kind) in enumerate(items):
        x0 = int(L + slot * j + slot * 0.2)
        x1 = int(L + slot * (j + 1) - slot * 0.2)
        y1 = H - B
        y0 = int(T + (H - T - B) * (1 - t / vmax))
        cv2.rectangle(img, (x0, y0), (x1, y1), colors[kind], -1)
        cv2.putText(img, "%.1f" % t, (x0, y0 - 10), FONT, 0.7, BLACK, 2)
        cv2.putText(img, label, (x0, y1 + 30), FONT, 0.7, BLACK, 2)
    cv2.putText(img, "Evacuation time per round (s, lower is better)",
                (L, 40), FONT, 0.9, BLACK, 2)
    cv2.imwrite(path, img)
    print("Saved %s." % path)


def highlights_for(layout, changes):
    """Rebuild draw_check highlights from a history entry's changes."""
    out = []
    add_n = 0
    for c in changes:
        try:
            kind, target = c["type"], c["target_id"]
        except KeyError:
            continue
        if kind == "widen_door" and target.startswith("door "):
            out.append(("door", int(target[5:])))
        elif kind == "move_wall" and target.startswith("wall "):
            out.append(("wall", int(target[5:])))
        elif kind == "add_exit":
            add_n += 1
    for k in range(add_n):  # new exits were appended in order
        out.append(("exit", len(layout["exits"]) - add_n + k))
    return out


def tag(img, text):
    bar = cv2.copyMakeBorder(img, 44, 0, 0, 0, cv2.BORDER_CONSTANT, value=BLACK)
    cv2.putText(bar, text, (12, 30), FONT, 0.8, WHITE, 2)
    return bar


def draw_comparison(base, l0, hl0, lab0, lB, hlB, labB, path):
    left = tag(opt.draw_check(base, l0, hl0), lab0)
    right = tag(opt.draw_check(base, lB, hlB), labB)
    cv2.imwrite(path, cv2.hconcat([left, right]))
    print("Saved %s." % path)


def main():
    hist_path = sys.argv[1] if len(sys.argv) > 1 else "history.json"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else "report"
    if not os.path.isfile(hist_path):
        sys.exit("Missing %s (run loop.py first)." % hist_path)
    history = json.load(open(hist_path, encoding="utf-8"))
    rounds = [e for e in history if not isinstance(e["round"], str)]
    confirms = [e for e in history if isinstance(e["round"], str)]
    if not rounds:
        sys.exit("No rounds in %s." % hist_path)
    os.makedirs(out_dir, exist_ok=True)

    best = min(rounds, key=lambda e: e["time_s"])
    t0 = rounds[0]["time_s"]
    pct = (t0 - best["time_s"]) / t0 * 100 if t0 else 0

    items = [("R%d" % e["round"], e["time_s"],
              "best" if e["round"] == best["round"] else "round")
             for e in rounds]
    items += [("C", e["time_s"], "confirm") for e in confirms]
    draw_progress(items, os.path.join(out_dir, "progress.png"))

    comp_name = None
    base = None
    if os.path.isfile(opt.IMAGE_PATH):
        base = cv2.imread(opt.IMAGE_PATH, cv2.IMREAD_COLOR)
    if base is None:
        print("No %s; skipping comparison.png." % opt.IMAGE_PATH)
    else:
        try:
            l0 = json.load(open(rounds[0]["layout"], encoding="utf-8"))
            lB = json.load(open(best["layout"], encoding="utf-8"))
        except OSError as exc:
            print("Skipping comparison.png (%s)." % exc)
        else:
            comp_name = "comparison.png"
            draw_comparison(
                base, l0, [], "ROUND 0 [%s]: %.1f s"
                % (rounds[0]["layout"], t0),
                lB, highlights_for(lB, best["changes"]),
                "BEST ROUND %d [%s]: %.1f s (yellow = AI changes)"
                % (best["round"], best["layout"], best["time_s"]),
                os.path.join(out_dir, comp_name))

    seeds = sorted({s for e in rounds for s in e.get("seed_times", {})})
    lines = ["# EvacLoop campaign report", "",
             "**%.1f s -> %.1f s (-%.0f%%)** over %d round(s)%s." % (
                 t0, best["time_s"], pct, len(rounds),
                 " (best: round %d)" % best["round"]
                 if best["round"] != 0 else ""),
             ""]
    if confirms:
        c = confirms[-1]
        lines.append("Held-out confirmation: **%.1f s** (seed %s)." % (
            c["time_s"], ", ".join(map(str, c.get("seed_times", {})))))
        lines.append("")
    lines += ["![progress](progress.png)", "",
              "| round | layout | time_s | vs r0 | cost | changes |",
              "| ----- | ------ | ------ | ----- | ---- | ------- |"]
    for e in history:
        d = (e["time_s"] - t0) / t0 * 100 if t0 else 0
        chg = "; ".join("%s %s" % (x["type"], x["target_id"])
                        for x in e["changes"]) or "baseline"
        lines.append("| %s | %s | %.1f | %+5.1f%% | %s | %s |" % (
            e["round"], e["layout"], e["time_s"], d,
            e["cost"] if e.get("cost") is not None else "-", chg))
    lines += [""]
    if comp_name:
        lines += ["![comparison](%s)" % comp_name, "",
                  "_Left: round 0 baseline. Right: best layout, "
                  "AI changes in yellow._", ""]
    if any(e.get("timed_out") for e in history):
        lines.append("_Note: at least one run hit Unity maxRunTime._")
        lines.append("")
    lines.append("Seeds per round: %s. Score = mean time."
                 % (", ".join(map(str, seeds)) or "?"))
    with open(os.path.join(out_dir, "REPORT.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("Saved %s." % os.path.join(out_dir, "REPORT.md"))
    print("Headline: %.1f s -> %.1f s (-%.0f%%)."
          % (t0, best["time_s"], pct))


if __name__ == "__main__":
    main()
