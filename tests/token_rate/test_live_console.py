import json
from pathlib import Path
import tempfile
import unittest

from live_console import Turn, Monitor, frame


def model(turn, begin, end, name):
    turn.item({"started_at_ms": begin, "completed_at_ms": end,
               "item": {"type": "Reasoning", "id": name}}, end)


def usage(turn, at, output, identity):
    turn.usage({"response_id": identity,
                "usage": {"input_tokens": 1000, "cached_input_tokens": 100,
                          "output_tokens": output, "reasoning_output_tokens": 0,
                          "total_tokens": 1000+output}}, at)


class LiveConsoleTests(unittest.TestCase):
    def test_real_extreme_timing_pattern_cannot_change_displayed_speed(self):
        t = Turn(1000); model(t, 1000, 11000, "valid"); usage(t, 11000, 1000, "one")
        before = t.speed
        model(t, 12000, 12001, "short"); usage(t, 12001, 920, "two")
        self.assertEqual(t.speed, before)
        self.assertEqual(t.output, 1920)
        self.assertEqual(t.snapshot(12001)["average"], 100)
        model(t, 14000, 24000, "next"); usage(t, 24000, 1000, "three")
        self.assertEqual(t.speed, 100)
        self.assertEqual(t.measurements, 2)

    def test_subsecond_samples_accumulate_before_first_display(self):
        t = Turn(1000)
        for i in range(3):
            at = 1000+i*1000
            model(t, at, at+400, str(i)); usage(t, at+400, 40, str(i))
            self.assertEqual(t.speed is not None, i == 2)
        self.assertEqual(t.speed, 100)

    def test_zero_window_does_not_poison_later_responses(self):
        t = Turn(1000); model(t, 1000, 1000, "empty"); usage(t, 1000, 920, "one")
        model(t, 2000, 12000, "good"); usage(t, 12000, 1000, "two")
        self.assertEqual(t.speed, 100)

    def test_ema_smooths_a_real_rate_change(self):
        t = Turn(1000); model(t, 1000, 11000, "one"); usage(t, 11000, 1000, "one")
        model(t, 12000, 22000, "two"); usage(t, 22000, 2000, "two")
        self.assertAlmostEqual(t.speed, 117.5)

    def test_tool_duration_is_not_in_speed(self):
        t = Turn(1000); model(t, 1000, 11000, "one"); usage(t, 11000, 1000, "one")
        t.item({"started_at_ms": 11000, "completed_at_ms": 111000,
                "item": {"type": "CommandExecution", "id": "tool"}}, 111000)
        model(t, 112000, 122000, "two"); usage(t, 122000, 1000, "two")
        self.assertEqual(t.speed, 100)

    def test_overlap_and_replay_do_not_add_time(self):
        t = Turn(1000); model(t, 1000, 7000, "one"); model(t, 5000, 11000, "two")
        model(t, 1000, 7000, "one"); usage(t, 11000, 1000, "one")
        usage(t, 11000, 1000, "one")
        self.assertEqual(t.accepted_ms, 10000)
        self.assertEqual(t.output, 1000)

    def test_counter_mirror_is_not_a_second_response(self):
        m = Monitor("thread")
        m.observe({"timestamp": "1970-01-01T00:00:01Z", "type": "event_msg",
                   "payload": {"type": "task_started", "turn_id": "turn"}}, "thread")
        m.observe({"timestamp": "1970-01-01T00:00:02Z", "type": "event_msg",
                   "payload": {"type": "token_count", "turn_id": "turn",
                               "info": {"last_token_usage": {"output_tokens": 920}}}}, "thread")
        self.assertEqual(m.snapshot()["output"], 0)

    def test_other_threads_and_older_replayed_turns_do_not_take_over(self):
        m = Monitor("thread")
        for stamp, turn, identity in (("02", "new", "thread"), ("01", "old", "thread"),
                                       ("03", "other", "different")):
            m.observe({"timestamp": f"1970-01-01T00:00:{stamp}Z", "type": "event_msg",
                       "payload": {"type": "task_started", "turn_id": turn}}, identity)
        self.assertIs(m.last, m.turns["new"])
        self.assertNotIn("other", m.turns)

    def test_partial_file_line_is_only_read_when_complete(self):
        m = Monitor("thread")
        with tempfile.TemporaryDirectory(prefix="TPS 中文 ") as folder:
            p = Path(folder)/"page.jsonl"
            value = json.dumps({"timestamp": "1970-01-01T00:00:01Z", "type": "event_msg",
                                "payload": {"type": "task_started", "turn_id": "turn"}}).encode()
            p.write_bytes(value[:20]); m.read(p)
            self.assertIsNone(m.last)
            with p.open("ab") as f: f.write(value[20:]+b"\n")
            m.read(p); self.assertIsNotNone(m.last)

    def test_dashboard_has_numbers_and_no_timing_error_explanation(self):
        t = Turn(1000); model(t, 1000, 11000, "one"); usage(t, 11000, 1000, "one")
        m = Monitor("thread"); m.turns["turn"] = t; m.last = t
        text = frame(m.snapshot(12000))
        self.assertIn("100.0  tok/s", text)
        self.assertNotIn("异常", text)
        self.assertIn("1 秒前", text)

    def test_completion_zeros_current_speed_but_keeps_measurement_and_totals(self):
        t = Turn(1000); model(t, 1000, 11000, "one"); usage(t, 11000, 1000, "one")
        t.complete(12000)
        s = t.snapshot(13000)
        self.assertEqual(s["speed"], 0)
        self.assertEqual(s["measuredSpeed"], 100)
        self.assertEqual(s["average"], 100)
        self.assertEqual(s["output"], 1000)

    def test_aborted_turn_also_zeros_speed(self):
        t = Turn(1000); model(t, 1000, 11000, "one"); usage(t, 11000, 1000, "one")
        t.complete(12000, aborted=True)
        self.assertEqual(t.snapshot(13000)["speed"], 0)

    def test_delayed_usage_after_completion_cannot_restart_current_speed(self):
        t = Turn(1000); model(t, 1000, 11000, "one"); t.complete(11500)
        usage(t, 12000, 1000, "one")
        s = t.snapshot(13000)
        self.assertEqual(s["speed"], 0)
        self.assertEqual(s["measuredSpeed"], 100)
        self.assertEqual(s["status"], "本轮已完成")

    def test_rolling_minute_is_wall_clock_usage_and_expires_at_exact_boundary(self):
        m = Monitor("thread"); t = Turn(1000); m.turns["turn"] = t; m.last = t
        model(t, 1000, 11000, "one"); usage(t, 11000, 1000, "one"); t.complete(12000)
        self.assertEqual(m.snapshot(13000)["speed"], 0)
        self.assertEqual(m.snapshot(13000)["outputPerMinute"], 1000)
        self.assertEqual(m.snapshot(70999)["outputPerMinute"], 1000)
        self.assertEqual(m.snapshot(71000)["outputPerMinute"], 0)
        self.assertEqual(m.snapshot(71000)["output"], 1000)

    def test_minute_usage_crosses_turn_boundary_but_does_not_include_future(self):
        m = Monitor("thread"); a = Turn(1000); b = Turn(12000)
        m.turns.update(a=a, b=b); m.last = b
        model(a, 1000, 11000, "a"); usage(a, 11000, 1000, "a"); a.complete(11500)
        model(b, 12000, 22000, "b"); usage(b, 22000, 2000, "b")
        self.assertEqual(m.snapshot(20000)["outputPerMinute"], 1000)
        self.assertEqual(m.snapshot(23000)["outputPerMinute"], 3000)


if __name__ == "__main__":
    unittest.main(verbosity=2)
