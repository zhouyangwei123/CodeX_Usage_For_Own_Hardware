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


class Turn:
    def __init__(self, started):
        self.started = started
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
        self.responses[response] = tuple(values)
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
        self.status = "等待下一段响应"
        if not usable:
            self.quality["timing_not_used_for_speed"] += 1
            return
        self.recent.append((values[2], duration))
        self.accepted_output += values[2]
        self.accepted_ms += duration
        measured = sum(ms for _, ms in self.recent)
        if measured < MIN_MEASUREMENT_MS:
            self.status = "正在积累测速样本"
            return
        raw = sum(tokens for tokens, _ in self.recent)*1000/measured
        if not math.isfinite(raw):
            return
        self.speed = raw if self.speed is None else ALPHA*raw + (1-ALPHA)*self.speed
        self.speed_at = stamp
        self.measurements += 1

    def snapshot(self, now_ms):
        return {"speed": round(self.speed, 1) if self.speed is not None else None,
                "average": round(self.accepted_output*1000/self.accepted_ms, 1)
                if self.accepted_ms >= MIN_MEASUREMENT_MS else None,
                "input": self.input, "output": self.output, "cache": self.cache,
                "responses": len(self.responses), "measurements": self.measurements,
                "ageSeconds": max(0, (now_ms-self.speed_at)//1000) if self.speed_at else None,
                "status": self.status, "acceptedModelMs": self.accepted_ms,
                "quality": dict(self.quality)}


class Monitor:
    def __init__(self, thread):
        self.thread = thread
        self.turns = {}
        self.last = None
        self.offsets = {}
        self.partial = {}
        self.paths = []
        self.next_discovery = 0

    def observe(self, row, identity):
        if not isinstance(row, dict):
            return
        kind, q = row.get("type"), row.get("payload", {})
        if not isinstance(q, dict) or (q.get("thread_id") or identity) != self.thread:
            return
        if kind not in ("event_msg", "token_usage_record", "response_item"):
            return
        sub, turn_id = q.get("type"), q.get("turn_id")
        stamp = event_ms(row)
        if stamp is None or not isinstance(turn_id, str):
            return
        if kind == "event_msg" and sub == "task_started" and turn_id:
            self.turns.setdefault(turn_id, Turn(stamp))
        turn = self.turns.get(turn_id)
        if turn is None:
            return
        if kind == "token_usage_record":
            turn.usage(q, stamp)
        elif kind == "event_msg" and sub == "item_completed":
            turn.item(q, stamp)
        elif kind == "event_msg" and sub in ("task_complete", "turn_aborted"):
            turn.at = max(turn.at, stamp)
            turn.status = "本轮已完成" if sub == "task_complete" else "本轮已停止"
        self.last = max(self.turns.values(), key=lambda t: t.started)

    def read(self, path):
        size = path.stat().st_size
        offset = self.offsets.get(path, 0)
        if size < offset:
            offset = 0
            self.partial[path] = b""
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
                       (b'"task_started"', b'"task_complete"', b'"turn_aborted"',
                        b'"item_completed"', b'"token_usage_record"')):
                continue
            try:
                row = json.loads(line)
            except (ValueError, UnicodeError):
                continue
            self.observe(row, self.thread)

    def poll(self, home):
        if time.monotonic() >= self.next_discovery:
            self.paths = sorted((home/"sessions").rglob("*"+self.thread+"*.jsonl"))
            self.next_discovery = time.monotonic()+3
        for path in self.paths:
            try:
                self.read(path)
            except (OSError, PermissionError):
                # Keep the last measured display while a page is being replaced.
                continue

    def snapshot(self):
        if self.last:
            return self.last.snapshot(int(time.time()*1000))
        return {"speed": None, "average": None, "input": 0, "output": 0, "cache": 0,
                "responses": 0, "measurements": 0, "ageSeconds": None,
                "status": "等待读取本次会话", "quality": {}}


def frame(state):
    speed = "--" if state["speed"] is None else f'{state["speed"]:.1f}'
    average = "--" if state["average"] is None else f'{state["average"]:.1f}'
    age = "等待首个样本" if state["ageSeconds"] is None else f'{state["ageSeconds"]} 秒前'
    return "\n".join([
        "  CodeX  |  Token 速度测试",
        "  " + "="*60,
        "",
        f"        输出速度     ≈ {speed}  tok/s",
        f"        本轮均速     ≈ {average}  tok/s",
        "",
        f'  本轮输入  {state["input"]:>12,}    本轮输出  {state["output"]:>10,}',
        f'  响应次数  {state["responses"]:>12}    测速更新  {state["measurements"]:>10}',
        "",
        f'  状态      {state["status"]}',
        f"  速度更新  {age}",
        "",
        "  读取本次会话的真实数据；响应结束后更新速度。",
        "  速度经过平滑，工具耗时不计入；≈ 表示估算值。",
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


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--thread", required=True)
    cli.add_argument("--home", type=Path, default=Path(os.environ.get("CODEX_HOME", str(Path.home()/".codex"))))
    cli.add_argument("--seconds", type=int, default=0)
    cli.add_argument("--state-file", type=Path)
    cli.add_argument("--once", action="store_true")
    args = cli.parse_args()
    if not args.thread or any(c not in "0123456789abcdef-" for c in args.thread.lower()):
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
                args.state_file.parent.mkdir(parents=True, exist_ok=True)
                temp = args.state_file.with_suffix(".tmp")
                temp.write_text(json.dumps({**state, "frame": text}, ensure_ascii=False), encoding="utf-8")
                temp.replace(args.state_file)
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
