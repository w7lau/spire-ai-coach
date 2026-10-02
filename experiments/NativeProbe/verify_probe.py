"""Compare native experiment evidence, never treating visible HP alone as replay equality."""
import argparse
import json
from pathlib import Path


def read_valid(path):
    data = json.loads((path / 'summary.json').read_text(encoding='utf-8'))
    if (data.get('timed_out') is not False or data.get('exit_code') != 0
            or data.get('source_unchanged') is not True or data.get('runtime_errors') != []
            or data.get('result', {}).get('status') != 'native_route_complete'):
        raise ValueError(f'Incomplete or failed experiment: {path.name}')
    if not data['result'].get('checkpoints'):
        raise ValueError('Missing checkpoints')
    if any(not c.get('native_state_sha256') for c in data['result']['checkpoints']):
        raise ValueError('Missing native state fingerprints')
    return data


def assert_replay(source, replay):
    if not replay['result'].get('restored_fixture'):
        raise ValueError('Replay did not restore a fixture')
    if not source['result'].get('fixture_sha256') or source['result']['fixture_sha256'] != replay['result'].get('fixture_sha256'):
        raise ValueError('Replay used another fixture')
    if source['source_hashes'] != replay['source_hashes']:
        raise ValueError('Game or Mod build changed')
    if source['request']['actions'] != replay['request']['actions']:
        raise ValueError('Replay used another action sequence')
    if source['result']['checkpoints'] != replay['result']['checkpoints']:
        raise ValueError('Replay diverged in projected state or native synchronization fingerprint')


def blood_delta(data):
    checkpoints = data['result']['checkpoints']
    index = next(i for i, c in enumerate(checkpoints) if c['label'] == 'BLOODLETTING')
    before, after = checkpoints[index - 1]['player'], checkpoints[index]['player']
    def plating(player):
        return sum(p['Amount'] for p in player['powers'] if p['id'] == 'POWER.PLATING_POWER')
    return dict(hp=after['hp'] - before['hp'], energy=after['energy'] - before['energy'],
                plating=plating(after) - plating(before))


def summarize(name, data):
    result = data['result']
    return dict(name=name, wall_ms=data['wall_ms'], action_ms=result['action_ms'],
                restored_fixture=result.get('restored_fixture', False), fixture_sha256=result.get('fixture_sha256'),
                request=data['request'], checkpoints=result['checkpoints'],
                runtime_errors=data['runtime_errors'], engine_errors=data.get('engine_errors', []))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--replay', required=True, type=Path)
    parser.add_argument('--control', required=True, type=Path)
    parser.add_argument('--alternative', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    source, replay, control = [read_valid(p) for p in [args.source, args.replay, args.control]]
    assert_replay(source, replay)
    if blood_delta(source) != dict(hp=-3, energy=2, plating=3):
        raise ValueError('Expected native Hextech effect not observed')
    if blood_delta(control) != dict(hp=-3, energy=2, plating=0):
        raise ValueError('Control did not isolate the relic effect')
    root, control_root = source['result']['checkpoints'][0], control['result']['checkpoints'][0]
    # Different relics necessarily change native fingerprints; compare other visible fixture fields.
    for field in ['piles', 'enemies', 'RoundNumber']:
        if root[field] != control_root[field]:
            raise ValueError(f'Control fixture differs in {field}')
    for field in ['hp', 'energy', 'block', 'powers']:
        if root['player'][field] != control_root['player'][field]:
            raise ValueError(f'Control player differs in {field}')
    experiments = [('source', source), ('restored_replay', replay), ('without_blood_armor', control)]
    if args.alternative:
        alternative = read_valid(args.alternative)
        if alternative['result']['checkpoints'][0] != root:
            raise ValueError('Alternative did not start at the same verified combat root')
        experiments.append(('alternative', alternative))
    for _, data in experiments:
        if data['result']['checkpoints'][-1]['RoundNumber'] != 2:
            raise ValueError('Experiment did not reach next player round')
    report = dict(scope='Synthetic singleplayer fixture only; no arbitrary mid-combat restoration or global optimum proof.',
                  native_replay_identical=True, hextech_delta=blood_delta(source), control_delta=blood_delta(control),
                  experiments=[summarize(name, data) for name, data in experiments])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'experiments'}, ensure_ascii=False))


if __name__ == '__main__':
    main()
