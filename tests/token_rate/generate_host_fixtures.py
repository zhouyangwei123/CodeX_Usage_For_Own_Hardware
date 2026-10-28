"""The accepted Python console supplies expected results for the C# port."""
from datetime import datetime, timezone
import json
from pathlib import Path
import random
import sys

from live_console import Monitor

cases = []


def case(name):
    global monitor, records, checks
    monitor, records, checks = Monitor(), [], []
    cases.append({"name": name, "records": records, "checks": checks})


def emit(source, turn, kind, payload, stamp):
    row = {"type": kind, "timestamp": datetime.fromtimestamp(stamp/1000, timezone.utc).isoformat(),
           "payload": {"thread_id": source, "turn_id": turn, **payload}}
    records.append(row)
    monitor.observe(row, source)


def begin(source, turn, at=1000):
    emit(source, turn, "event_msg", {"type": "task_started"}, at)


def model(source, turn, identity, start, end):
    emit(source, turn, "event_msg", {"type": "item_completed", "started_at_ms": start,
         "completed_at_ms": end, "item": {"type": "Reasoning", "id": identity}}, end)


def usage(source, turn, response, at, output, input=1000):
    emit(source, turn, "token_usage_record", {"response_id": response,
         "usage": {"input_tokens": input, "cached_input_tokens": 0,
                   "output_tokens": output, "reasoning_output_tokens": 0,
                   "total_tokens": input+output}}, at)


def end(source, turn, at, aborted=False):
    emit(source, turn, "event_msg", {"type": "turn_aborted" if aborted else "task_complete"}, at)


def check(at):
    s = monitor.snapshot(at)
    checks.append({"after": len(records), "now": at, "rate": s["speed"],
                   "active": s["activeSessions"], "measured": s["measuredSessions"],
                   "sources": s["discoveredSessions"]})


case("parallel, one ends, all end, late usage, expiry")
for source, output in (("a", 1000), ("b", 2000)):
    begin(source, "same-turn"); model(source, "same-turn", "item", 2000, 12000)
    usage(source, "same-turn", source, 12000, output)
check(13000); end("a", "same-turn", 14000); check(14000)
end("b", "same-turn", 15000, True); check(15000)
usage("a", "same-turn", "late", 16000, 50); check(16000); check(80000)

case("overlap, replay and inherited response")
begin("a", "turn"); model("a", "turn", "one", 2000, 8000); model("a", "turn", "two", 6000, 12000)
model("a", "turn", "one", 2000, 8000); usage("a", "turn", "shared", 12000, 1000)
usage("a", "turn", "shared", 12000, 1000); check(13000)
begin("b", "turn"); model("b", "turn", "one", 2000, 12000); usage("b", "turn", "shared", 12000, 1000); check(13000)

case("short timing, zero timing, recovery and smoothing")
begin("a", "turn"); model("a", "turn", "first", 2000, 12000); usage("a", "turn", "one", 12000, 1000); check(12000)
model("a", "turn", "short", 13000, 13001); usage("a", "turn", "two", 13001, 920); check(14000)
model("a", "turn", "zero", 15000, 15000); usage("a", "turn", "three", 15000, 1000); check(15000)
model("a", "turn", "next", 16000, 26000); usage("a", "turn", "four", 26000, 2000); check(26000)
check(86001)

case("subsecond accumulation")
begin("a", "turn")
for i in range(4):
    at = 2000+i*1000; model("a", "turn", str(i), at, at+400)
    usage("a", "turn", str(i), at+400, 40); check(at+400)

case("missing start and end before replayed start")
usage("a", "unknown", "orphan", 12000, 100); check(13000)
end("b", "turn", 15000); begin("b", "turn"); model("b", "turn", "one", 2000, 12000)
usage("b", "turn", "valid", 12000, 1000); check(16000)

case("new turn replaces unclosed predecessor")
begin("a", "old"); model("a", "old", "one", 2000, 12000); usage("a", "old", "one", 12000, 1000)
begin("a", "new", 13000); check(13000); end("a", "new", 14000); check(14000)

case("interleaved models and repeated completion")
rng = random.Random(628)
for source in ("a", "b", "c"): begin(source, "turn")
for i in range(30):
    source = ("a", "b", "c")[i % 3]; start = 2000+i*6000; duration = rng.randrange(1, 6)*500
    model(source, "turn", str(i), start, start+duration)
    usage(source, "turn", str(i), start+duration, rng.randrange(1, 6)*100); check(start+duration)
for source in ("c", "a", "b"):
    end(source, "turn", 190000); check(190000)

Path(sys.argv[1]).write_text(json.dumps(cases, indent=2), encoding="utf-8")
print(f"Generated {len(cases)} cases, {sum(len(c['checks']) for c in cases)} Python/C# checkpoints")
