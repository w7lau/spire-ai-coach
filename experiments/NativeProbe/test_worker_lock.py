import os
from pathlib import Path
import tempfile
import unittest
from worker_lock import worker_lock


@unittest.skipUnless(os.name == 'nt', 'Native launcher is Windows only')
class WorkerLockTests(unittest.TestCase):
    def test_same_worker_cannot_be_used_twice(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with worker_lock(root):
                with self.assertRaises(RuntimeError):
                    with worker_lock(root):
                        self.fail('Competing launcher acquired the same worker')
            with worker_lock(root):
                pass

    def test_independent_workers_can_run_concurrently(self):
        with tempfile.TemporaryDirectory() as directory:
            a, b = Path(directory) / 'a', Path(directory) / 'b'
            a.mkdir()
            b.mkdir()
            with worker_lock(a), worker_lock(b):
                pass


if __name__ == '__main__':
    unittest.main()
