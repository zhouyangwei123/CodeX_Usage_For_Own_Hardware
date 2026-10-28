"""Small live TPS console. Only reads local Codex logs; no API or credentials."""
import argparse
from collections import Counter, deque
import ctypes
import json
import math
import os
from pathlib import Path
import sys
import time

from probe import event_ms, union_ms

TITLE = "CodeX Token Speed - Live Test"
ALPHA = .35
MIN_RESPONSE_MS = 250
MIN_MEASUREMENT_MS = 1000
MAX_ITEM_MS = 600000
MAX_TPS = 100000
CURRENT_SAMPLE_MS = 60000


class Turn:
    def __init__(self, started, has_start=True):
        self.started = started
        self.has_start = has_start
        self.at = started
        self.usage_at = 0
        self.responses = {}
        self.items = set()
        self.pending = []
        self.bad_timing = False
        self.recent = deque(maxlen=5)
        self.input = self.output = self.cache = self.accepted_output = self.accepted_ms = 0
        self.speed = None
        self.speed_at = None
        self.status = "等待模型响应"
        self.finished = False
        self.measurements = 0
        self.quality = Counter()

    def item(self, payload, stamp):
        item = payload.get("item")
        if not isinstance(item, dict):
            return
        kind, identity = item.get("type"), item.get("id")
        self.at = max(self.at, stamp)
        if kind not in ("Reasoning", "AgentMessage") or not isinstance(identity, str):
            return
        if (kind, identity) in self.items:
            return
        self.items.add((kind, identity))
        start, end = payload.get("started_at_ms"), payload.get("completed_at_ms")
        if (type(start) is not int or type(end) is not int or start <= 0
                or not 0 < end-start <= MAX_ITEM_MS or end > stamp+1000):
            self.bad_timing = True
            return
        self.pending.append((start, end))
        if not self.finished:
            self.status = "正在处理响应"

    def usage(self, payload, stamp):
        usage, response = payload.get("usage"), payload.get("response_id")
        if not isinstance(usage, dict) or not isinstance(response, str) or response in self.responses:
            return
        values = [usage.get(k, 0) for k in
                  ("input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens")]
        if (any(type(x) is not int or x < 0 or x > 10**15 for x in values)
                or values[1] > values[0] or values[3] > values[2]
                or usage.get("total_tokens", values[0]+values[2]) != values[0]+values[2]):
            self.quality["invalid_usage"] += 1
            self.pending.clear()
            self.bad_timing = False
            self.usage_at = max(self.usage_at, stamp)
            return
        self.responses[response] = (stamp, tuple(values))
        self.input += values[0]
        self.cache += values[1]
        self.output += values[2]
        duration = union_ms(self.pending)
        aligned = all(a >= self.usage_at and b <= stamp for a, b in self.pending)
        usable = (not self.bad_timing and aligned and duration >= MIN_RESPONSE_MS
                  and values[2] > 0 and values[2]*1000/duration <= MAX_TPS)
        self.pending.clear()
        self.bad_timing = False
        self.usage_at = max(self.usage_at, stamp)
        self.at = max(self.at, stamp)
        if not self.finished:
            self.status = "等待下一段响应"
        if not usable:
            self.quality["timing_not_used_for_speed"] += 1
            return
        self.recent.append((values[2], duration))
        self.accepted_output += values[2]
        self.accepted_ms += duration
        measured = sum(ms for _, ms in self.recent)
        if measured < MIN_MEASUREMENT_MS:
            if not self.finished:
                self.status = "正在积累测速样本"
            return
        raw = sum(tokens for tokens, _ in self.recent)*1000/measured
        if not math.isfinite(raw):
            return
        self.speed = raw if self.speed is None else ALPHA*raw + (1-ALPHA)*self.speed
        self.speed_at = stamp
        self.measurements += 1

    def snapshot(self, now_ms):
        measured = round(self.speed, 1) if self.speed is not None else None
        return {"speed": 0.0 if self.finished else measured,
                "measuredSpeed": measured,
                "average": round(self.accepted_output*1000/self.accepted_ms, 1)
                if self.accepted_ms >= MIN_MEASUREMENT_MS else None,
                "input": self.input, "output": self.output, "cache": self.cache,
                "responses": len(self.responses), "measurements": self.measurements,
                "ageSeconds": max(0, (now_ms-self.speed_at)//1000) if self.speed_at else None,
                "status": self.status, "acceptedModelMs": self.accepted_ms,
                "quality": dict(self.quality)}

    def complete(self, stamp, aborted=False):
        self.at = max(self.at, stamp)
        self.finished = True
        self.status = "本轮已停止" if aborted else "本轮已完成"


class Monitor:
    def __init__(self, thread=None):
        self.thread = thread
        self.turns = {}
        self.latest = {}
        self.response_owners = {}
        self.identities = {}
        self.coverage = Counter()
        self.last = None
        self.offsets = {}
        self.partial = {}
        self.paths = []
        self.next_discovery = 0

    def observe(self, row, identity):
        if not isinstance(row, dict):
            return
        kind, q = row.get("type"), row.get("payload", {})
        if not isinstance(q, dict):
            return
        source = q.get("thread_id") or identity
        if not isinstance(source, str) or (self.thread and source != self.thread):
            return
        if kind not in ("event_msg", "token_usage_record", "response_item"):
            return
        sub, turn_id = q.get("type"), q.get("turn_id")
        stamp = event_ms(row)
        if stamp is None or not isinstance(turn_id, str):
            return
        key = (source, turn_id)
        if kind != "token_usage_record" and not (kind == "event_msg" and sub in
                ("task_started", "item_completed", "task_complete", "turn_aborted")):
            return
        turn = self.turns.setdefault(key, Turn(stamp, has_start=False))
        if kind == "event_msg" and sub == "task_started" and not turn.has_start:
            turn.started = stamp
            turn.has_start = True
        if kind == "token_usage_record":
            if not turn.has_start:
                self.coverage["usage_without_turn_start"] += 1
            response = q.get("response_id")
            if isinstance(response, str):
                owner = self.response_owners.setdefault(response, key)
                if owner != key:
                    self.coverage["inherited_response_duplicates"] += 1
                    turn.pending.clear()
                    turn.bad_timing = False
                    turn.usage_at = max(turn.usage_at, stamp)
                    return
            turn.usage(q, stamp)
        elif kind == "event_msg" and sub == "item_completed":
            turn.item(q, stamp)
        elif kind == "event_msg" and sub in ("task_complete", "turn_aborted"):
            turn.complete(stamp, sub == "turn_aborted")
        previous = self.latest.get(source)
        if previous is None or (turn.has_start, turn.started) >= (previous.has_start, previous.started):
            self.latest[source] = turn
        self.last = max(self.latest.values(), key=lambda t: t.started)

    def read(self, path):
        size = path.stat().st_size
        offset = self.offsets.get(path, 0)
        if size < offset:
            offset = 0
            self.partial[path] = b""
            self.identities.pop(path, None)
        if size == offset:
            return
        with path.open("rb") as stream:
            stream.seek(offset)
            chunk = stream.read(size-offset)
            self.offsets[path] = stream.tell()
        lines = (self.partial.get(path, b"")+chunk).split(b"\n")
        self.partial[path] = lines.pop()
        for line in lines:
            if not any(tag in line for tag in
                       (b'"session_meta"', b'"task_started"', b'"task_complete"', b'"turn_aborted"',
                        b'"item_completed"', b'"token_usage_record"')):
                continue
            try:
                row = json.loads(line)
            except (ValueError, UnicodeError):
                continue
            if isinstance(row, dict) and row.get("type") == "session_meta":
                self.identities[path] = row.get("payload", {}).get("id")
                continue
            self.observe(row, self.identities.get(path, self.thread))

    def poll(self, home):
        if time.monotonic() >= self.next_discovery:
            pattern = "*"+self.thread+"*.jsonl" if self.thread else "*.jsonl"
            self.paths = sorted(p for folder in ("sessions", "archived_sessions")
                                for p in (home/folder).rglob(pattern))
            self.next_discovery = time.monotonic()+3
        for path in self.paths:
            try:
                self.read(path)
            except (OSError, PermissionError):
                # Keep the last measured display while a page is being replaced.
                continue

    def snapshot(self, now_ms=None):
        now_ms = int(time.time()*1000) if now_ms is None else now_ms
        if self.last:
            state = self.last.snapshot(now_ms)
        else:
            state = {"speed": None, "measuredSpeed": None, "average": None,
                     "input": 0, "output": 0, "cache": 0, "responses": 0,
                     "measurements": 0, "ageSeconds": None,
                     "status": "等待读取本次会话", "quality": {}}
        recent = [values for turn in self.turns.values()
                  for stamp, values in turn.responses.values() if now_ms-60000 < stamp <= now_ms]
        state["inputPerMinute"] = sum(v[0] for v in recent)
        state["outputPerMinute"] = sum(v[2] for v in recent)
        state["scope"] = "thread" if self.thread else "local"
        state["snapshotAtMs"] = now_ms
        if not self.thread:
            active = [t for t in self.latest.values()
                      if not t.finished and 0 <= now_ms-t.at <= CURRENT_SAMPLE_MS]
            measured = [t for t in active if t.has_start and t.speed_at is not None
                        and 0 <= now_ms-t.speed_at <= CURRENT_SAMPLE_MS]
            state.update(
                speed=round(sum(t.speed for t in measured), 1) if measured or not active else None,
                input=sum(t.input for t in self.turns.values()),
                output=sum(t.output for t in self.turns.values()),
                responses=sum(len(t.responses) for t in self.turns.values()),
                measurements=sum(t.measurements for t in self.turns.values()),
                ageSeconds=min(((now_ms-t.speed_at)//1000 for t in measured), default=None),
                activeSessions=len(active), measuredSessions=len(measured),
                waitingSessions=len(active)-len(measured), discoveredSessions=len(self.latest),
                files=len(self.paths),
                status=("并行会话统计中" if len(active)>1 else "会话统计中" if active else
                        "当前无活跃会话"),
                coverage=dict(self.coverage),
                sessionSpeeds=[round(t.speed, 1) for t in measured])
        return state


def frame(state):
    speed = "--" if state["speed"] is None else f'{state["speed"]:.1f}'
    if state.get("scope") == "local":
        age = "等待近期样本" if state["ageSeconds"] is None else f'{state["ageSeconds"]} 秒前'
        return "\n".join([
            "  CodeX  |  本机全部会话 Token 速度测试",
            "  " + "="*60, "",
            f"        当前合计       {speed}  tok/s",
            f'  近期活跃 {state["activeSessions"]} 个  /  已测速 {state["measuredSessions"]} 个  /  待测速 {state["waitingSessions"]} 个',
            "",
            f'  近60秒输入  {state["inputPerMinute"]:>14,} tok/min',
            f'  近60秒输出  {state["outputPerMinute"]:>14,} tok/min',
            f'  已读累计    输入 {state["input"]:,}  /  输出 {state["output"]:,}',
            "",
            f'  会话 {state["discoveredSessions"]} 个  /  日志 {state["files"]} 份  /  测速更新 {state["measurements"]} 次',
            f'  状态      {state["status"]}', f"  速度更新  {age}", "",
            "  各会话独立平滑，并行速率相加；全部结束当前合计归零。",
            "  超过60秒的旧测速不计当前合计；历史用量保留。",
            "  响应结束后更新。按 Q 或 Ctrl+C 退出。",
            "  " + "="*60])
    average = "--" if state["average"] is None else f'{state["average"]:.1f}'
    measured = "--" if state["measuredSpeed"] is None else f'{state["measuredSpeed"]:.1f}'
    age = "等待首个样本" if state["ageSeconds"] is None else f'{state["ageSeconds"]} 秒前'
    return "\n".join([
        "  CodeX  |  Token 速度测试",
        "  " + "="*60,
        "",
        f"        输出速度       {speed}  tok/s",
        f"        本轮均速       {average}  tok/s",
        f"        最近测速       {measured}  tok/s",
        "",
        f'  本会话近60秒   输入 {state["inputPerMinute"]:,}  /  输出 {state["outputPerMinute"]:,} tok/min',
        f'  本轮输入  {state["input"]:>12,}    本轮输出  {state["output"]:>10,}',
        f'  响应次数  {state["responses"]:>12}    测速更新  {state["measurements"]:>10}',
        "",
        f'  状态      {state["status"]}',
        f"  速度更新  {age}",
        "",
        "  读取本次会话的真实数据；响应结束后更新速度。",
        "  轮次结束当前速度归零；累计用量保留，60秒用量自然回落。",
        "  工具耗时不计入测速。",
        "  继续在 Codex 中使用即可观察。按 Q 或 Ctrl+C 退出。",
        "  " + "="*60,
    ])


def console_setup():
    if os.name == "nt":
        from ctypes import wintypes
        ctypes.windll.kernel32.SetConsoleTitleW(TITLE)
        ctypes.windll.kernel32.GetStdHandle.restype = wintypes.HANDLE
        ctypes.windll.kernel32.GetConsoleMode.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        ctypes.windll.kernel32.SetConsoleMode.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        handle = ctypes.windll.kernel32.GetStdHandle(-11)
        mode = wintypes.DWORD()
        if ctypes.windll.kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
            ctypes.windll.kernel32.SetConsoleMode(handle, mode.value | 4)
    if sys.stdout.isatty():
        print("\033[2J\033[?25l", end="", flush=True)


def write_state(path, state, text):
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temp = path.with_suffix(".tmp")
        temp.write_text(json.dumps({**state, "frame": text}, ensure_ascii=False), encoding="utf-8")
        temp.replace(path)
        return True
    except OSError:
        # Optional diagnostics must not close the live panel when a reader holds it.
        return False


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--thread", help="optional: follow just one local conversation")
    cli.add_argument("--home", type=Path, default=Path(os.environ.get("CODEX_HOME", str(Path.home()/".codex"))))
    cli.add_argument("--seconds", type=int, default=0)
    cli.add_argument("--state-file", type=Path)
    cli.add_argument("--once", action="store_true")
    args = cli.parse_args()
    if args.thread is not None and (not args.thread or any(c not in "0123456789abcdef-" for c in args.thread.lower())):
        cli.error("thread must be a local thread identifier")
    monitor = Monitor(args.thread)
    console_setup()
    start = time.monotonic()
    try:
        while True:
            monitor.poll(args.home)
            state = monitor.snapshot()
            text = frame(state)
            if args.state_file:
                write_state(args.state_file, state, text)
            print(("\033[H\033[J" if sys.stdout.isatty() else "")+text, flush=True)
            if args.once or (args.seconds and time.monotonic()-start >= args.seconds):
                break
            if os.name == "nt":
                import msvcrt
                if msvcrt.kbhit() and msvcrt.getwch().lower() == "q":
                    break
            time.sleep(.5)
    except KeyboardInterrupt:
        pass
    finally:
        if sys.stdout.isatty():
            print("\033[?25h", end="", flush=True)


if __name__ == "__main__":
    main()
