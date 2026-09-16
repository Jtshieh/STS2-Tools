"""Synthetic protocol regressions only. No game, original saves or network."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import compare_gui
from import_trace import import_raw, validate_bundle
from prepare import ordinary
from protocol import validate

def observation():
    return {'state': {'phase': 'event', 'status': {'hp': 70, 'gold': 0}, 'text': 'Synthetic option'}, 'actions': [{'id': 'synthetic:choose', 'kind': 'event', 'label': 'Synthetic option', 'detail': None}]}

def recording():
    obs = observation()
    base = dict(schema='sts2-gui-semantic-v1', processRunId='synthetic-process', rootRunId='synthetic-root', actionSequence=1)
    return [dict(base, eventSequence=1, kind='action_initiated', data={'actionId': 'synthetic:choose', 'observation': obs}), dict(base, eventSequence=2, kind='action_status', data={'status': 'callback_returned'}), dict(base, eventSequence=3, kind='action_successor', data={'observation': obs})]

def raw(rows): return ('\n'.join(json.dumps(x) for x in rows)+'\n').encode()

class Contracts(unittest.TestCase):
    def setUp(self):
        compare_gui.card_ids.clear(); compare_gui.inverse_card_ids.clear()

    def test_complete_synthetic_segment(self):
        self.assertEqual(len(import_raw(raw(recording()), 'synthetic', 'new', None, 1)['commands']), 1)

    def test_omitted_input_or_successor_rejected(self):
        rows = recording()
        with self.assertRaises(ValueError): import_raw(raw(rows[1:]), 'synthetic', 'new', None, 1)
        with self.assertRaises(ValueError): import_raw(raw(rows[:2]), 'synthetic', 'new', None, 1)

    def test_gap_restart_unknown_and_failed_action_rejected(self):
        for kind in ['continuity_boundary', 'unmapped_action', 'unmapped_ui_input', 'unrecognized_event']:
            rows = recording(); rows[1]['kind'] = kind
            with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = recording(); rows[1]['data']['error'] = 'synthetic failure'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = recording(); rows[1]['processRunId'] = 'second-process'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = recording(); rows[1]['eventSequence'] = 9
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)

    def test_next_act_is_not_guessed(self):
        rows = recording(); rows[0]['data']['actionId'] = 'next_act'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)

    def test_mechanical_changes_not_normalized(self):
        a = observation()
        for key in ['hp', 'gold']:
            b = copy.deepcopy(a); b['state']['status'][key] += 1
            self.assertFalse(compare_gui.compare(a, b)['matched'])
        b = copy.deepcopy(a); b['actions'][0]['label'] = 'Changed reward'
        self.assertFalse(compare_gui.compare(a, b)['matched'])

    def test_card_state_survives_identity_mapping(self):
        a = observation(); a['state']['cardDetails'] = {'state': {'instance': 1, 'identityScope': 'recorder_process', 'enchantment': {'amount': 1}, 'upgrade': 1}, 'rendered': None}
        b = copy.deepcopy(a); b['state']['cardDetails']['state']['instance'] = 9
        self.assertTrue(compare_gui.compare(a, b)['matched'])
        b['state']['cardDetails']['state']['enchantment']['amount'] = 2
        self.assertFalse(compare_gui.compare(a, b)['matched'])

    def test_missing_endpoint_or_error_rejected(self):
        b = import_raw(raw(recording()), 'synthetic', 'new', None, 1); del b['endpoint']
        with self.assertRaises(ValueError): validate_bundle(b)
        row = dict(schema='sts2-action-log-v1', rootRunId='synthetic', processRunId='synthetic', slAttempt=0, recovery=None, sequence=0, status='interrupted', action=None, observation=None, error=None)
        with self.assertRaises(ValueError): validate(row)

    def test_symlink_and_hardlink_input_rejected(self):
        import os
        with tempfile.TemporaryDirectory() as d:
            p = Path(d); (p/'source').write_text('synthetic')
            (p/'link').symlink_to(p/'source')
            with self.assertRaises(ValueError): ordinary(p/'link')
            os.link(p/'source', p/'hard')
            with self.assertRaises(ValueError): ordinary(p/'hard')

if __name__ == '__main__': unittest.main()
