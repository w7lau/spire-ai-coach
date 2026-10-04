"""Read-only verification of the owned native run and unchanged source assets."""
import argparse
import json
from pathlib import Path
from prepare_native import digest


def verify(args):
    root = args.root.resolve()
    workspace = (Path.cwd() / 'work').resolve()
    if not root.is_relative_to(workspace) or not (root / '.native-worker-reuse-owner').is_file():
        raise ValueError('Expected the owned private acceptance directory')
    before = json.loads((root / 'preparation.json').read_text(encoding='utf-8'))
    mods = {}
    for manifest in args.workshop.rglob('*.json'):
        try:
            ident = json.loads(manifest.read_text(encoding='utf-8-sig')).get('id')
        except (ValueError, UnicodeError):
            continue
        if ident in before['mod_ids']:
            if ident in mods:
                raise ValueError('Ambiguous source Mod: ' + ident)
            mods[ident] = manifest.parent
    for key, expected in before['source_hashes'].items():
        if key.startswith('game/'):
            source = args.game / key[5:]
        else:
            ident, relative = key[4:].split('/', 1)
            source = mods[ident] / relative
        if digest(source) != expected:
            raise ValueError('Source asset changed: ' + key)
    installed_hash = digest(args.game / 'mods/SpireAiCoach/SpireAiCoach.dll')
    if installed_hash != before['installed_coach_sha256_before']:
        raise ValueError('Installed Coach changed')
    if digest(root / 'frozen-request.json') != before['frozen_request_sha256']:
        raise ValueError('Frozen fixture changed')
    logs = list((root / '.spire-ai-coach-workers').rglob('game.log'))
    if len(logs) != 1:
        raise ValueError('Expected one owned native worker log')
    lines = logs[0].read_text(encoding='utf-8', errors='replace').splitlines()
    errors = [line for line in lines if line.startswith('ERROR:')]
    fatal_files = list((root / '.spire-ai-coach-workers').rglob('fatal.txt'))
    runtime_errors = [line for line in lines if '[ERROR]' in line or 'ERROR: FATAL:' in line
                      or line.startswith('System.') and 'Exception:' in line]
    summary = json.loads((root / 'native-summary.json').read_text(encoding='utf-8-sig'))
    record = dict(passed=summary['passed'] and not runtime_errors and not fatal_files,
                  unchanged_source_asset_count=len(before['source_hashes']),
                  installed_coach_sha256_before=before['installed_coach_sha256_before'],
                  installed_coach_sha256_after=installed_hash,
                  private_coach_sha256=before['private_coach_sha256'],
                  frozen_request_sha256=before['frozen_request_sha256'],
                  game_log_sha256=digest(logs[0]), runtime_error_filter_matches=len(runtime_errors),
                  fatal_files=len(fatal_files), engine_error_lines=errors,
                  existing_process_query=False, real_save_access=False,
                  real_installation_write=False, provider_calls=False)
    (root / 'native-verification.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(record))
    if not record['passed']:
        raise ValueError('Owned native acceptance failed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--workshop', type=Path, required=True)
    verify(parser.parse_args())
