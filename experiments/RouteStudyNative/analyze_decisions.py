"""Export decision-study aggregates; raw native histories stay private."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("runs", nargs="+", type=Path)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--implementation", choices=["parent-only", "product-candidate"], default="parent-only")
args = parser.parse_args()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def prefix_matches(expected, actual):
    if len(actual) < len(expected):
        return False
    for a, b in zip(expected, actual):
        fields = ["ModelId", "TargetId", "Round", "EndTurn", "PotionSlot", "BeforeHash"]
        fields.append("CombatCardIndex" if a.get("CombatCardIndex") is not None else "HandIndex")
        if any(a.get(k) != b.get(k) for k in fields):
            return False
        choices = a.get("Choices") or []
        if len(choices) > len(b.get("Choices") or []):
            return False
        for x, y in zip(choices, b.get("Choices") or []):
            if any(x.get(k) != y.get(k) for k in ["OfferHash", "Kind", "Index", "ModelId", "Indices"]):
                return False
    return True


studies = []
hashes = set()
for folder in args.runs:
    summary = read(folder / "summary.json")
    hashes.add(summary["product_sha256"])
    config = read(folder.with_suffix(".json"))
    cases = {c["Name"]: c for c in config["Cases"]}
    samples = []
    for row in summary["rows"]:
        if "error" in row:
            samples.append({"case": row["Name"], "valid": False, "category": row.get("category", "unclassified")})
            continue
        stem = f"{row['Name']}-{row['repeat']}-{row['algorithm']}-{row['mode']}"
        raw = read(folder / (stem + "-private.json"))
        best = raw["Best"]
        sample = {"case": row["Name"], "mode": row["mode"], "repeat": row["repeat"],
                  "native_replay_passed": row["verified"], "elapsed_ms": row["ElapsedMs"],
                  "search_ms": raw["SearchElapsedMs"], "verification_ms": raw["Timing"]["VerificationMs"],
                  "evaluated": raw["Evaluated"], "won": best["Won"], "final_hp": best["Hp"],
                  "gross_loss": best["HpLost"], "rounds": best["Rounds"], "actions": len(best["Actions"]),
                  "minimum_hp": min([p["Hp"] for p in best["Continuation"] if p.get("Hp") is not None] + [best["Hp"]]),
                  "goal_plays": (best.get("CardGoalOutcome") or {}).get("Plays"),
                  "goal_kills": (best.get("CardGoalOutcome") or {}).get("Kills"),
                  "goal_completion": (best.get("CardGoalOutcome") or {}).get("ConsumableGoals"),
                  "soft_tails_cleared": row.get("soft_tails_cleared", 0),
                  "proof_focus_deferred": row.get("proof_focus_deferred", 0),
                  "goal_continuations_promoted": row.get("goal_continuations_promoted", 0),
                  "ordinary_final_verifications": raw["Timing"]["Verifications"],
                  "stopped_on_minimum": raw["StoppedOnMinimum"],
                  "round_end_hp": [{"round": a["Round"], "before": d["HpBefore"], "after": d["HpAfter"]}
                                   for a, d in zip(best["Actions"], best["Decisions"]) if a["EndTurn"]]}
        plan = cases[row["Name"]].get("InitialPlanPath")
        sample["seeded_counterfactual"] = plan is not None
        if sample["goal_plays"] is not None:
            sample["first_complete_final_hp_and_goal_ms"] = next((t["FinishedMs"] for t in raw["Trials"]
                if t["Complete"] and t["Won"] and t["Hp"] >= best["Hp"] and
                (t.get("GoalPlays") or 0) >= (sample["goal_plays"] or 0) and
                (t.get("FinisherKills") or 0) >= (sample["goal_kills"] or 0)), None)
        if plan:
            sample["counterfactual_prefix_matched"] = prefix_matches(read(Path(plan)), best["Actions"])
            if not sample["counterfactual_prefix_matched"]:
                raise ValueError(f"Counterfactual prefix diverged: {row['Name']}")
        samples.append(sample)
    studies.append({"study": folder.name, "batch_completed": summary["completed"],
                    "workers": summary["Workers"], "attempts": summary["MaxNodes"],
                    "budget_seconds": summary["BudgetSeconds"], "samples": samples})
if len(hashes) != 1:
    raise ValueError("Mixed product DLLs")
output = {"product_sha256": next(iter(hashes)), "implementation": args.implementation, "studies": studies,
          "limitations": ["Research implementation identified above; no production optimization installed.",
                          "Counterfactuals contain observed prefix seeds and are not discovery-speed benchmarks.",
                          "Partial batches are explicitly marked; only finished per-run results are included.",
                          "Timing includes independent replay; report warmup and reverse-order controls separately."]}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Exported {sum(len(s['samples']) for s in studies)} samples from {len(studies)} studies.")
