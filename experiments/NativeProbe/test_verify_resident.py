import copy
import unittest
from verify_resident import validate_resident


def evidence():
    branches = []
    for index, route in enumerate(['A', 'B', 'A', 'B', 'A']):
        labels = ['root', 'BLOODLETTING' if route == 'A' else 'STRIKE_IRONCLAD',
                  'STRIKE_IRONCLAD', 'DEFEND_IRONCLAD', 'END_TURN']
        trace = [dict(label=label, RoundNumber=2 if label == 'END_TURN' else 1,
                      native_state_sha256='shared-root' if label == 'root' else route + str(i), hp=80)
                 for i, label in enumerate(labels)]
        branches.append(dict(route=route, root_matches=True, trace_matches=True, result=dict(
            status='native_route_complete', fixture_sha256='frozen', restored_fixture=index > 0,
            checkpoints=trace)))
    return dict(exit_code=0, timed_out=False, source_unchanged=True, runtime_errors=[],
                result=dict(status='native_resident_complete', branches=branches))


class ResidentEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.data = evidence()

    def test_complete_replay(self):
        self.assertEqual(set(validate_resident(self.data)), {'A', 'B'})

    def test_reported_success_cannot_hide_rng_divergence(self):
        self.data['result']['branches'][2]['result']['checkpoints'][-1]['native_state_sha256'] = 'changed'
        with self.assertRaises(ValueError):
            validate_resident(self.data)

    def test_other_speed_must_match_reference(self):
        reference = validate_resident(self.data)
        changed = copy.deepcopy(self.data)
        for branch in changed['result']['branches']:
            branch['result']['checkpoints'][0]['hp'] = 79
        with self.assertRaises(ValueError):
            validate_resident(changed, reference)

    def test_missing_native_fingerprint_rejected(self):
        self.data['result']['branches'][0]['result']['checkpoints'][0].pop('native_state_sha256')
        with self.assertRaises(ValueError):
            validate_resident(self.data)

    def test_partial_batch_rejected(self):
        self.data['result']['branches'].pop()
        with self.assertRaises(ValueError):
            validate_resident(self.data)

    def test_game_error_rejected_even_with_full_result(self):
        self.data['runtime_errors'] = ['[ERROR] Combat turn loop died']
        with self.assertRaises(ValueError):
            validate_resident(self.data)

    def test_changed_fixture_rejected(self):
        self.data['result']['branches'][1]['result']['fixture_sha256'] = 'other'
        with self.assertRaises(ValueError):
            validate_resident(self.data)

    def test_regenerated_branch_is_not_restore(self):
        self.data['result']['branches'][3]['result']['restored_fixture'] = False
        with self.assertRaises(ValueError):
            validate_resident(self.data)

    def test_another_workers_fixture_rejected(self):
        with self.assertRaises(ValueError):
            validate_resident(self.data, expected_fixture='another-worker-fixture')


if __name__ == '__main__':
    unittest.main()
