import copy
import unittest
from verify_probe import assert_replay


class ReplayEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.source = dict(source_hashes={'game': 'build-a'}, request={'actions': ['strike']}, result={
            'fixture_sha256': 'fixture-a', 'checkpoints': [{'player': {'hp': 80}, 'native_state_sha256': 'rng-a'}]})
        self.replay = copy.deepcopy(self.source)
        self.replay['result']['restored_fixture'] = True

    def test_identical_native_trace(self):
        assert_replay(self.source, self.replay)

    def test_same_hp_different_native_state_is_not_a_pass(self):
        self.replay['result']['checkpoints'][0]['native_state_sha256'] = 'rng-b'
        with self.assertRaises(ValueError):
            assert_replay(self.source, self.replay)

    def test_new_fixture_is_not_a_restore(self):
        self.replay['result']['restored_fixture'] = False
        with self.assertRaises(ValueError):
            assert_replay(self.source, self.replay)

    def test_other_saved_fixture_rejected(self):
        self.replay['result']['fixture_sha256'] = 'fixture-b'
        with self.assertRaises(ValueError):
            assert_replay(self.source, self.replay)

    def test_changed_mod_build_rejected(self):
        self.replay['source_hashes']['game'] = 'build-b'
        with self.assertRaises(ValueError):
            assert_replay(self.source, self.replay)


if __name__ == '__main__':
    unittest.main()
