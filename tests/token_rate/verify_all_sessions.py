"""Bounded read-only real-log check for the all-session console."""
import argparse
import copy
import json
import os
from pathlib import Path
import time

from live_console import Monitor, CURRENT_SAMPLE_MS


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--seconds", type=int, default=300)
    cli.add_argument("--home", type=Path, default=Path(os.environ.get("CODEX_HOME", str(Path.home()/".codex"))))
    cli.add_argument("--output", type=Path, required=True)
    args = cli.parse_args()
    if args.seconds < 1:
        cli.error("seconds must be positive")
    m = Monitor()
    cpu = time.process_time(); start = time.monotonic()
    m.poll(args.home)
    startup = time.monotonic()-start
    loops = max_active = max_measured = 0
    previous_input = previous_output = previous_updates = 0
    update_advances = 0
    next_progress = 30
    while True:
        m.poll(args.home)
        now = int(time.time()*1000)
        s = m.snapshot(now)
        assert s["input"] >= previous_input and s["output"] >= previous_output, "cumulative usage regressed"
        if s["measurements"] > previous_updates:
            update_advances += 1
        previous_input, previous_output, previous_updates = s["input"], s["output"], s["measurements"]
        measured = [t for t in m.latest.values() if t.has_start and not t.finished
                    and 0 <= now-t.at <= CURRENT_SAMPLE_MS and t.speed_at is not None
                    and 0 <= now-t.speed_at <= CURRENT_SAMPLE_MS]
        if measured:
            assert s["speed"] == round(sum(t.speed for t in measured), 1), "concurrent sum mismatch"
        elif not s["activeSessions"]:
            assert s["speed"] == 0, "idle total did not reach zero"
        response_ids = [identity for t in m.turns.values() for identity in t.responses]
        assert len(response_ids) == len(set(response_ids)), "duplicate response across sessions"
        max_active = max(max_active, s["activeSessions"])
        max_measured = max(max_measured, s["measuredSessions"])
        loops += 1
        elapsed = time.monotonic()-start
        if elapsed >= next_progress:
            print(json.dumps({"elapsedSeconds": round(elapsed, 1), "sources": s["discoveredSessions"],
                              "active": s["activeSessions"], "measured": s["measuredSessions"]}), flush=True)
            next_progress += 30
        if elapsed >= args.seconds:
            break
        time.sleep(.5)
    # This is an isolated lifecycle replay of collected state, not a claim that we
    # stopped the user's actual tasks. No source log or running task is modified.
    ended = copy.deepcopy(m)
    for t in ended.latest.values():
        t.complete(now)
    after = ended.snapshot(now)
    assert after["speed"] == 0 and after["activeSessions"] == 0
    assert after["outputPerMinute"] == s["outputPerMinute"] and after["output"] == s["output"]
    elapsed = time.monotonic()-start
    result = {"passed": True, "durationSeconds": round(elapsed, 3), "polls": loops,
              "initialReplaySeconds": round(startup, 3),
              "averageCpuPercentOfOneCore": round(100*(time.process_time()-cpu)/elapsed, 3),
              "sources": s["discoveredSessions"], "files": s["files"],
              "maxActiveSources": max_active, "maxSimultaneouslyMeasuredSources": max_measured,
              "iterationsWithMeasurementAdvance": update_advances,
              "aggregateMatchedPerSourceSum": True, "responseIdsGloballyUnique": True,
              "consumptionNeverRegressed": True, "simulatedAllEndedZero": True,
              "simulatedEndPreservesSlowAndCumulativeUsage": True,
              "actualUserTasksInterrupted": False, "hostFirmwareChanged": False,
              "coverage": dict(m.coverage), "personalContentOrIdentityExported": False}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
