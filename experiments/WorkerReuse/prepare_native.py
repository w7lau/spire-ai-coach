"""Prepare a NEW private native installation. Never launches or enumerates processes."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def prepare(args):
    root = args.root.resolve()
    workspace = (Path.cwd() / 'work').resolve()
    if not root.is_relative_to(workspace) or root == workspace or root.exists():
        raise ValueError('Use a new directory strictly inside this checkout work directory')
    request_path = args.request.resolve()
    request = json.loads(request_path.read_text(encoding='utf-8-sig'))
    if not request.get('Replay') or any(request.get(k) for k in ('InitialPlan', 'VerifyCandidate', 'RecordedReplayProbe')):
        raise ValueError('Expected an unseeded frozen native request, not a save or answer route')
    if any(k.lower() in {'apikey', 'api_key', 'authorization', 'password', 'token'} for k in request):
        raise ValueError('Credentials are not accepted in the fixture')
    game_source = args.game.resolve()
    expected = {m.split(':', 1)[0] for m in request['LoadedMods']} - {'SpireAiCoach', 'sts2'}
    sources = {}
    for folder in args.workshop.resolve().iterdir():
        if not folder.is_dir():
            continue
        candidates = [folder, *[p for p in folder.iterdir() if p.is_dir()]]
        for candidate in candidates:
            for manifest in candidate.glob('*.json'):
                try:
                    data = json.loads(manifest.read_text(encoding='utf-8-sig'))
                except (ValueError, UnicodeError):
                    continue
                if data.get('id') in expected:
                    sources[data['id']] = candidate
    if set(sources) != expected:
        raise ValueError('Missing matching frozen-request Mod assets: ' + str(expected - set(sources)))
    root.mkdir(parents=True)
    (root / '.native-worker-reuse-owner').write_text('owned isolated native worker reuse acceptance v1', encoding='utf-8')
    game = root / 'game'
    game.mkdir()
    hashes = {}
    for path in game_source.iterdir():
        if path.is_file() and path.suffix.lower() in {'.exe', '.dll', '.pck', '.json'}:
            hashes['game/' + path.name] = digest(path)
            shutil.copy2(path, game / path.name)
        elif path.is_dir() and path.name.startswith('data_sts2_'):
            shutil.copytree(path, game / path.name)
            for source in path.rglob('*'):
                if source.is_file():
                    hashes['game/' + str(source.relative_to(game_source)).replace('\\', '/')] = digest(source)
    for ident, source in sources.items():
        shutil.copytree(source, game / 'mods' / ident)
        for path in source.rglob('*'):
            if path.is_file() and path.suffix.lower() in {'.dll', '.pck', '.json'}:
                hashes['mod/' + ident + '/' + str(path.relative_to(source)).replace('\\', '/')] = digest(path)
    repo = Path(__file__).resolve().parents[2]
    coach = game / 'mods' / 'SpireAiCoach'
    coach.mkdir()
    shutil.copy2(repo / 'src/SpireAiCoach.Mod/bin/Release/net9.0/SpireAiCoach.dll', coach)
    shutil.copy2(repo / 'SpireAiCoach.json', coach)
    shutil.copy2(request_path, root / 'frozen-request.json')
    installed_coach = game_source / 'mods/SpireAiCoach/SpireAiCoach.dll'
    record = {
        'frozen_request_sha256': digest(request_path),
        'private_coach_sha256': digest(coach / 'SpireAiCoach.dll'),
        'installed_coach_sha256_before': digest(installed_coach),
        'source_hashes': hashes,
        'mod_ids': sorted(sources),
        'userdata': 'new per-worker Roaming/Local directories created by production pool',
        'existing_process_query': False,
        'real_save_access': False,
        'real_installation_write': False,
        'native_processes_launched': 0,
    }
    (root / 'preparation.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
    # Verify every copied source byte before any executable can be launched.
    for key, expected_hash in hashes.items():
        if key.startswith('game/'):
            copied = game / key[5:]
        else:
            ident, relative = key[4:].split('/', 1)
            copied = game / 'mods' / ident / relative
        if digest(copied) != expected_hash:
            raise ValueError('Private copy changed: ' + key)
    print(json.dumps({'root': str(root), 'copied_mod_ids': sorted(sources), 'fixture_sha256': record['frozen_request_sha256'],
                      'installed_coach_sha256_before': record['installed_coach_sha256_before'], 'native_launches': 0}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--request', type=Path, required=True)
    parser.add_argument('--workshop', type=Path, required=True)
    prepare(parser.parse_args())
