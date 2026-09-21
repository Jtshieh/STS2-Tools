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



def notification_recording():
    obs = observation(); obs['actions'][0]['id'] = 'proceed'
    parent = dict(actionId='proceed', source='NClickableControl.OnReleaseHandler', observation=obs)
    notice = dict(actionId='next_act', role='engine_notification', notificationSequence=1,
                  source='RunReplays.ActChangeSynchronizer.SetLocalPlayerReady', error=None,
                  attributionRevision='callback-scope-v1', relation='synchronous_callback', parentActionSequence=1)
    events = [('action_initiated', 1, parent),
              ('engine_notification', None, dict(notice, status='callback_entered')),
              ('engine_notification', None, dict(notice, status='callback_returned')),
              ('action_status', 1, {'status': 'callback_returned'}),
              ('action_successor', 1, {'observation': obs})]
    return [dict(schema='sts2-gui-semantic-v1', processRunId='process', rootRunId='root',
                 eventSequence=i, actionSequence=seq, kind=kind, data=d)
            for i, (kind, seq, d) in enumerate(events, 1)]


class ImportConsistency(unittest.TestCase):
    def test_notification_is_evidence_not_an_input(self):
        rows = notification_recording(); data = raw(rows)
        b = import_raw(data, 'synthetic', 'new', None, 1)
        self.assertEqual([c['actionId'] for c in b['commands']], ['proceed'])
        self.assertEqual(b['sourceRaw'].encode(), data)
        self.assertEqual(len(b['transformations']), 2)
        self.assertTrue(all(t['parentActionSequence'] == 1 and not t['execute'] for t in b['transformations']))
        self.assertEqual(b['replayEvidence']['status'], 'not_run')

    def test_unreliable_notification_attribution_rejected(self):
        for field, value in [('parentActionSequence', None), ('parentActionSequence', 99), ('parentActionSequence', []),
                             ('attributionRevision', None), ('relation', 'nearby_event'),
                             ('source', 'unknown'), ('role', 'nested_input'), ('notificationSequence', None)]:
            rows = notification_recording()
            for r in rows[1:3]: r['data'][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = notification_recording(); rows[2]['kind'] = 'observation'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = notification_recording(); rows[2]['data']['error'] = 'failed original callback'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = notification_recording(); rows[0]['data']['actionId'] = 'select_hand:0'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)

    def test_real_nested_input_is_retained_and_closed(self):
        rows = recording(); rows[2]['data']['status'] = 'entered_nested_decision'
        obs = observation(); obs['actions'][0]['id'] = 'select_hand:0'
        child = copy.deepcopy(rows[0]); child.update(eventSequence=4, actionSequence=2)
        child['data'] = dict(actionId='select_hand:0', observation=obs, parentActionSequence=1, role='nested_input')
        applied = copy.deepcopy(rows[1]); applied.update(eventSequence=5, actionSequence=2)
        applied['data']['status'] = 'selection_input_applied'
        closed = copy.deepcopy(rows[1]); closed.update(eventSequence=6, kind='nested_action_closed', data={})
        successor = copy.deepcopy(rows[2]); successor.update(eventSequence=7, actionSequence=2)
        successor['data'] = {'observation': obs}
        rows += [child, applied, closed, successor]
        b = import_raw(raw(rows), 'synthetic', 'new', None, 2)
        self.assertEqual(len(b['commands']), 2)
        self.assertEqual(b['commands'][1]['parentActionSequence'], 1)
        self.assertEqual(b['commands'][1]['role'], 'nested_input')
        rows[5]['kind'] = 'observation'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 2)

    def test_legacy_adapter_requires_its_source_and_save(self):
        from import_trace import LEGACY_ADAPTER, LEGACY_RUN_SHA256
        with self.assertRaisesRegex(ValueError, 'exact reviewed source'):
            import_raw(raw(recording()), 'synthetic', 'continue', LEGACY_RUN_SHA256, 1, LEGACY_ADAPTER)

    def test_unimplemented_bindings_and_failed_status_rejected(self):
        for aid in ('claim_relic:0', 'deselect_hand:0'):
            rows = recording(); rows[0]['data']['actionId'] = aid
            rows[0]['data']['observation']['actions'][0]['id'] = aid
            with self.assertRaisesRegex(ValueError, 'binding not implemented'):
                import_raw(raw(rows), 'synthetic', 'new', None, 1)
        rows = recording(); rows[1]['data']['status'] = 'failed'
        with self.assertRaises(ValueError): import_raw(raw(rows), 'synthetic', 'new', None, 1)

    def test_endpoint_keeps_actions_and_rejects_missing_receipts(self):
        from sts2_play import recorded_endpoint
        obs = observation(); terminal = dict(state=obs['state'], submittedCount=1)
        events = [dict(kind='action_response', value=dict(sequence=1, successor=obs))]
        self.assertEqual(recorded_endpoint(terminal, events, 1), obs)
        changed = copy.deepcopy(obs); changed['actions'] = []
        self.assertFalse(compare_gui.compare(obs, changed)['matched'])
        for bad in ([], events+events):
            with self.assertRaises(ValueError): recorded_endpoint(terminal, bad, 1)
        with self.assertRaises(ValueError): recorded_endpoint(dict(terminal, submittedCount=2), events, 1)

    def test_all_mechanical_card_fields_still_fail(self):
        a = observation(); a['state']['cardDetails'] = {
            'state': {'instance': 1, 'identityScope': 'recorder_process', 'upgrade': 1,
                      'affliction': {'id': 'synthetic', 'amount': 1},
                      'enchantment': {'id': 'synthetic', 'amount': 2, 'displayAmount': 2},
                      'keywords': ['retain']},
            'rendered': {'locked': True, 'description': 'Damage 5'}}
        changes = [('state', 'upgrade', 2), ('state', 'affliction', None),
                   ('state', 'enchantment', None), ('state', 'keywords', []),
                   ('rendered', 'locked', False), ('rendered', 'description', 'Damage 6')]
        for section, key, value in changes:
            compare_gui.card_ids.clear(); compare_gui.inverse_card_ids.clear()
            b = copy.deepcopy(a); b['state']['cardDetails'][section][key] = value
            with self.subTest(field=key): self.assertFalse(compare_gui.compare(a, b)['matched'])

if __name__ == '__main__': unittest.main()
