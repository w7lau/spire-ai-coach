"""Validate native resident/pool evidence independently of the probe's equality flags."""
import argparse
import json
from pathlib import Path
import statistics


EXPECTED = ['A', 'B', 'A', 'B', 'A']


def validate_resident(summary, expected_traces=None, expected_fixture=None):
    if (summary.get('timed_out') is not False or summary.get('exit_code') != 0
            or summary.get('source_unchanged') is not True or summary.get('runtime_errors') != []):
        raise ValueError('Worker did not complete without game errors')
    result = summary.get('result', {})
    branches = result.get('branches', [])
    if result.get('status') != 'native_resident_complete' or [b.get('route') for b in branches] != EXPECTED:
        raise ValueError('Incomplete resident branch sequence')
    known = {} if expected_traces is None else dict(expected_traces)
    fixture = branches[0]['result'].get('fixture_sha256')
    if not fixture:
        raise ValueError('Missing frozen fixture hash')
    if expected_fixture is not None and fixture != expected_fixture:
        raise ValueError('Worker used a different frozen fixture')
    root = None
    for index, branch in enumerate(branches):
        data = branch['result']
        if data.get('status') != 'native_route_complete' or data.get('fixture_sha256') != fixture:
            raise ValueError('Failed route or changed fixture')
        if index > 0 and data.get('restored_fixture') is not True:
            raise ValueError('Resident branch did not restore frozen fixture')
        trace = data.get('checkpoints', [])
        labels = ['root', 'BLOODLETTING', 'STRIKE_IRONCLAD', 'DEFEND_IRONCLAD', 'END_TURN'] if branch['route'] == 'A' else [
            'root', 'STRIKE_IRONCLAD', 'STRIKE_IRONCLAD', 'DEFEND_IRONCLAD', 'END_TURN']
        if [c.get('label') for c in trace] != labels or trace[-1].get('RoundNumber') != 2:
            raise ValueError('Missing action or full turn evidence')
        if any(not c.get('native_state_sha256') for c in trace):
            raise ValueError('Missing native synchronization fingerprint')
        if root is not None and trace[0] != root:
            raise ValueError('Branch root changed')
        root = trace[0]
        if branch['route'] in known and trace != known[branch['route']]:
            raise ValueError('Native trace differs from same-route reference')
        known[branch['route']] = trace
    return known


def compact(summary):
    result = summary['result']
    branches = result['branches']
    return dict(wall_ms=summary['wall_ms'], time_scale=result['time_scale'],
                instant=result.get('instant', False), keep_assets=result.get('keep_assets', False),
                skip_transitions=result.get('skip_transitions', False),
                warm_cycle_median_ms=statistics.median(b['cycle_ms'] for b in branches[1:]),
                branches=[{k: v for k, v in b.items() if k != 'result'} | {
                    'action_ms': b['result']['action_ms'], 'setup_ms': b['result']['setup_ms'],
                    'native_fingerprints': [c['native_state_sha256'] for c in b['result']['checkpoints']]
                } for b in branches],
                runtime_errors=summary['runtime_errors'], engine_errors=summary.get('engine_errors', []))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--normal', type=Path, required=True)
    parser.add_argument('--fast', type=Path, required=True)
    parser.add_argument('--pool', type=Path)
    parser.add_argument('--serial-pool', type=Path)
    parser.add_argument('--instant', type=Path)
    parser.add_argument('--keep-assets', type=Path)
    parser.add_argument('--skip-transitions', type=Path)
    parser.add_argument('--instant-pool', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    read = lambda p: json.loads(p.read_text(encoding='utf-8'))
    normal, fast = read(args.normal), read(args.fast)
    traces = validate_resident(normal)
    fixture = normal['result']['branches'][0]['result']['fixture_sha256']
    validate_resident(fast, traces, fixture)
    if normal['source_hashes'] != fast['source_hashes']:
        raise ValueError('Native source builds changed')
    report = dict(scope='Fixed synthetic singleplayer fixture; no arbitrary Mod isolation guarantee.',
                  normal=compact(normal), fast=compact(fast), traces=traces,
                  all_native_traces_identical=True)
    for label, path in [('instant', args.instant), ('keep_assets', args.keep_assets),
                        ('skip_transitions', args.skip_transitions)]:
        if path:
            summary = read(path)
            validate_resident(summary, traces, fixture)
            if summary['source_hashes'] != normal['source_hashes']:
                raise ValueError('Source builds changed')
            report[label] = compact(summary)
    for label, path in [('parallel_pool', args.pool), ('serial_pool', args.serial_pool), ('instant_pool', args.instant_pool)]:
        if path:
            pool = read(path)
            if len(pool['workers']) != 2:
                raise ValueError('Missing worker')
            for worker in pool['workers']:
                if worker['launcher_exit_code'] != 0:
                    raise ValueError('Failed worker launcher')
                validate_resident(worker['summary'], traces, fixture)
                if worker['summary']['source_hashes'] != normal['source_hashes']:
                    raise ValueError('Worker source builds differ')
            report[label] = dict(parallelism=pool['parallelism'],
                total_including_setup_ms=pool['total_including_setup_ms'],
                workers=[compact(w['summary']) for w in pool['workers']])
    if args.pool and args.serial_pool:
        report['measured_pool_speedup_including_setup'] = round(
            report['serial_pool']['total_including_setup_ms'] / report['parallel_pool']['total_including_setup_ms'], 3)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(all_native_traces_identical=True,
        normal_warm_ms=report['normal']['warm_cycle_median_ms'], fast_warm_ms=report['fast']['warm_cycle_median_ms'],
        pool_speedup=report.get('measured_pool_speedup_including_setup'))))


if __name__ == '__main__':
    main()
