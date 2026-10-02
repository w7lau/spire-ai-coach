"""Prevent concurrent launchers from rewriting the same native worker directory."""
from contextlib import contextmanager
import os


@contextmanager
def worker_lock(root):
    if os.name != 'nt':
        raise RuntimeError('The native probe launcher requires Windows')
    import msvcrt
    with (root / '.worker.lock').open('a+b') as stream:
        if stream.seek(0, 2) == 0:
            stream.write(b'0')
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError('This worker directory is already in use') from error
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
