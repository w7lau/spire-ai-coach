"""Run integration in an existing owned NativeProbe workspace, never in the real game."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

here = Path(__file__).resolve().parent
sys.path.insert(0, str(here.parent / 'NativeProbe'))
from worker_lock import worker_lock

parser = argparse.ArgumentParser()
parser.add_argument('--workspace', type=Path, required=True)
parser.add_argument('--quick', action='store_true')
parser.add_argument('--benchmark', action='store_true')
parser.add_argument('--features', action='store_true')
parser.add_argument('--optimization', action='store_true')
parser.add_argument('--execution', action='store_true')
parser.add_argument('--skip-final-verification', action='store_true')
parser.add_argument('--choices', action='store_true')
parser.add_argument('--fallback', action='store_true')
parser.add_argument('--mechanics', action='store_true')
parser.add_argument('--mechanic-cases')
parser.add_argument('--replay', type=Path)
parser.add_argument('--game', type=Path)
parser.add_argument('--mods', type=Path)
parser.add_argument('--seed-result', type=Path)
parser.add_argument('--speed-benchmark', action='store_true')
parser.add_argument('--visual-benchmark', action='store_true')
parser.add_argument('--checkpoint', action='store_true')
parser.add_argument('--workers', type=int)
parser.add_argument('--shared-work', choices=['on', 'off'])
parser.add_argument('--search-order', choices=['monte-carlo', 'limited', 'depth', 'portfolio', 'turn-frontier'])
parser.add_argument('--correlated-rollouts', action='store_true')
parser.add_argument('--search-cases', help='One to four comma-separated baseline/correlated comparisons in one owned pool')
parser.add_argument('--lean-checksum-test', action='store_true')
parser.add_argument('--work-benchmark', action='store_true')
parser.add_argument('--settle-benchmark', action='store_true')
parser.add_argument('--quality-benchmark', action='store_true')
parser.add_argument('--native-data-probe', action='store_true')
parser.add_argument('--data-combat-benchmark', action='store_true')
parser.add_argument('--death-route', action='store_true')
parser.add_argument('--data-combat', action='store_true')
parser.add_argument('--data-run', action='store_true')
parser.add_argument('--execution-search-benchmark', action='store_true')
parser.add_argument('--bootstrap-benchmark', action='store_true')
parser.add_argument('--bootstrap-reverse', action='store_true')
parser.add_argument('--recorded-replay', type=Path)
parser.add_argument('--numerical-benchmark', action='store_true')
parser.add_argument('--early-stop-test', action='store_true')
parser.add_argument('--results-dir', type=Path)
parser.add_argument('--overhead-benchmark', action='store_true')
parser.add_argument('--verification-benchmark', action='store_true')
parser.add_argument('--algorithm-test', action='store_true')
parser.add_argument('--final-verification-test', action='store_true')
parser.add_argument('--concurrency-test', action='store_true')
parser.add_argument('--incident-verification', action='store_true')
args = parser.parse_args()
if args.incident_verification and (not args.replay or not args.seed_result):
    parser.error('--incident-verification requires --replay and --seed-result')
if args.final_verification_test and (not args.replay or not args.seed_result):
    parser.error('--final-verification-test requires --replay and --seed-result')

if args.verification_benchmark and (not args.replay or not args.seed_result):
    parser.error('--verification-benchmark requires --replay and --seed-result')
if args.algorithm_test and (not args.replay or args.seed_result or args.recorded_replay):
    parser.error('--algorithm-test requires an unseeded --replay')
if args.overhead_benchmark and (not args.replay or not args.seed_result):
    parser.error('--overhead-benchmark requires --replay and --seed-result')
if args.numerical_benchmark and (not args.replay or not args.recorded_replay):
    parser.error('--numerical-benchmark requires --replay and --recorded-replay')
if args.early_stop_test and (not args.replay or not args.seed_result):
    parser.error('--early-stop-test requires --replay and --seed-result')
if args.workers is not None and not 1 <= args.workers <= 16:
    parser.error('--workers must be between 1 and 16')
if args.search_order and not args.replay:
    parser.error('--search-order requires --replay')
if args.correlated_rollouts and (not args.replay or args.seed_result or args.recorded_replay):
    parser.error('--correlated-rollouts requires an unseeded --replay')
if args.search_cases and (not args.replay or args.seed_result or args.recorded_replay):
    parser.error('--search-cases requires an unseeded --replay')
if args.lean_checksum_test and (not args.replay or not args.seed_result or args.search_cases):
    parser.error('--lean-checksum-test requires --replay and a fixed --seed-result')
if args.work_benchmark and (not args.replay or not args.seed_result):
    parser.error('--work-benchmark requires --replay and --seed-result')
if args.settle_benchmark and (not args.replay or not args.seed_result):
    parser.error('--settle-benchmark requires --replay and --seed-result')
if args.quality_benchmark and (not args.replay or not args.seed_result):
    parser.error('--quality-benchmark requires --replay and --seed-result')
if args.native_data_probe and (not args.replay or not args.seed_result):
    parser.error('--native-data-probe requires --replay and --seed-result')
if args.data_combat_benchmark and (not args.replay or not args.seed_result):
    parser.error('--data-combat-benchmark requires --replay and --seed-result')
if args.death_route and not args.data_combat_benchmark:
    parser.error('--death-route requires --data-combat-benchmark')
if args.execution_search_benchmark and (not args.replay or not args.seed_result):
    parser.error('--execution-search-benchmark requires --replay and --seed-result')
if args.bootstrap_benchmark and (not args.replay or not args.seed_result):
    parser.error('--bootstrap-benchmark requires --replay and --seed-result')
if args.bootstrap_reverse and not args.bootstrap_benchmark:
    parser.error('--bootstrap-reverse requires --bootstrap-benchmark')
if args.visual_benchmark and (not args.replay or not args.seed_result or args.speed_benchmark):
    parser.error('--visual-benchmark requires --replay and --seed-result, without --speed-benchmark')
root = args.workspace.resolve()
if not (root / '.spire-native-probe-owner').is_file() or not (root / 'fixture.json').is_file():
    raise ValueError('Prepare an owned synthetic NativeProbe workspace first')
with worker_lock(root):
    repo = here.parent.parent
    coach = root / 'game/mods/SpireAiCoach'
    coach.mkdir(exist_ok=True)
    shutil.copy2(repo / 'src/SpireAiCoach.Mod/bin/Release/net9.0/SpireAiCoach.dll', coach)
    shutil.copy2(repo / 'SpireAiCoach.json', coach)
    integration = root / 'game/mods/SpireLocalIntegration'
    integration.mkdir(exist_ok=True)
    shutil.copy2(here / 'bin/Release/net9.0/SpireLocalIntegration.dll', integration)
    shutil.copy2(here / 'bin/Release/net9.0/SpireLocalIntegration.pdb', integration)
    (integration / 'SpireLocalIntegration.json').write_text(json.dumps(dict(
        id='SpireLocalIntegration', name='Isolated local integration test', author='w7lau',
        description='Synthetic capture, search and read-only integration verification', version='0.0.1',
        has_dll=True, has_pck=False, affects_gameplay=False,
        dependencies=[dict(id='SpireAiCoach', min_version='0.4.0')])), encoding='utf-8')
    for name in ['integration-success', 'integration-error.txt', 'integration-result.json',
                 'integration-algorithm-private.json', 'integration-algorithm-summary.json']:
        (root / name).unlink(missing_ok=True)
    if args.incident_verification:
        for name in ['integration-incident-summary.json', 'integration-incident-private.json',
                     'integration-incident-rejection-private.json']:
            (root / name).unlink(missing_ok=True)
    if args.search_cases:
        for name in ['integration-self-search-summary.json', *[f'integration-self-search-private-{i}.json' for i in range(4)]]:
            (root / name).unlink(missing_ok=True)
    if args.lean_checksum_test:
        for name in ['integration-lean-checksum-summary.json', 'integration-lean-checksum-private-0.json', 'integration-lean-checksum-private-1.json']:
            (root / name).unlink(missing_ok=True)
    env = dict(os.environ, APPDATA=str(root / 'Roaming'), LOCALAPPDATA=str(root / 'Local'),
               SPIRE_LOCAL_INTEGRATION=str(root))
    env.pop('SPIRE_NATIVE_PROBE_ROOT', None)
    env.pop('SPIRE_COACH_WORKER', None)
    env['SPIRE_LOCAL_INTEGRATION_QUICK'] = '1' if args.quick else '0'
    env['SPIRE_LOCAL_BENCHMARK'] = '1' if args.benchmark else '0'
    env['SPIRE_LOCAL_FEATURES'] = '1' if args.features else '0'
    env['SPIRE_LOCAL_OPTIMIZATION'] = '1' if args.optimization else '0'
    env['SPIRE_LOCAL_EXECUTION'] = '1' if args.execution else '0'
    env['SPIRE_LOCAL_SKIP_FINAL_VERIFICATION'] = '1' if args.skip_final_verification else '0'
    env['SPIRE_LOCAL_CHOICES'] = '1' if args.choices else '0'
    env['SPIRE_LOCAL_FALLBACK'] = '1' if args.fallback else '0'
    env['SPIRE_LOCAL_MECHANICS'] = '1' if args.mechanics else '0'
    if args.mechanic_cases:
        env['SPIRE_LOCAL_MECHANICS_CASES'] = args.mechanic_cases
    env['SPIRE_LOCAL_REPLAY'] = str(args.replay.resolve()) if args.replay else ''
    env['SPIRE_LOCAL_REPLAY_GAME'] = str(args.game.resolve()) if args.game else ''
    env['SPIRE_LOCAL_REPLAY_MODS'] = str(args.mods.resolve()) if args.mods else ''
    env['SPIRE_LOCAL_SEED_RESULT'] = str(args.seed_result.resolve()) if args.seed_result else ''
    env['SPIRE_LOCAL_SPEED_BENCHMARK'] = '1' if args.speed_benchmark else '0'
    env['SPIRE_LOCAL_VISUAL_BENCHMARK'] = '1' if args.visual_benchmark else '0'
    env['SPIRE_LOCAL_CHECKPOINT'] = '1' if args.checkpoint else '0'
    env['SPIRE_LOCAL_WORKERS'] = str(args.workers) if args.workers is not None else ''
    env['SPIRE_LOCAL_SHARED_WORK'] = args.shared_work or ''
    env['SPIRE_LOCAL_SEARCH_ORDER'] = {
        'monte-carlo': 'MonteCarlo', 'limited': 'LimitedDiscrepancy',
        'depth': 'DepthDiscrepancy', 'portfolio': 'DiscrepancyPortfolio',
        'turn-frontier': 'TurnFrontier',
    }.get(args.search_order, '')
    env['SPIRE_LOCAL_CORRELATED_ROLLOUTS'] = '1' if args.correlated_rollouts else '0'
    env['SPIRE_LOCAL_SEARCH_CASES'] = args.search_cases or ''
    env['SPIRE_LOCAL_LEAN_CHECKSUM_TEST'] = '1' if args.lean_checksum_test else '0'
    env['SPIRE_LOCAL_WORK_BENCHMARK'] = '1' if args.work_benchmark else '0'
    env['SPIRE_LOCAL_SETTLE_BENCHMARK'] = '1' if args.settle_benchmark else '0'
    env['SPIRE_LOCAL_QUALITY_BENCHMARK'] = '1' if args.quality_benchmark else '0'
    env['SPIRE_LOCAL_NATIVE_DATA_PROBE'] = '1' if args.native_data_probe else '0'
    env['SPIRE_LOCAL_DATA_COMBAT_BENCHMARK'] = '1' if args.data_combat_benchmark else '0'
    env['SPIRE_LOCAL_DEATH_ROUTE'] = '1' if args.death_route else '0'
    env['SPIRE_LOCAL_DATA_COMBAT'] = '1' if args.data_combat else '0'
    env['SPIRE_LOCAL_DATA_RUN'] = '1' if args.data_run else '0'
    env['SPIRE_LOCAL_EXECUTION_SEARCH_BENCHMARK'] = '1' if args.execution_search_benchmark else '0'
    env['SPIRE_LOCAL_BOOTSTRAP_BENCHMARK'] = '1' if args.bootstrap_benchmark else '0'
    env['SPIRE_LOCAL_BOOTSTRAP_REVERSE'] = '1' if args.bootstrap_reverse else '0'
    env['SPIRE_LOCAL_RECORDED_REPLAY'] = str(args.recorded_replay.resolve()) if args.recorded_replay else ''
    env['SPIRE_LOCAL_NUMERICAL_BENCHMARK'] = '1' if args.numerical_benchmark else '0'
    env['SPIRE_LOCAL_EARLY_STOP_TEST'] = '1' if args.early_stop_test else '0'
    env['SPIRE_LOCAL_OVERHEAD_BENCHMARK'] = '1' if args.overhead_benchmark else '0'
    env['SPIRE_LOCAL_VERIFICATION_BENCHMARK'] = '1' if args.verification_benchmark else '0'
    env['SPIRE_LOCAL_ALGORITHM_TEST'] = '1' if args.algorithm_test else '0'
    env['SPIRE_LOCAL_FINAL_VERIFICATION_TEST'] = '1' if args.final_verification_test else '0'
    env['SPIRE_LOCAL_CONCURRENCY_TEST'] = '1' if args.concurrency_test else '0'
    env['SPIRE_LOCAL_INCIDENT_VERIFICATION'] = '1' if args.incident_verification else '0'
    settings = root / 'Roaming/SlayTheSpire2/default/1/settings.save'
    settings_data = json.loads(settings.read_text(encoding='utf-8-sig')) if settings.exists() else {}
    settings_data.update(volume_master=0, volume_bgm=0, volume_sfx=0, volume_ambience=0)
    settings.write_text(json.dumps(settings_data), encoding='utf-8')
    with (root / 'integration-stdout.log').open('wb') as output:
        process = subprocess.Popen([str(root / 'game/SlayTheSpire2.exe'), '--headless', '--audio-driver', 'Dummy', '--max-fps', '120',
                                    '--force-steam=off', '--log-file', str(root / 'integration-game.log')],
            cwd=root / 'game', env=env, stdout=output, stderr=output, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            process.wait(timeout=300)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
            raise
    if (root / 'integration-error.txt').exists():
        print((root / 'integration-error.txt').read_text(encoding='utf-8'))
    passed = (root / 'integration-success').is_file()
    if args.results_dir:
        # Preserve this run while the workspace lock is still held. Other local
        # experiments may reuse the host immediately after it exits.
        args.results_dir.mkdir(parents=True, exist_ok=True)
        names = ['integration-replay-private.json', 'integration-replay-summary.json',
                     'integration-algorithm-private.json', 'integration-algorithm-summary.json',
                     'integration-concurrency-summary.json', 'integration-concurrency-MonteCarlo-private.json',
                     'integration-concurrency-TurnFrontier-private.json',
                     'integration-error.txt', 'integration-stdout.log', 'integration-game.log']
        if args.incident_verification:
            names = ['integration-incident-summary.json', 'integration-incident-private.json',
                     'integration-incident-rejection-private.json', 'integration-success',
                     'integration-error.txt', 'integration-stdout.log', 'integration-game.log']
        if args.search_cases:
            names = ['integration-self-search-summary.json', *[f'integration-self-search-private-{i}.json' for i in range(4)],
                     'integration-success', 'integration-error.txt', 'integration-stdout.log', 'integration-game.log']
        if args.lean_checksum_test:
            names = ['integration-lean-checksum-summary.json', 'integration-lean-checksum-private-0.json', 'integration-lean-checksum-private-1.json',
                     'integration-success', 'integration-error.txt', 'integration-stdout.log', 'integration-game.log']
        for name in names:
            if (root / name).is_file():
                shutil.copy2(root / name, args.results_dir / name)
    print(json.dumps(dict(exit_code=process.returncode, passed=passed)))
    raise SystemExit(0 if passed and process.returncode == 0 else 1)
