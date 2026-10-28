import unittest
from datetime import datetime, timezone
from probe import Probe, event_ms, union_ms, update_gaps


def row(kind, payload, at=100000, thread="thread-a"):
    return {"type": kind, "payload": payload, "probe_thread": thread,
            "timestamp": datetime.fromtimestamp(at/1000, timezone.utc).isoformat()}


def start(probe, turn="turn-a", thread="thread-a"):
    probe.observe(row("event_msg", {"type": "task_started", "turn_id": turn}, at=1000, thread=thread))


def item(probe, name, begin, end, identity="item-a", turn="turn-a", thread="thread-a"):
    return probe.observe(row("event_msg", {"type": "item_completed", "turn_id": turn,
        "thread_id": thread, "started_at_ms": begin, "completed_at_ms": end,
        "item": {"type": name, "id": identity}}, at=max(100000, end)))


def usage(probe, output=1400, response="response-a", turn="turn-a", thread="thread-a", at=100000, **extra):
    counts = {"input_tokens": 9000, "cached_input_tokens": 8000,
              "output_tokens": output, "reasoning_output_tokens": 400,
              "total_tokens": 9000+output}
    counts.update(extra)
    return probe.observe(row("token_usage_record", {"thread_id": thread, "turn_id": turn,
        "response_id": response, "usage": counts,
        # Deliberately incorrect turn/thread aggregates must never become numerator.
        "turn_token_usage": {"output_tokens": 9000000},
        "thread_token_usage": {"output_tokens": 9000000}}, at=at))


