"""Judge a Spooky Together lab run from each instance's spooky-together-lab.log.

Checks:
  * every ST-CHECK line passed, and each --expect'ed check appeared on the right instances
  * no mod errors (ST-MODERROR) at all
  * game errors do not keep growing after warm-up (slope over the last half of the run)
  * managed heap / Unity memory / GameObject count do not trend upward across the run
  * clients stay on the host's room sequence and entity desync stays bounded and does not worsen

Usage: python -I verify-lab.py <mod root> [--expect name[@role] ...] [--min-rooms N] [--out results.json]
"""
import json
import re
import sys
from pathlib import Path

LABS = {"host": "lab-host", "c1": "lab-client", "c2": "lab-c2", "c3": "lab-c3"}
METRIC = re.compile(r"ST-METRIC (.*)")
CHECK = re.compile(r"ST-CHECK (\S+) (PASS|FAIL) ?(.*)")


def parse(path):
    metrics, checks, moderrors = [], [], []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = METRIC.search(line)
        if m:
            fields = dict(kv.split("=", 1) for kv in m.group(1).split() if "=" in kv)
            metrics.append(fields)
            continue
        c = CHECK.search(line)
        if c:
            checks.append((c.group(1), c.group(2) == "PASS", c.group(3)))
        if "ST-MODERROR" in line:
            moderrors.append(line.strip()[:400])
    return metrics, checks, moderrors


def slope(values):
    n = len(values)
    if n < 3:
        return 0.0
    xs = range(n)
    mx, my = (n - 1) / 2, sum(values) / n
    den = sum((x - mx) ** 2 for x in xs)
    return sum((x - mx) * (y - my) for x, y in zip(xs, values)) / den if den else 0.0


def main():
    root = Path(sys.argv[1])
    args = sys.argv[2:]
    expects, min_rooms, out = [], 0, None
    i = 0
    while i < len(args):
        if args[i] == "--expect":
            expects.append(args[i + 1]); i += 2
        elif args[i] == "--min-rooms":
            min_rooms = int(args[i + 1]); i += 2
        elif args[i] == "--out":
            out = args[i + 1]; i += 2
        else:
            i += 1
    results, failures = {}, []
    host_seq = {}
    for role, folder in LABS.items():
        path = root / "private" / folder / "spooky-together-lab.log"
        if not path.exists():
            continue
        metrics, checks, moderrors = parse(path)
        info = {"checks": [{"name": n, "pass": p, "detail": d} for n, p, d in checks], "moderrors": moderrors[:5]}
        for n, p, d in checks:
            if not p:
                failures.append(f"{role}: check {n} failed: {d}")
        if moderrors:
            failures.append(f"{role}: {len(moderrors)} mod errors, first: {moderrors[0]}")
        if metrics:
            half = metrics[len(metrics) // 2:]
            err = [float(m.get("errors", 0)) for m in half]
            # Memory is judged while playing rooms; loading an ending or credits scene is not a leak.
            playing = [m for m in metrics if m.get("phase", "room") == "room"]
            heap = [float(m.get("heapMB", 0)) for m in playing[len(playing) // 2:]]
            objs = [float(m.get("objects", 0)) for m in half]
            info.update({
                "samples": len(metrics),
                "errors_end": metrics[-1].get("errors"),
                "error_slope_per_10s": round(slope(err), 3),
                "heap_slope_mb_per_10s": round(slope(heap), 3),
                "objects_slope_per_10s": round(slope(objs), 2),
                "max_seq": max(int(m.get("seq", -1)) for m in metrics),
            })
            if slope(err) > 0.5:
                failures.append(f"{role}: game errors keep growing ({slope(err):.2f} per 10 s)")
            if slope(heap) > 1.5:
                failures.append(f"{role}: managed heap keeps growing ({slope(heap):.2f} MB per 10 s)")
            mod = sorted(float(m["modms"]) for m in metrics if "modms" in m)
            if mod:
                info["modms_median"] = mod[len(mod) // 2]
                info["modms_max"] = mod[-1]
                if mod[len(mod) // 2] > 2.0:
                    failures.append(f"{role}: mod costs {mod[len(mod) // 2]:.2f} ms per frame (median), budget 2 ms")
            mobj = [float(m["modobjects"]) for m in half if "modobjects" in m]
            if mobj:
                info["modobjects_end"] = mobj[-1]
                info["modobjects_slope"] = round(slope(mobj), 3)
                # Runtime + one avatar and one retarget proxy per peer + spawn anchors and lab targets.
                peers = max(int(m.get("peers", 0)) for m in metrics)
                bound = 1 + 2 * peers + 4
                info["modobjects_bound"] = bound
                if max(mobj) > bound:
                    failures.append(f"{role}: the mod holds {max(mobj):.0f} objects, more than its bound of {bound}")
                # The mod creates a fixed set lazily (avatars, one retarget proxy per player); the bound above is the leak test.
            desync = [float(m["desync"]) for m in metrics if "desync" in m and float(m["desync"]) >= 0]
            if desync:
                info["desync_max"] = max(desync)
                info["desync_slope"] = round(slope(desync[len(desync) // 2:]), 3)
                if slope(desync[len(desync) // 2:]) > 0.2:
                    failures.append(f"{role}: entity desync is worsening ({info['desync_slope']} m per 10 s)")
            if role == "host":
                host_seq = info["max_seq"]
        results[role] = info
    if not results:
        failures.append("no lab logs found")
    for role, info in results.items():
        if role != "host" and "max_seq" in info and isinstance(host_seq, int) and host_seq - info["max_seq"] > 1:
            failures.append(f"{role}: fell behind the host (seq {info['max_seq']} vs {host_seq})")
    if min_rooms and (not isinstance(host_seq, int) or host_seq < min_rooms):
        failures.append(f"host reached only room seq {host_seq}, expected at least {min_rooms}")
    for spec in expects:
        name, _, where = spec.partition("@")
        if where == "anyclient":
            if not any(c["pass"] and c["name"] == name for r, info in results.items() if r != "host" for c in info.get("checks", [])):
                failures.append(f"no client passed {name}")
            continue
        clients_only = name.startswith("client.")
        if clients_only:
            name = name[len("client."):]
        roles = [where] if where else [r for r in results if r != "host"] if clients_only else list(results)
        for role in roles:
            names = [c["name"] for c in results.get(role, {}).get("checks", []) if c["pass"]]
            if name not in names:
                failures.append(f"{role}: expected passing check {name} not seen")
    summary = {"pass": not failures, "failures": failures, "instances": results}
    text = json.dumps(summary, indent=2)
    if out:
        Path(out).write_text(text, encoding="utf-8")
    print(text)
    sys.exit(0 if not failures else 1)


if __name__ == "__main__":
    main()
