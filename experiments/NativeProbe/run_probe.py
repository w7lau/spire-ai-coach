"""Isolated native engine experiment. Never installs into the source game or reads real saves."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--game', required=True, type=Path)
    parser.add_argument('--hextech', required=True, type=Path)
    parser.add_argument('--ritsu', required=True, type=Path)
    parser.add_argument('--workspace', required=True, type=Path)
    parser.add_argument('--name', default='inventory')
    parser.add_argument('--mode', choices=['inventory', 'route'], default='inventory')
    parser.add_argument('--blood-armor', action='store_true')
    parser.add_argument('--restore-fixture', action='store_true')
    parser.add_argument('--encounter', default='SLIMES_WEAK')
    parser.add_argument('--actions', nargs='*', default=[])
    args = parser.parse_args()
    root = args.workspace.resolve()
    source = args.game.resolve()
    if not args.name or Path(args.name).name != args.name or args.name in {'.', '..'}:
        raise ValueError('Evidence name must be a single directory name')
    if root == source or root.is_relative_to(source) or source.is_relative_to(root):
        raise ValueError('Worker and original installation must be disjoint')
    if root.exists() and any(root.iterdir()) and not (root / '.spire-native-probe-owner').is_file():
        raise ValueError('Refusing an existing unowned directory')
    root.mkdir(parents=True, exist_ok=True)
    (root / '.spire-native-probe-owner').write_text('spire-ai-coach native feasibility v1', encoding='utf-8')
    game = root / 'game'
    game.mkdir(exist_ok=True)
    # Actual copies, never writable hardlinks into the user's installation.
    for p in source.iterdir():
        if p.is_file() and p.suffix.lower() in {'.exe', '.dll', '.pck', '.json'}:
            target = game / p.name
            if not target.exists() or target.stat().st_size != p.stat().st_size:
                shutil.copy2(p, target)
        elif p.is_dir() and p.name.startswith('data_sts2_'):
            shutil.copytree(p, game / p.name, dirs_exist_ok=True)
    for folder, source_mod in [('HextechRunes', args.hextech), ('RitsuLib', args.ritsu)]:
        shutil.copytree(source_mod, game / 'mods' / folder, dirs_exist_ok=True)
    mod = game / 'mods' / 'SpireNativeProbe'
    mod.mkdir(parents=True, exist_ok=True)
    here = Path(__file__).resolve().parent
    shutil.copy2(here / 'bin/Release/net9.0/SpireNativeProbe.dll', mod / 'SpireNativeProbe.dll')
    shutil.copy2(here / 'SpireNativeProbe.json', mod / 'SpireNativeProbe.json')
    # Start from an empty profile, with mod loading enabled only inside this private worker.
    roaming, local = root / 'Roaming', root / 'Local'
    settings = roaming / 'SlayTheSpire2/default/1/settings.save'
    settings.parent.mkdir(parents=True, exist_ok=True)
    config = json.loads(settings.read_text(encoding='utf-8-sig')) if settings.exists() else {}
    config['mod_settings'] = {'mods_enabled': True, 'mod_list': []}
    settings.write_text(json.dumps(config), encoding='utf-8')
    progress = settings.parent / 'modded/profile1/saves/progress.save'
    progress.parent.mkdir(parents=True, exist_ok=True)
    progress_data = json.loads(progress.read_text(encoding='utf-8-sig')) if progress.exists() else {'schema_version': 24}
    progress_data['enable_ftues'] = False
    progress_data['ftue_completed'] = ['combat_rules_ftue']
    progress.write_text(json.dumps(progress_data), encoding='utf-8')
    local.mkdir(exist_ok=True)
    request = dict(mode=args.mode, blood_armor=args.blood_armor, restore_fixture=args.restore_fixture,
                   encounter=args.encounter, actions=args.actions)
    (root / 'request.json').write_text(json.dumps(request), encoding='utf-8')
    evidence = root / 'evidence' / args.name
    evidence.mkdir(parents=True, exist_ok=False)
    for filename in ['result.json', 'inventory.json']:
        if (root / filename).exists():
            (root / filename).unlink()  # Exact owned outputs, never a recursive deletion.
    env = dict(os.environ, APPDATA=str(roaming), LOCALAPPDATA=str(local), SPIRE_NATIVE_PROBE_ROOT=str(root))
    watched = [source / 'SlayTheSpire2.exe', source / 'data_sts2_windows_x86_64/sts2.dll', args.hextech / 'lib/0.111.0/HextechRunes.dll']
    before = {str(p): digest(p) for p in watched}
    begin = time.perf_counter()
    with (evidence / 'stdout.log').open('wb') as stdout, (evidence / 'stderr.log').open('wb') as stderr:
        process = subprocess.Popen([str(game / 'SlayTheSpire2.exe'), '--headless', '--disable-vsync', '--max-fps', '120',
                                    '--force-steam=off', '--log-file', str(evidence / 'game.log')], cwd=game, env=env,
                                   stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
        timed_out = False
        try:
            process.wait(timeout=100)
        except subprocess.TimeoutExpired:
            timed_out = True
            process.kill()  # Only the child created and owned by this invocation.
            process.wait(timeout=10)
    summary = dict(request=request, pid=process.pid, exit_code=process.returncode, timed_out=timed_out,
                   wall_ms=round((time.perf_counter() - begin) * 1000, 2),
                   source_hashes=before, source_unchanged=all(digest(p) == before[str(p)] for p in watched))
    log_path = evidence / 'game.log'
    summary['runtime_errors'] = [line for line in log_path.read_text(encoding='utf-8', errors='replace').splitlines()
                                 if '[ERROR]' in line] if log_path.exists() else ['Missing game log']
    summary['engine_errors'] = [line for line in log_path.read_text(encoding='utf-8', errors='replace').splitlines()
                                if line.startswith('ERROR:')] if log_path.exists() else []
    for name in ['result.json', 'inventory.json']:
        if (root / name).exists():
            shutil.copy2(root / name, evidence / name)
    if (evidence / 'result.json').exists():
        summary['result'] = json.loads((evidence / 'result.json').read_text(encoding='utf-8'))
    (evidence / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if (not timed_out and process.returncode == 0 and summary['source_unchanged'] and not summary['runtime_errors']
                 and summary.get('result', {}).get('status') in {'inventory_ready', 'native_route_complete'}) else 1


if __name__ == '__main__':
    raise SystemExit(main())
