"""Synthetic coverage of capture completeness versus replay validation."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from export_trace import export


def recording(action_id="play_card:0:0"):
    obs = {"state": {"phase": "combat", "cardDetails": {
        "state": {"instance": 1, "enchantment": {"id": "synthetic", "amount": 2}}}},
        "actions": [{"id": action_id, "kind": "synthetic", "detail": None}]}
    events = [
        ("recorder_initialized", {"observationRevision": "card-state-v2"}, None),
        ("ready", {}, None), ("run_started", {}, None),
        ("action_initiated", {"actionId": action_id, "observation": obs}, 1),
        ("action_status", {"status": "callback_returned"}, 1),
        ("action_successor", {"status": "next_decision_observed", "observation": obs}, 1),
    ]
    return [dict(schema="sts2-gui-semantic-v1", processRunId="synthetic-process",
                 rootRunId="synthetic-root", eventSequence=i, decisionId=1,
                 utc="2000-01-01T00:00:00Z", kind=kind, data=data, actionSequence=seq)
            for i, (kind, data, seq) in enumerate(events, 1)]


class ExportReport(unittest.TestCase):
    def run_export(self, rows, debug=False):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); session = root / "session"; session.mkdir()
            raw = ("\n".join(json.dumps(r) for r in rows) + "\n").encode()
            (session / "actions.jsonl").write_bytes(raw)
            if debug:
                (session / "launch.json").write_text('{"recordingMode":"debug_console"}')
            output = root / "export"
            report = export(session, output)
            self.assertEqual(raw, (output / "actions.raw.jsonl").read_bytes())
            self.assertEqual(report, json.loads((output / "conversion.json").read_text()))
            return report, [json.loads(s) for s in (output / "commands.proposed.jsonl").read_text().splitlines()]

    def test_card_state_capture_does_not_claim_replay(self):
        rows = recording(); report, commands = self.run_export(rows)
        self.assertEqual(report["schema"], "sts2-gui-conversion-v2")
        self.assertTrue(report["structuralReady"])
        self.assertEqual(report["issues"], [])
        self.assertEqual(report["observationComparison"]["status"], "not_run")
        self.assertEqual(report["linuxReplay"]["status"], "not_run")
        self.assertEqual(report["linuxReplay"]["input"], "actions.raw.jsonl")
        self.assertEqual(report["crossPlatformFidelity"], "not tested")
        self.assertEqual(commands[0]["observation"]["state"], rows[3]["data"]["observation"]["state"])
        self.assertEqual(report["requiredLinuxAdaptation"], [])

    def test_adapter_reviews_are_separate_and_inputs_preserved(self):
        for aid in ["next_act", "claim_relic:0", "deselect_hand:0"]:
            with self.subTest(action=aid):
                report, commands = self.run_export(recording(aid))
                self.assertTrue(report["structuralReady"])
                self.assertEqual(report["issues"], [])
                self.assertEqual(report["requiredActionBindings"][0]["actionId"], aid)
                self.assertTrue(report["requiredActionBindings"][0]["reason"])
                self.assertEqual(commands[0]["actionId"], aid)
                self.assertEqual(report["linuxReplay"]["status"], "not_run")

    def test_incomplete_capture_still_fails(self):
        cases = []
        cases.append(recording()[:-1])
        rows = recording(); rows[-1]["eventSequence"] += 1; cases.append(rows)
        rows = recording(); rows[4]["data"]["error"] = "synthetic failure"; cases.append(rows)
        rows = recording(); rows[4]["kind"] = "unknown_callback"; cases.append(rows)
        rows = recording(); rows[-1]["processRunId"] = "second-process"; cases.append(rows)
        rows = recording(); rows[-1]["data"]["status"] = "entered_nested_decision"; cases.append(rows)
        for rows in cases:
            with self.subTest(rows=rows):
                report, _ = self.run_export(rows)
                self.assertFalse(report["structuralReady"])
                self.assertTrue(report["issues"])

    def test_continuity_boundary_is_retained(self):
        rows = recording()
        boundary = copy.deepcopy(rows[-1])
        boundary.update(eventSequence=7, kind="continuity_boundary", data={"reason":"returned_to_menu"})
        report, _ = self.run_export(rows + [boundary])
        self.assertFalse(report["structuralReady"])
        self.assertIn("continuity boundary: returned_to_menu", report["issues"])

    def test_debug_capture_is_not_ordinary_replay(self):
        report, _ = self.run_export(recording(), debug=True)
        self.assertTrue(report["structuralReady"])
        self.assertEqual(report["recordingMode"], "debug_console")
        self.assertEqual(report["linuxReplay"]["status"], "not_applicable")

    def test_malformed_event_is_reported(self):
        report, _ = self.run_export(recording() + [None])
        self.assertFalse(report["structuralReady"])
        self.assertIn("invalid semantic event envelope", report["issues"])


if __name__ == "__main__":
    unittest.main()
