from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from live_console import Monitor, frame, write_state


def event(m, source, turn, kind, payload, stamp):
    payload = {"thread_id": source, "turn_id": turn, **payload}
    m.observe({"timestamp": datetime.fromtimestamp(stamp/1000, timezone.utc).isoformat(),
               "type": kind, "payload": payload}, source)


def begin(m, source, output=1000, turn="same-turn", response=None):
    event(m, source, turn, "event_msg", {"type": "task_started"}, 1000)
    event(m, source, turn, "event_msg", {"type": "item_completed", "started_at_ms": 2000,
          "completed_at_ms": 12000, "item": {"type": "Reasoning", "id": "item"}}, 12000)
    event(m, source, turn, "token_usage_record", {"response_id": response or source,
          "usage": {"input_tokens": 1000, "output_tokens": output,
                    "total_tokens": 1000+output}}, 12000)


def end(m, source, stamp=14000, turn="same-turn", aborted=False):
    event(m, source, turn, "event_msg", {"type": "turn_aborted" if aborted else "task_complete"}, stamp)


class AllSessionsTests(unittest.TestCase):
    def test_parallel_speeds_are_added_without_a_second_smoothing_tail(self):
        m = Monitor(); begin(m, "a"); begin(m, "b", 2000)
        s = m.snapshot(13000)
        self.assertEqual(s["speed"], 300)
        self.assertEqual((s["activeSessions"], s["measuredSessions"]), (2, 2))
        end(m, "a"); self.assertEqual(m.snapshot(14000)["speed"], 200)
        end(m, "b", 15000); s = m.snapshot(15000)
        self.assertEqual(s["speed"], 0)
        self.assertEqual(s["activeSessions"], 0)
        self.assertEqual(s["outputPerMinute"], 3000)
        self.assertEqual(s["output"], 3000)

    def test_abort_and_late_usage_cannot_resurrect_finished_source(self):
        m = Monitor(); begin(m, "a"); begin(m, "b", 2000)
        end(m, "a", aborted=True); end(m, "b")
        event(m, "a", "same-turn", "token_usage_record", {"response_id": "late",
              "usage": {"input_tokens": 0, "output_tokens": 50, "total_tokens": 50}}, 15000)
        self.assertEqual(m.snapshot(15000)["speed"], 0)
        self.assertEqual(m.snapshot(15000)["output"], 3050)

    def test_replayed_pages_and_mirrored_carriers_do_not_duplicate(self):
        m = Monitor(); begin(m, "a"); begin(m, "a")
        event(m, "a", "same-turn", "event_msg", {"type": "token_count",
              "info": {"last_token_usage": {"output_tokens": 1000}}}, 12001)
        s = m.snapshot(13000)
        self.assertEqual((s["speed"], s["output"], s["responses"]), (100, 1000, 1))

    def test_inherited_response_id_is_not_billed_twice_across_threads(self):
        m = Monitor(); begin(m, "a", response="shared"); begin(m, "b", response="shared")
        s = m.snapshot(13000)
        self.assertEqual(s["output"], 1000)
        self.assertEqual(s["speed"], 100)
        self.assertEqual(s["waitingSessions"], 1)

    def test_latest_turn_replaces_old_unclosed_turn_in_same_conversation(self):
        m = Monitor(); begin(m, "a")
        event(m, "a", "new-turn", "event_msg", {"type": "task_started"}, 20000)
        s = m.snapshot(21000)
        self.assertIsNone(s["speed"])
        self.assertEqual(s["activeSessions"], 1)
        end(m, "a", 22000, turn="new-turn")
        self.assertEqual(m.snapshot(22000)["speed"], 0)

    def test_old_unclosed_history_cannot_hold_current_total_forever(self):
        m = Monitor(); begin(m, "old"); begin(m, "new", 2000)
        s = m.snapshot(72001)
        self.assertEqual(s["speed"], 0)
        self.assertEqual(s["activeSessions"], 0)
        self.assertEqual(s["outputPerMinute"], 0)
        self.assertEqual(s["output"], 3000)

    def test_missing_start_preserves_consumption_without_inventing_speed(self):
        m = Monitor()
        event(m, "a", "unknown-turn", "token_usage_record", {"response_id": "one",
              "usage": {"input_tokens": 1000, "output_tokens": 100, "total_tokens": 1100}}, 12000)
        s = m.snapshot(13000)
        self.assertEqual(s["discoveredSessions"], 1)
        self.assertEqual(s["output"], 100)
        self.assertIsNone(s["speed"])
        self.assertEqual(s["waitingSessions"], 1)

    def test_end_before_start_in_another_page_is_not_lost(self):
        m = Monitor(); end(m, "a"); begin(m, "a")
        s = m.snapshot(15000)
        self.assertEqual(s["speed"], 0)
        self.assertEqual(s["output"], 1000)

    def test_discovers_new_session_and_archive_with_file_metadata_identity(self):
        with tempfile.TemporaryDirectory(prefix="TPS 全部会话 ") as folder:
            home = Path(folder); (home/"sessions").mkdir(); (home/"archived_sessions").mkdir()
            a = home/"sessions"/"a.jsonl"
            a.write_text(json.dumps({"type": "session_meta", "payload": {"id": "a"}})+"\n"+
                json.dumps({"timestamp": "1970-01-01T00:00:01Z", "type": "event_msg",
                            "payload": {"type": "task_started", "turn_id": "one"}})+"\n")
            m = Monitor(); m.poll(home)
            self.assertEqual(len(m.latest), 1)
            b = home/"archived_sessions"/"b.jsonl"
            b.write_text(a.read_text().replace('"a"', '"b"'))
            m.next_discovery = 0; m.poll(home)
            self.assertEqual((len(m.paths), len(m.latest)), (2, 2))
            # Moving a file into archive must keep all deduplication state.
            a.rename(home/"archived_sessions"/a.name)
            m.next_discovery = 0; m.poll(home)
            self.assertEqual(len(m.turns), 2)

    def test_pending_source_is_visible_alongside_known_partial_total(self):
        m = Monitor(); begin(m, "a")
        event(m, "b", "same-turn", "event_msg", {"type": "task_started"}, 13000)
        s = m.snapshot(14000)
        self.assertEqual(s["speed"], 100)
        self.assertEqual(s["waitingSessions"], 1)
        self.assertIn("待测速 1", frame(s))

    def test_busy_diagnostic_file_cannot_close_the_panel(self):
        with tempfile.TemporaryDirectory(prefix="TPS ") as folder:
            with patch.object(Path, "replace", side_effect=PermissionError("busy reader")):
                self.assertFalse(write_state(Path(folder)/"state.json", {}, "panel"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
