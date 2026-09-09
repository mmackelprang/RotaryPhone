#!/usr/bin/env python3
"""Report the five statistics that bound a WARN threshold for browserSessionAgeSeconds.

    python3 report-browser-session-age.py ~/.local/state/gv-session-age-samples.csv

⛔ THIS SCRIPT DELIBERATELY DOES NOT RECOMMEND A NUMBER, and that is its only real
requirement (plan Task 15; spec §11 decision 3). If you add one, you have broken it.
The threshold is chosen by the owner from these statistics.

⭐ WHY "age now" IS THE WRONG STATISTIC. browserSessionAgeSeconds RESETS every time
the 20-minute cron successfully revalidates, so its instantaneous value mostly
measures how long ago the last cron tick was — not session health. A threshold must
sit above the PEAK OF THE SAWTOOTH reached while the session was demonstrably
healthy, not above its mean.
"""
import csv, sys
from datetime import datetime, timezone


def parse(path):
    rows = []
    with open(path, newline="") as fh:
        for r in csv.DictReader(fh):
            try:
                t = datetime.strptime(r["utc"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
            except (ValueError, KeyError):
                continue
            age = r.get("age_seconds") or ""
            rows.append({
                "t": t,
                "outcome": r.get("outcome") or "",
                "age": int(age) if age.isdigit() else None,
            })
    rows.sort(key=lambda x: x["t"])
    return rows


def pct(vals, p):
    if not vals:
        return None
    s = sorted(vals)
    k = max(0, min(len(s) - 1, round((p / 100) * (len(s) - 1))))
    return s[k]


def hms(sec):
    if sec is None:
        return "n/a"
    h, rem = divmod(int(sec), 3600)
    return f"{sec} s ({h}h{rem // 60:02d}m)"


def main(path):
    rows = parse(path)
    if not rows:
        print(f"no usable samples in {path}")
        return 1

    window_h = (rows[-1]["t"] - rows[0]["t"]).total_seconds() / 3600
    healthy = [r for r in rows if r["outcome"] == "Succeeded" and r["age"] is not None]
    ages = [r["age"] for r in healthy]

    # The largest interval between two consecutive Succeeded samples. A cron tick that
    # is merely LATE must not read as a dead session, so this bounds the same thing
    # from the other side.
    gap = None
    for a, b in zip(healthy, healthy[1:]):
        d = (b["t"] - a["t"]).total_seconds()
        gap = d if gap is None or d > gap else gap

    noise = {}
    for r in rows:
        if r["outcome"] in ("Unreachable", "NotAttempted", "ABSENT", "UNREACHABLE"):
            noise[r["outcome"]] = noise.get(r["outcome"], 0) + 1

    print(f"Samples            : {len(rows)}  ({len(healthy)} with outcome=Succeeded)")
    print(f"Observation window : {window_h:.1f} hours "
          f"({rows[0]['t']:%Y-%m-%dT%H:%MZ} -> {rows[-1]['t']:%Y-%m-%dT%H:%MZ})")
    print()
    print("THE NUMBER A THRESHOLD MUST EXCEED, or it fires on a healthy phone:")
    print(f"  max age while outcome==Succeeded : {hms(max(ages) if ages else None)}")
    print()
    print("Is that max a routine peak or a one-off?")
    for p in (50, 95, 99):
        print(f"  p{p:<3} of the same population       : {hms(pct(ages, p))}")
    print()
    print("A cron tick that is merely LATE must not read as a dead session:")
    print(f"  largest gap between Succeeded    : {hms(gap)}")
    print()
    print("The noise floor a WARN would have to sit above:")
    if noise:
        for k, v in sorted(noise.items()):
            print(f"  {k:<28} : {v} samples")
    else:
        print("  (none — no Unreachable/NotAttempted/ABSENT samples in the window)")
    print()

    # ⛔ The honest answer when the window did not bound the maximum. Today's evidence
    # says a healthy session "runs for days"; a 72-hour window cannot bound that if the
    # max is still climbing at the end.
    #
    # ⚠ The test is "does the last tenth EXCEED everything before it", not "does it
    # contain the maximum". A healthy sawtooth re-reaches its peak every cycle, so
    # `>=` would flag every well-behaved sample as unbounded — a warning that always
    # fires, which is the failure mode this whole arc is about.
    cut = max(1, len(healthy) // 10)
    tail = [r["age"] for r in healthy[-cut:] if r["age"] is not None]
    earlier = [r["age"] for r in healthy[:-cut] if r["age"] is not None]
    still_climbing = bool(tail and earlier and max(tail) > max(earlier))
    if window_h < 72:
        print(f"⚠ WINDOW TOO SHORT: {window_h:.1f}h of samples, and the task asks for >= 72h.")
    if still_climbing:
        print("⚠ THE MAXIMUM IS STILL BEING SET IN THE LAST TENTH OF THE WINDOW.")
        print("  The window did NOT bound the maximum. The honest answer is")
        print("  \"the window did not bound it\" — NOT a number derived from a truncated sample.")
    elif window_h >= 72:
        print("The maximum was reached before the final tenth of the window, so the window")
        print("appears to have bounded it. That is a statement about THIS window only.")
    print()
    print("⛔ No threshold is proposed here, by design. See the module docstring.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
