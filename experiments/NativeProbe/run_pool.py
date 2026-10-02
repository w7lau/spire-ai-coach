"""Bounded parallel native workers. Threads schedule; each Godot instance owns its process/state."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    for name in ['game', 'hextech', 'ritsu', 'workspace', 'fixture-from']:
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--name', required=True)
    parser.add_argument('--parallelism', choices=[1, 2], type=int, default=2)
    parser.add_argument('--time-scale', choices=[1, 4], type=int, default=1)
    parser.add_argument('--instant', action='store_true')
    parser.add_argument('--keep-assets', action='store_true')
    parser.add_argument('--skip-transitions', action='store_true')
    args = parser.parse_args()
    if not args.name or Path(args.name).name != args.name or args.name in {'.', '..'}:
        raise ValueError('Invalid evidence name')
    root = args.workspace.resolve()
    source = args.game.resolve()
    if root == source or root.is_relative_to(source) or source.is_relative_to(root):
        raise ValueError('Pool and game installation must be disjoint')
    marker = root / '.spire-native-pool-owner'
    if root.exists() and any(root.iterdir()) and not marker.is_file():
        raise ValueError('Refusing an unowned pool directory')
    root.mkdir(parents=True, exist_ok=True)
    marker.write_text('spire-ai-coach native pool v1', encoding='utf-8')
    report_path = root / (args.name + '.json')
    if report_path.exists():
        raise ValueError('Evidence already exists')
    script = Path(__file__).with_name('run_probe.py')

    def execute(index):
        worker = root / ('worker-' + str(index))
        command = [sys.executable, str(script), '--game', str(args.game), '--hextech', str(args.hextech),
                   '--ritsu', str(args.ritsu), '--workspace', str(worker), '--name', args.name,
                   '--mode', 'resident', '--blood-armor', '--restore-fixture',
                   '--fixture-from', str(args.fixture_from), '--time-scale', str(args.time_scale)]
        if args.instant:
            command.append('--instant')
        if args.keep_assets:
            command.append('--keep-assets')
        if args.skip_transitions:
            command.append('--skip-transitions')
        begin = time.perf_counter()
        # Each launcher has a bounded native-child timeout and records its own evidence.
        process = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace')
        result_path = worker / 'evidence' / args.name / 'summary.json'
        data = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else None
        return dict(index=index, launcher_exit_code=process.returncode,
                    including_setup_ms=round((time.perf_counter() - begin) * 1000, 2),
                    evidence=str(result_path), summary=data,
                    launcher_error=process.stderr if process.returncode != 0 else '')

    begin = time.perf_counter()
    with ThreadPoolExecutor(max_workers=args.parallelism) as pool:
        workers = list(pool.map(execute, range(2)))
    report = dict(parallelism=args.parallelism, time_scale=args.time_scale, workers=workers,
                  total_including_setup_ms=round((time.perf_counter() - begin) * 1000, 2))
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'workers'}, ensure_ascii=False))
    print(json.dumps([dict(index=w['index'], exit_code=w['launcher_exit_code'],
                          including_setup_ms=w['including_setup_ms']) for w in workers]))
    return 0 if all(w['launcher_exit_code'] == 0 for w in workers) else 1


if __name__ == '__main__':
    raise SystemExit(main())