class TokenRateTests(unittest.TestCase):
    def fixture(self):
        p = Probe(); start(p)
        item(p, "Reasoning", 1000, 5000)
        item(p, "CommandExecution", 5000, 55000, "tool")
        item(p, "AgentMessage", 55000, 58000, "message")
        return p

    def test_long_tool_gap_is_not_in_denominator(self):
        s = usage(self.fixture())
        self.assertEqual((s["outputTpsApprox"], s["modelMs"], s["toolMs"]), (200, 7000, 50000))
        self.assertEqual(s["modelSpanMs"], 57000)

    def test_overlapping_windows_use_union(self):
        p = Probe(); start(p)
        item(p, "Reasoning", 1000, 6000)
        item(p, "AgentMessage", 5000, 11000, "message")
        self.assertEqual(usage(p)["outputTpsApprox"], 140)

    def test_reasoning_is_already_inside_output_tokens(self):
        self.assertEqual(usage(self.fixture())["outputTpsApprox"], 200)

    def test_duplicate_response_id_is_not_counted_twice(self):
        p = self.fixture(); usage(p); usage(p)
        self.assertEqual(p.report()["samples"][0]["outputTpsApprox"], 200)
        self.assertEqual(p.counts["duplicate_responses"], 1)

    def test_equal_counts_with_distinct_response_ids_are_two_responses(self):
        p = self.fixture(); usage(p); usage(p, response="response-b")
        self.assertEqual(p.report()["samples"][0]["outputTpsApprox"], 400)

    def test_legacy_mirror_is_ignored(self):
        p = self.fixture(); usage(p)
        p.observe(row("event_msg", {"type": "token_count", "info": {"last_token_usage": {"output_tokens": 1400}}}))
        self.assertEqual(p.report()["samples"][0]["outputTpsApprox"], 200)

    def test_duplicate_model_item_is_idempotent(self):
        p = self.fixture(); item(p, "Reasoning", 1000, 5000)
        self.assertEqual(usage(p)["modelMs"], 7000)

    def test_inconsistent_duplicate_item_is_unavailable(self):
        p = self.fixture(); item(p, "Reasoning", 1000, 6000)
        self.assertIsNone(usage(p))

    def test_inconsistent_duplicate_response_is_unavailable(self):
        p = self.fixture(); usage(p); usage(p, output=1500)
        self.assertEqual(p.report()["turnsWithEstimate"], 0)

    def test_missing_turn_start_does_not_use_a_truncated_prefix(self):
        p = Probe(); item(p, "Reasoning", 1000, 5000)
        self.assertIsNone(usage(p))

    def test_missing_model_time_is_unavailable(self):
        p = Probe(); start(p)
        item(p, "CommandExecution", 1000, 5000)
        self.assertIsNone(usage(p))

    def test_zero_or_reversed_model_time_is_unavailable(self):
        for end in (1000, 999):
            p = Probe(); start(p); item(p, "Reasoning", 1000, end)
            self.assertIsNone(usage(p))

    def test_invalid_token_subcounts_are_rejected(self):
        self.assertIsNone(usage(self.fixture(), reasoning_output_tokens=1401))
        self.assertIsNone(usage(self.fixture(), cached_input_tokens=9001))

    def test_bool_tokens_are_not_numbers(self):
        self.assertIsNone(usage(self.fixture(), input_tokens=True))

    def test_input_volume_does_not_become_decode_speed(self):
        self.assertEqual(usage(self.fixture(), input_tokens=900000, total_tokens=901400)["outputTpsApprox"], 200)

    def test_threads_and_turns_do_not_mix(self):
        p = self.fixture(); usage(p)
        start(p, thread="thread-b"); item(p, "AgentMessage", 1000, 11000, thread="thread-b")
        usage(p, output=2000, thread="thread-b")
        self.assertEqual(sorted(x["outputTpsApprox"] for x in p.report()["samples"]), [200, 200])

    def test_replayed_task_start_does_not_clear_samples(self):
        p = self.fixture(); usage(p); start(p)
        self.assertEqual(p.report()["turnsWithEstimate"], 1)

    def test_no_new_usage_means_no_new_tps_update(self):
        p = self.fixture(); usage(p)
        count = p.counts["response_boundary_updates"]
        item(p, "AgentMessage", 100100, 105000, "pending")
        self.assertEqual(p.counts["response_boundary_updates"], count)

    def test_union_handles_nested_and_adjacent_intervals(self):
        self.assertEqual(union_ms([(10, 20), (12, 15), (20, 30), (35, 40)]), 25)

    def test_epoch_milliseconds_do_not_lose_a_boundary_to_float_rounding(self):
        self.assertEqual(event_ms({"timestamp": "2026-10-28T01:02:25.841Z"}), 1793149345841)

    def test_latest_response_and_whole_turn_are_different_metrics(self):
        p = Probe(); start(p)
        item(p, "Reasoning", 1000, 5000)
        usage(p, output=800, at=5000)
        item(p, "CommandExecution", 5000, 55000, "tool")
        item(p, "AgentMessage", 55000, 58000, "message")
        s = usage(p, output=300, reasoning_output_tokens=0, response="second", at=58000)
        self.assertEqual(s["lastResponseTpsApprox"], 100)
        self.assertEqual(s["outputTpsApprox"], 157.143)

    def test_crossing_a_previous_usage_boundary_is_not_matched_to_next_response(self):
        p = self.fixture(); usage(p)
        item(p, "AgentMessage", 90000, 105000, "overlap")
        s = usage(p, output=1000, response="second", at=106000)
        self.assertIsNone(s["lastResponseTpsApprox"])
        self.assertEqual(p.counts["ambiguous_response_timing"], 1)

    def test_replayed_previous_item_does_not_contaminate_next_response(self):
        p = Probe(); start(p); item(p, "Reasoning", 1000, 5000)
        usage(p, output=800, at=5000)
        item(p, "Reasoning", 1000, 5000)
        item(p, "AgentMessage", 55000, 58000, "message")
        s = usage(p, output=300, reasoning_output_tokens=0, response="second", at=58000)
        self.assertEqual(s["lastResponseTpsApprox"], 100)

    def test_update_cadence_does_not_mix_concurrent_threads(self):
        samples = [{"sourceIndex": 0, "asOfMs": 1000}, {"sourceIndex": 1, "asOfMs": 1500},
                   {"sourceIndex": 0, "asOfMs": 21000}, {"sourceIndex": 1, "asOfMs": 31500}]
        self.assertEqual(update_gaps(samples), [20, 30])


if __name__ == "__main__":
    unittest.main(verbosity=2)
