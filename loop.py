"""Autonomous multi-round evacuation loop (Unity runs, Spark proposes).

Usage:
    python loop.py [start_layout] [max_rounds] [seeds]
    defaults: layout_bad.json 4 42,43

Requires MODEL_API_KEY. Unity side: press Play, then O (auto mode starts
from layout_bad.json). Each round tests one layout under every seed and
scores it by mean evacuation time. Worse rounds revert to the best layout.
Invalid Spark replies are retried with the error message (3 tries); the
prompt carries history, a tabu list of changes that made it worse, and a
worst-zone tie-break plus change costs. Stops after max_rounds or two
straight rounds improving < 3%, then confirms the best layout once with
a held-out seed.
"""

import base64
import copy
import json
import os
import shutil
import sys
import time

import cv2

import optimize as opt

STREAM = os.path.join("unity", "EvacSim", "Assets", "StreamingAssets")
RESULTS = os.path.join(STREAM, "results.json")
STATUS = os.path.join(STREAM, "status.json")
RUNCFG = os.path.join(STREAM, "run_config.json")
HEATMAP = os.path.join(STREAM, "heatmap.png")
NEXT = os.path.join(STREAM, "layout_next.json")
ROUND0 = os.path.join(STREAM, "layout_bad.json")
HISTORY = "history.json"
PREV_HISTORY = "history_prev.json"
BEST = "layout_v2.json"

MAX_TRIES = 3
COSTS = {"widen_door": 1, "move_wall": 2, "add_exit": 5}

LOOP_EXTRA = (
    "Best so far: round {best} at {time:.1f} s (mean over seeds {seeds}). "
    "Propose at most 3 NEW changes to the BEST layout below (not the latest "
    "run's layout); do not repeat changes that made it worse.\n"
    "{tabu}"
    "Also minimize worst-zone congestion as a tie-break (best: {wzc:.2f}). "
    "Prefer cheaper changes when effects are similar "
    "(widen_door 1, move_wall 2, add_exit 5). "
    "Return ONLY the JSON object.\n"
)

_warned_no_base = False


def mtime(path):
    return os.path.getmtime(path) if os.path.isfile(path) else 0.0


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def load_results():
    """Read results.json, retrying through a mid-write read."""
    for _ in range(10):
        try:
            return load_json(RESULTS)
        except (json.JSONDecodeError, OSError, ValueError):
            time.sleep(0.2)
    return load_json(RESULTS)  # last try raises


def wait_for_results(last):
    while mtime(RESULTS) <= last:
        time.sleep(0.5)
    return load_results(), mtime(RESULTS)


def auto_layout_ok():
    """Last Unity run was an auto-mode file (Key A), not a manual one."""
    try:
        layout = load_json(STATUS).get("layout", "")
    except (OSError, ValueError):
        return True  # old Unity build without status.json: trust the user
    return layout == "layout_bad.json" or layout.startswith("layout_auto_")


def require_auto_layout():
    if not auto_layout_ok():
        sys.exit("Unity's last run was not an auto-mode layout: press O in "
                 "Unity for auto mode, then restart loop.py.")


def trigger_run(layout, seed):
    """Stage one Unity run: seed config first, then layout_next.json.

    Unity picks it up because layout_next.json is newer than results.json.
    """
    with open(RUNCFG, "w", encoding="utf-8") as f:
        json.dump({"seed": seed}, f)
    with open(NEXT, "w", encoding="utf-8") as f:
        json.dump(layout, f, indent=2)


def cost_of(changes):
    return sum(COSTS.get(c["type"], 0) for c in changes)


def worst_zone(results):
    zones = results.get("congestion_by_zone", {})
    return max(zones.values()) if zones else 0.0


def history_text(history):
    lines = []
    for e in history:
        if e["changes"]:
            desc = "; ".join(
                "%s %s -> %s (%s)" % (c["type"], c["target_id"],
                                      json.dumps(c["new_geometry"]),
                                      c["reason"])
                for c in e["changes"])
        else:
            desc = "baseline, no changes"
        lines.append("round %s [%s]: %.1f s | seeds %s | worst-zone %.2f | %s"
                     % (e["round"], e["layout"], e["time_s"],
                        json.dumps(e.get("seed_times", {})), e.get("wzc", 0),
                        desc))
    return "\n".join(lines)


def tabu_text(history, best_time):
    """Changes from rounds that scored worse than the current best."""
    lines = []
    for e in history:
        if isinstance(e["round"], str) or e["time_s"] <= best_time:
            continue
        for c in e["changes"]:
            lines.append("- %s %s -> %s (round %s: %.1f s, +%.1f vs best)"
                         % (c["type"], c["target_id"],
                            json.dumps(c["new_geometry"]), e["round"],
                            e["time_s"], e["time_s"] - best_time))
    if not lines:
        return ""
    return ("TABU (these made it worse, do not repeat or closely vary):\n"
            + "\n".join(lines) + "\n")


