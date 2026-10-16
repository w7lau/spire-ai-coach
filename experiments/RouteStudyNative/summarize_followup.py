"""Export explicit follow-up runs without captures, paths, identities or raw errors."""
import argparse
import json
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('runs', nargs='+', type=Path)
p.add_argument('--output', required=True, type=Path)
args = p.parse_args()
samples = []
for run in args.runs:
    summary = json.loads((run / 'summary.json').read_text(encoding='utf-8-sig'))
    if not summary.get('completed'):
        raise ValueError('Only completed batches can enter this export')
    for row in summary['rows']:
        sample = dict(batch=run.name, case=row['Name'], algorithm=row.get('algorithm'),
                      mode=row.get('mode'), repeat=row.get('repeat'), dll_sha256=summary['product_sha256'],
                      workers=summary['Workers'], max_nodes=summary['MaxNodes'],
                      budget_seconds=summary['BudgetSeconds'])
        if 'error' in row:
            sample.update(status='failed', category=row.get('category', 'unknown'),
                          native_replay_passed=False)
            samples.append(sample)
            continue
        stem = f"{row['Name']}-{row['repeat']}-{row['algorithm']}-{row['mode']}"
        result = json.loads((run / (stem + '-private.json')).read_text(encoding='utf-8-sig'))
        best = result.get('Best') or {}
        health = best.get('HealthChanges') or {}
        damage = best.get('DamageSources') or {}
        target = result.get('HealthTarget') or {}
        goals = best.get('CardGoalOutcome') or {}
        finite = (goals.get('ConsumableGoals') or {}).get('Finisher') or {}
        runtime_errors = sum(s.get('Phase') == 'runtime_error'
                             for s in (result.get('Trace') or {}).get('Spans') or [])
        same_hp = [x for x in (result.get('TurnSearch') or {}).get('Outcomes') or []
                   if x.get('Won') and x.get('Hp') == best.get('Hp')]
        first_hp = min(same_hp, key=lambda x: x['CompletedMs']) if same_hp else {}
        round_hp = {}
        for action, checkpoint in zip(best.get('Actions') or [], best.get('Continuation') or []):
            round_hp.setdefault(str(action['Round']), checkpoint.get('Hp'))
        sample.update(status=row['Status'], native_replay_passed=row['verified'],
                      continuation_guided=row.get('continuation_guided', False),
                      seeded=row['seeded_counterfactual'], prefix_matched=row['counterfactual_prefix_matched'],
                      conditional_comparison_valid=bool(row['verified'] and not runtime_errors and
                          (not row['seeded_counterfactual'] or row['counterfactual_prefix_matched'])),
                      recorded_runtime_errors=runtime_errors,
                      evaluated=row['Evaluated'], won=best.get('Won'), starting_hp=best.get('StartingHp'),
                      elapsed_ms=row.get('ElapsedMs'), wall_ms=row.get('wall_ms'),
                      first_win_at_final_hp_ms=first_hp.get('CompletedMs'),
                      first_win_at_final_hp_worker=first_hp.get('Worker'),
                      first_win_at_final_hp_attempt=first_hp.get('Attempt'),
                      final_hp=best.get('Hp'), max_hp=best.get('MaxHp'), net_loss=best.get('NetHpLoss'),
                      goal_hp_loss_threshold=(result.get('CardGoals') or {}).get('HpLossThreshold'),
                      goal_plays=goals.get('Plays'), goal_kills=goals.get('Kills'),
                      finisher_target=finite.get('Target'), finisher_complete=finite.get('Complete'),
                      gross_hp_lost=health.get('HpLost'), hp_gained=health.get('HpGained'),
                      enemy_damage=damage.get('Enemy'), self_damage=damage.get('Self'),
                      damage_accounting_complete=damage.get('Complete'),
                      minimum_observed_hp=min([p['Hp'] for p in best.get('Continuation') or []
                                               if p.get('Hp') is not None] + [best.get('Hp', 0)]),
                      round_end_hp=[dict(round=a['Round'], before=d.get('HpBefore'), after=d.get('HpAfter'))
                                    for a, d in zip(best.get('Actions') or [], best.get('Decisions') or [])
                                    if a['EndTurn']],
                      max_hp_gained=health.get('MaxHpGained'),
                      health_changes_fully_observed=health.get('FullyObserved'),
                      round_start_hp=round_hp, target_hp=target.get('TargetHp'),
                      target_full_health=target.get('FullHealth'), target_uncertain=target.get('Uncertain'),
                      rest_callback_excluded=any('.AfterRestSiteHeal:' in s for s in target.get('Sources') or []),
                      stopped_on_health_target=result.get('StoppedOnHealthTarget'),
                      stopped_on_minimum=result.get('StoppedOnMinimum'),
                      final_verifications=(result.get('Timing') or {}).get('Verifications'))
        samples.append(sample)
output = dict(samples=samples, scope='Frozen native follow-up. Seeded prefixes test conditional consequences or return behavior, not search discovery speed. Failed runs are excluded from quality conclusions. No general optimality claim.')
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'Exported {len(samples)} allowlisted samples')
