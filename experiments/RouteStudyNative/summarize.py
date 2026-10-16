"""Export bounded, sanitized measurements; never export requests or full routes."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("runs", nargs="+", type=Path)
parser.add_argument("--output", required=True, type=Path)
parser.add_argument("--product-version", required=True)
args = parser.parse_args()
groups = {
    "case-1": "regent-card-goal",
    "case-2": "regent-generated-cards-and-choices",
    "case-4": "silent-discard-shivs",
    "case-5": "necrobinder-low-health-summon",
    "case-6": "necrobinder-summon-callback",
    "case-7": "regent-generated-combinations",
    "case-8": "historical-unrestorable-root",
    "case-9": "silent-discard-shivs-poison",
}
samples = []
product_hashes = set()
for folder in args.runs:
    summary = json.loads((folder / "summary.json").read_text(encoding="utf-8-sig"))
    if not summary["completed"]:
        raise ValueError("Study is still running")
    product_hashes.add(summary["product_sha256"])
    if len(product_hashes) != 1:
        raise ValueError("Cannot combine different product DLLs into a single-DLL study")
    for row in summary["rows"]:
        name, order = row["Name"], row.get("algorithm")
        sample = {"case": groups[name], "algorithm": order, "repeat": row.get("repeat"),
                  "mode": row.get("mode", "current"), "workers": summary["Workers"],
                  "attempts_per_worker": summary["MaxNodes"], "budget_seconds": summary["BudgetSeconds"],
                  "native_replay_passed": row.get("verified", False), "study": folder.name}
        if "loss_round_reorders" in row:
            sample["loss_round_reorders"] = row["loss_round_reorders"]
        if "error" in row:
            error = row["error"]
            sample["failure_category"] = row.get("category") or (
                "local_runtime" if "Variant" in error else "local_replay_mismatch" if "还原" in error else "unclassified")
            samples.append(sample)
            continue
        for key in ["ElapsedMs", "Evaluated", "Victories", "Rejected", "StoppedOnHealthTarget", "StoppedOnCardGoals", "StoppedOnMinimum"]:
            sample[key] = row[key]
        best = row.get("best")
        if best:
            sample["outcome"] = {k: best[k] for k in ["Won", "Dead", "StartingHp", "Hp", "MaxHp", "NetHpLoss", "Rounds", "potions", "actions", "checkpoints"]}
            sample["native_victory"] = best["CombatOutcome"]["NativeVictory"]
            sample["escaped"] = best["CombatOutcome"]["Escaped"]
            curves = row["curve"]
            sample["first_observed_win_ms"] = curves[0]["observed_ms"] if curves else None
            sample["first_observed_final_hp_ms"] = next((x["observed_ms"] for x in curves if x["Hp"] >= best["Hp"]), None)
            stem = f"{name}-{row['repeat']}-{order}"
            if "mode" in row:
                stem += "-" + row["mode"]
            raw = json.loads((folder / (stem + "-private.json")).read_text(encoding="utf-8-sig"))
            points = raw["Best"]["Continuation"] or []
            hp = [p["Hp"] for p in points if p.get("Hp") is not None] + [best["Hp"]]
            sample["minimum_observed_hp"] = min(hp)
            sample["gross_hp_lost"] = raw["Best"]["HpLost"]
            sample["ordinary_final_verifications"] = raw["Timing"]["Verifications"]
            goal = raw["Best"].get("CardGoalOutcome")
            if goal:
                consumable = goal.get("ConsumableGoals") or {}
                sample["card_goal"] = {"plays": goal["Plays"], "finisher_kills": goal["Kills"],
                    "play_goal_complete": (consumable.get("Play") or {}).get("Complete"),
                    "finisher_goal_complete": (consumable.get("Finisher") or {}).get("Complete")}
        stats = row.get("TurnSearch") or {}
        sample["search"] = {k: stats.get(k) for k in ["Probes", "Offered", "DuplicateOffers", "Pending", "CompletedHistories", "RepeatedHistories", "LossProofProbes"]}
        samples.append(sample)
output = {"product_version": args.product_version, "product_sha256": summary["product_sha256"],
          "samples": samples,
          "limitations": ["Historical frozen cases, not all cards or encounters.",
                          "Screening uses two workers; ablations use one. Not default eight-worker performance.",
                          "First ablation cycle is warmup; order reverses on odd cycles.",
                          "Callback timestamps are observation times. Elapsed time includes final replay and excludes explicit preparation.",
                          "Runtime and root-restore failures are excluded from algorithm quality/speed comparisons.",
                          "No answer seeds, live execution, player save edits, RNG changes, or production policy changes."]}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Exported {len(samples)} sanitized samples; {sum(s['native_replay_passed'] for s in samples)} passed native replay.")