def build_body(prompt):
    """Responses body; heatmap.png attached when present, numbers primary."""
    fmt = {"format": {"type": "json_schema", "name": "layout_changes",
                      "schema": opt.REPLY_SCHEMA}}
    if not os.path.isfile(HEATMAP):
        return {"model": opt.SPARK_MODEL, "input": prompt, "text": fmt}
    with open(HEATMAP, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    prompt += ("\nAttached heatmap.png shows congestion hotspots "
               "(red = worst); treat the numeric SIMULATION as primary.\n")
    return {"model": opt.SPARK_MODEL,
            "input": [{"role": "user", "content": [
                {"type": "input_text", "text": prompt},
                {"type": "input_image",
                 "image_url": "data:image/png;base64," + b64}]}],
            "text": fmt}


def propose(api_key, base_prompt, best_layout):
    """Ask Spark for changes, retrying invalid replies with the error."""
    last_err = None
    for attempt in range(MAX_TRIES):
        prompt = base_prompt
        if last_err is not None:
            prompt = ("Previous reply was INVALID: %s\n"
                      "Fix it and return only valid JSON.\n\n" % last_err
                      + base_prompt)
        try:
            reply = json.loads(opt.output_text_of(
                opt.post_responses(api_key, build_body(prompt))))
            changes = opt.validate_reply(reply, best_layout)
            return opt.apply_changes(best_layout, changes) + (changes,)
        except (json.JSONDecodeError, ValueError) as exc:
            last_err = exc
            print("Proposal attempt %d/%d invalid: %s"
                  % (attempt + 1, MAX_TRIES, exc))
    return None


def save_check_image(nxt, new_layout, highlights):
    global _warned_no_base
    if not os.path.isfile(opt.IMAGE_PATH):
        if not _warned_no_base:
            print("No %s; skipping check images." % opt.IMAGE_PATH)
            _warned_no_base = True
        return
    base = cv2.imread(opt.IMAGE_PATH, cv2.IMREAD_COLOR)
    if base is None:
        return
    out = nxt.replace(".json", "_check.png")
    cv2.imwrite(out, opt.draw_check(base, new_layout, highlights))
    print("Saved %s." % out)


def main():
    api_key = os.environ.get("MODEL_API_KEY")
    if not api_key:
        sys.exit("Missing MODEL_API_KEY environment variable.")
    start = sys.argv[1] if len(sys.argv) > 1 else "layout_bad.json"
    max_rounds = int(sys.argv[2]) if len(sys.argv) > 2 else 4
    if max_rounds < 1:
        sys.exit("max_rounds must be >= 1.")
    seed_arg = sys.argv[3] if len(sys.argv) > 3 else "42,43"
    try:
        seeds = sorted({int(s) for s in seed_arg.split(",")
                        if s.strip() != ""})
        assert seeds
    except (ValueError, AssertionError):
        sys.exit("Bad seeds %r; want e.g. 42,43." % seed_arg)
    if not os.path.isfile(start):
        sys.exit("Missing start layout: %s" % start)
    if os.path.abspath(start) != os.path.abspath(ROUND0):
        shutil.copyfile(start, ROUND0)  # Unity auto mode always starts here
    if os.path.isfile(NEXT):
        os.remove(NEXT)  # stale proposal must not outrun round 0
    if os.path.isfile(HISTORY):
        shutil.copyfile(HISTORY, PREV_HISTORY)
        print("Backed up %s -> %s." % (HISTORY, PREV_HISTORY))

    # Round 0 may already be done (loop started late): adopt it, run the
    # remaining seeds. Unity's status.json tells running from done.
    pending = None
    try:
        done = load_json(STATUS).get("state") == "done"
    except (OSError, ValueError):
        done = False
    unity_idle = done  # finished a run and now polling for layout_next.json
    if done and os.path.isfile(RESULTS) \
            and time.time() - mtime(RESULTS) < 900:  # else stale: rerun
        require_auto_layout()
        try:
            prev_seed = load_json(RUNCFG).get("seed", 42)
        except (OSError, ValueError):
            prev_seed = 42  # Unity default when no run_config.json
        res = load_results()
        pending = (res, prev_seed)
        print("Round 0 already finished (%.1f s, seed %s); continuing."
              % (float(res["evacuation_time_s"]), prev_seed))
    with open(RUNCFG, "w", encoding="utf-8") as f:
        json.dump({"seed": seeds[0]}, f)

    print("Plan: %d round(s) x seeds %s, scoring by mean time."
          % (max_rounds, seeds))
    history = []
    cur_file, cur_layout, cur_changes = start, load_json(start), []
    best_layout, best_time, best_round, best_wzc = \
        copy.deepcopy(cur_layout), None, 0, 0.0
    small, last = 0, mtime(RESULTS)

    for rnd in range(max_rounds):
        all_results, seed_times, timed_out = [], {}, False
        if pending is not None and rnd == 0:
            res0, s0 = pending
            pending = None
            all_results.append(res0)
            seed_times[s0] = round(float(res0["evacuation_time_s"]), 1)
            timed_out = bool(res0.get("timed_out"))
            run_seeds = [s for s in seeds if s != s0]
        else:
            run_seeds = seeds
        live_first = rnd == 0 and not seed_times and not unity_idle
        for i, seed in enumerate(run_seeds):
            if live_first and i == 0:
                print("Round 0 [%s] seed %d: waiting for %s ..."
                      % (cur_file, seed, RESULTS))
            else:
                trigger_run(cur_layout, seed)
                print("Round %d [%s] seed %d: staged, waiting ..."
                      % (rnd, cur_file, seed))
            results, last = wait_for_results(last)
            require_auto_layout()
            t = float(results["evacuation_time_s"])
            all_results.append(results)
            seed_times[seed] = round(t, 1)
            if results.get("timed_out"):
                timed_out = True
                print("Seed %d hit Unity maxRunTime (%.1f s)." % (seed, t))
        mean = sum(seed_times.values()) / len(seed_times)
        wzc = max(worst_zone(r) for r in all_results)
        history.append({"round": rnd, "layout": cur_file,
                        "time_s": round(mean, 1), "seed_times": seed_times,
                        "wzc": round(wzc, 2), "timed_out": timed_out,
                        "cost": cost_of(cur_changes), "changes": cur_changes,
                        "reasons": [c["reason"] for c in cur_changes]})
        with open(HISTORY, "w", encoding="utf-8") as f:
            json.dump(history, f, indent=2)

        if best_time is None or mean < best_time:
            sig = best_time is None or (best_time - mean) / best_time >= 0.03
            best_layout = copy.deepcopy(cur_layout)
            best_time, best_round, best_wzc = mean, rnd, wzc
            small = 0 if sig else small + 1
            print("Round %d [%s]: mean %.1f s over %s (new best)"
                  % (rnd, cur_file, mean, sorted(seed_times)))
        else:
            small += 1
            print("Round %d [%s]: mean %.1f s, worse than best %.1f s "
                  "(round %d); reverting"
                  % (rnd, cur_file, mean, best_time, best_round))
        if rnd == max_rounds - 1 or small >= 2:
            break

        base_prompt = (opt.PROMPT_TEMPLATE.format(
            layout=json.dumps(best_layout),
            results=json.dumps(all_results[-1]))
            + "\nHISTORY (changes tried and resulting mean times):\n"
            + history_text(history) + "\n"
            + LOOP_EXTRA.format(best=best_round, time=best_time,
                                seeds=",".join(map(str, seeds)),
                                tabu=tabu_text(history, best_time),
                                wzc=best_wzc))
        got = propose(api_key, base_prompt, best_layout)
        if got is None:
            print("Giving up on new proposals; keeping best.")
            break
        new_layout, highlights, changes = got
        nxt = "layout_round%d.json" % (rnd + 1)
        with open(nxt, "w", encoding="utf-8") as f:
            json.dump(new_layout, f, indent=2)
            f.write("\n")
        save_check_image(nxt, new_layout, highlights)
        cur_file, cur_layout, cur_changes = nxt, new_layout, changes
        print("Proposed %d change(s), cost %d -> %s (+ %s next run)."
              % (len(changes), cost_of(changes), nxt, NEXT))

    # Confirm the winner once with a held-out seed (no tuning on it).
    confirm_seed = 99 if 99 not in seeds else max(seeds) + 1
    trigger_run(best_layout, confirm_seed)
    print("Confirm: best layout (round %d) with held-out seed %d ..."
          % (best_round, confirm_seed))
    results, _ = wait_for_results(last)
    require_auto_layout()
    t = float(results["evacuation_time_s"])
    history.append({"round": "confirm",
                    "layout": history[best_round]["layout"],
                    "time_s": round(t, 1),
                    "seed_times": {confirm_seed: round(t, 1)},
                    "wzc": round(worst_zone(results), 2),
                    "timed_out": bool(results.get("timed_out")),
                    "cost": None, "changes": [],
                    "reasons": ["held-out seed confirmation, no changes"]})
    with open(HISTORY, "w", encoding="utf-8") as f:
        json.dump(history, f, indent=2)

    with open(BEST, "w", encoding="utf-8") as f:
        json.dump(best_layout, f, indent=2)
        f.write("\n")
    t0 = history[0]["time_s"] if history else 0
    print("\nround    layout                 time_s   vs r0  cost  changes")
    for e in history:
        pct = (e["time_s"] - t0) / t0 * 100 if t0 else 0
        chg = "; ".join("%s %s" % (c["type"], c["target_id"])
                        for c in e["changes"]) or "baseline"
        print("%-8s %-20s  %6.1f  %+5.1f%%  %-4s  %s%s"
              % (e["round"], e["layout"], e["time_s"], pct,
                 e["cost"] if e["cost"] is not None else "-",
                 chg, " *" if e["round"] == best_round else ""))
    print("Best: round %d (mean %.1f s) -> %s; confirm %.1f s (seed %d)."
          % (best_round, best_time, BEST, t, confirm_seed))


if __name__ == "__main__":
    main()
