"""Read-only feasibility probe: reported output tokens / completed model-item time.

This is a response-boundary estimate, never a live decoder-speed measurement.
Prompts, message text, tool arguments, credentials and thread IDs are not exported.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import statistics
import time

MODEL_ITEMS = {"Reasoning", "AgentMessage"}
TOOL_ITEMS = {"CommandExecution", "McpToolCall", "FileChange", "WebSearch", "ImageView"}


def union_ms(intervals):
    end = None
    total = 0
    for start, stop in sorted(intervals):
        if stop <= start:
            continue
        total += max(0, stop - max(start, end if end is not None else start))
        end = max(stop, end if end is not None else stop)
    return total


def event_ms(record):
    try:
        stamp = datetime.fromisoformat(record["timestamp"].replace("Z", "+00:00"))
        elapsed = stamp - datetime(1970, 1, 1, tzinfo=timezone.utc)
        return elapsed.days * 86400000 + elapsed.seconds * 1000 + elapsed.microseconds // 1000
    except (KeyError, ValueError, TypeError):
        return None


class Probe:
    def __init__(self):
        self.turns = {}
        self.counts = Counter()

    def observe(self, record):
        kind = record.get("type")
        payload = record.get("payload")
        if not isinstance(payload, dict):
            return None
        sub = payload.get("type")
        if kind not in {"token_usage_record", "event_msg"}:
            if "delta" in str(kind).lower():
                self.counts["delta_records"] += 1
            return None
        if kind == "event_msg" and sub == "token_count":
            # This legacy carrier duplicates modern usage in the current desktop.
            # The probe deliberately uses response IDs, not numeric-equality guesses.
            self.counts["legacy_token_count_records"] += 1
            return None
        if kind == "event_msg" and sub not in {"task_started", "task_complete", "item_completed"}:
            if "delta" in str(sub).lower():
                self.counts["delta_records"] += 1
            return None
        thread, turn = payload.get("thread_id"), payload.get("turn_id")
        # task_started/task_complete lack thread_id; the caller supplies file identity.
        thread = thread or record.get("probe_thread")
        if not thread or not turn:
            self.counts["unmatched_identity"] += 1
            return None
        state = self.turns.setdefault((thread, turn), {
            "started": False, "completed": False, "model": {}, "tools": {},
            "responses": {}, "invalid": False, "at": 0,
            "response_windows": [], "last_response": None,
        })
        stamp = event_ms(record)
        if kind == "event_msg" and sub == "task_started":
            state["started"] = True
            return None
        if kind == "event_msg" and sub == "task_complete":
            state["completed"] = True
            return None
        if kind == "event_msg" and sub == "item_completed":
            item = payload.get("item", {})
            item_type = item.get("type")
            if item_type not in MODEL_ITEMS | TOOL_ITEMS:
                return None
            start, end = payload.get("started_at_ms"), payload.get("completed_at_ms")
            if (type(start) is not int or type(end) is not int or start <= 0 or end <= start
                    or stamp is None or end > stamp + 1000 or not item.get("id")):
                self.counts["invalid_model_windows" if item_type in MODEL_ITEMS else "invalid_tool_windows"] += 1
                if item_type in MODEL_ITEMS:
                    state["invalid"] = True
                return None
            bucket = state["model"] if item_type in MODEL_ITEMS else state["tools"]
            key = (item_type, item["id"])
            new_item = key not in bucket
            if key in bucket:
                self.counts["duplicate_items"] += 1
                if bucket[key] != (start, end):
                    state["invalid"] = True
            bucket[key] = (start, end)
            if item_type in MODEL_ITEMS and new_item:
                state["response_windows"].append(key)
            return None
        usage = payload.get("usage")
        self.counts["modern_usage_records"] += 1
        response = payload.get("response_id")
        if not isinstance(usage, dict) or not response or stamp is None:
            self.counts["invalid_usage"] += 1
            return None
        names = ("input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens")
        values = tuple(usage.get(n, 0) for n in names)
        if (any(type(n) is not int or n < 0 or n > 10**15 for n in values)
                or values[1] > values[0] or values[3] > values[2]
                or usage.get("total_tokens", values[0] + values[2]) != values[0] + values[2]):
            self.counts["invalid_usage"] += 1
            state["invalid"] = True
            return None
        if response in state["responses"]:
            self.counts["duplicate_responses"] += 1
            if state["responses"][response] != values:
                state["invalid"] = True
            return None
        state["responses"][response] = values
        windows = [state["model"][k] for k in state["response_windows"]]
        response_ms = union_ms(windows)
        aligned = (state["started"] and not state["invalid"] and windows
                   and all(a >= state["at"] and b <= stamp for a,b in windows))
        state["last_response"] = (round(values[2]*1000/response_ms, 3)
                                  if aligned and response_ms > 0 else None)
        if state["last_response"] is not None:
            self.counts["response_estimates"] += 1
            if response_ms < 1000:
                self.counts["subsecond_response_estimates"] += 1
        else:
            self.counts["unavailable_response_estimates"] += 1
            if state["started"] and not state["invalid"] and windows:
                self.counts["ambiguous_response_timing"] += 1
        state["response_windows"].clear()
        state["at"] = max(state["at"], stamp)
        sample = self.snapshot(state)
        if sample is not None:
            self.counts["response_boundary_updates"] += 1
        return sample

    @staticmethod
    def snapshot(state):
        windows = list(state["model"].values())
        duration = union_ms(windows)
        output = sum(v[2] for v in state["responses"].values())
        if not state["started"] or state["invalid"] or duration <= 0 or output <= 0:
            return None
        # A new item can finish before its response usage arrives. Only measurements
        # folded at usage boundaries are emitted; no previous count / new duration mix.
        if any(end > state["at"] for _, end in windows):
            return None
        rate = output * 1000 / duration
        if not math.isfinite(rate):
            return None
        tools = union_ms(state["tools"].values())
        wall = max(end for _, end in windows) - min(start for start, _ in windows)
        return {"outputTpsApprox": round(rate, 3), "modelMs": duration,
                "lastResponseTpsApprox": state["last_response"],
                "toolMs": tools, "modelSpanMs": wall,
                "responseCount": len(state["responses"]),
                "modelItemCount": len(windows), "completed": state["completed"],
                "asOfMs": state["at"]}

    def report(self):
        rows = [s for t in self.turns.values() if (s := self.snapshot(t)) is not None]
        rates = [r["outputTpsApprox"] for r in rows]
        unavailable = Counter()
        for state in self.turns.values():
            if self.snapshot(state) is not None:
                continue
            reason = ("missing_turn_start" if not state["started"] else
                      "invalid_timing_or_counts" if state["invalid"] else
                      "awaiting_matching_usage_or_items")
            unavailable[reason] += 1
        return {"formula": "reported output_tokens / union(Reasoning, AgentMessage) seconds",
                "meaning": "completed-response boundary estimate; not instantaneous streaming TPS",
                "scope": "sampled local logs; no account-wide live speed",
                "turnsObserved": len(self.turns), "turnsWithEstimate": len(rows),
                "counters": dict(self.counts),
                "unavailableReasons": dict(unavailable),
                "tpsRange": [min(rates), max(rates)] if rates else [],
                "tpsMedian": statistics.median(rates) if rates else None,
                "samples": sorted(rows, key=lambda x: x["asOfMs"]),
                "messageContentOrIdentityExported": False}


def read_records(path, probe):
    identity = None
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if not any(s in line for s in ('"session_meta"', '"token_usage_record"', '"event_msg"')):
                continue
            try:
                row = json.loads(line)
            except (ValueError, TypeError):
                probe.counts["malformed_lines"] += 1
                continue
            if row.get("type") == "session_meta":
                identity = row.get("payload", {}).get("id")
                continue
            row["probe_thread"] = identity
            probe.observe(row)
    return identity


def update_gaps(updates):
    previous, gaps = {}, []
    for sample in updates:
        source, stamp = sample["sourceIndex"], sample["asOfMs"]
        if source in previous and stamp > previous[source]:
            gaps.append((stamp-previous[source])/1000)
        previous[source] = stamp
    return [min(gaps), max(gaps)] if gaps else []


def watch(paths, identities, probe, seconds):
    offsets = {p: p.stat().st_size for p in paths}
    pending = {p: b"" for p in paths}
    updates, events, lags = [], Counter(), []
    sources = {}
    start = time.monotonic()
    while time.monotonic() - start < seconds:
        for path in paths:
            with path.open("rb") as stream:
                stream.seek(offsets[path])
                data = stream.read()
                offsets[path] = stream.tell()
            lines = (pending[path] + data).split(b"\n")
            pending[path] = lines.pop()
            for line in lines:
                try:
                    row = json.loads(line)
                except (ValueError, TypeError):
                    continue
                q = row.get("payload", {})
                sub = q.get("type", "") if isinstance(q, dict) else ""
                kind = row.get("type", "")
                events[kind + "/" + sub] += 1
                row["probe_thread"] = identities[path]
                sample = probe.observe(row)
                if sample:
                    identity = q.get("thread_id") or identities[path]
                    sample["sourceIndex"] = sources.setdefault(identity, len(sources))
                    at = event_ms(row)
                    if at is not None:
                        lags.append(max(0, int(time.time()*1000) - at))
                    updates.append(sample)
        time.sleep(.25)
    return {"observedSeconds": round(time.monotonic()-start, 3), "pollIntervalMs": 250,
            "events": dict(events), "responseUpdates": len(updates),
            "readLagMedianMs": statistics.median(lags) if lags else None,
            "updatesPerSource": dict(Counter(s["sourceIndex"] for s in updates)),
            "sameSourceUpdateGapRangeSeconds": update_gaps(updates),
            "samples": updates}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--home", type=Path, default=Path(os.environ.get("CODEX_HOME", str(Path.home()/".codex"))))
    cli.add_argument("--files", type=int, default=8)
    cli.add_argument("--watch-seconds", type=int, default=0)
    cli.add_argument("--output", type=Path, required=True)
    args = cli.parse_args()
    if not 1 <= args.files <= 30 or not 0 <= args.watch_seconds <= 300:
        cli.error("files must be 1..30; watch-seconds must be 0..300")
    paths = sorted((args.home/"sessions").rglob("*.jsonl"), key=lambda p:p.stat().st_mtime, reverse=True)[:args.files]
    probe = Probe()
    identities = {p: read_records(p, probe) for p in paths}
    report = {"filesSampled": len(paths), "history": probe.report()}
    if args.watch_seconds:
        report["liveObservation"] = watch(paths, identities, probe, args.watch_seconds)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({"files": len(paths), "turnsWithEstimate": report["history"]["turnsWithEstimate"],
                      "tpsMedian": report["history"]["tpsMedian"],
                      "liveUpdates": report.get("liveObservation", {}).get("responseUpdates")}, ensure_ascii=True))


if __name__ == "__main__":
    main()
