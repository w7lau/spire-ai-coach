# Conditional terminal recovery acceptance

The product recognizes native callback semantics, scalar predicates and HP transfers; it contains no relic-name rules. This metadata harness uses concrete native examples and declared variable fixtures to test that mechanism. It never executes a game callback. Build/run with `NativeCoachDll` set to the exact package prepared by `scripts/Update.ps1 -BuildOnly`, and `GameDir` set to the installed game.

Checks include high HP, low HP, threshold crossing, odd maximum HP, changed threshold/amount, an unconditional terminal heal, bounded enumeration, and rejection of an externally patched predicate.

Native acceptance is separately implemented in `experiments/LocalIntegration/ConditionalRecoveryIntegration.cs`. Build LocalIntegration against the same product sources/package. In a newly owned NativeProbe workspace, run `experiments/LocalIntegration/run.py --workspace <owned-workspace>` with `SPIRE_LOCAL_CONDITIONAL_RECOVERY_TEST=1`. Verify the product SHA-256 in `integration-conditional-summary.json` against the prepared package before installation.

The fixture uses native combat and settlement, both search algorithms, seeded stopping checks and unseeded discovery. It checks high HP 52/66 returns at 52, low HP 30/66 finds 42, and conditional plus fixed terminal healing finds 48. Every final route needs independent native replay, complete execution checkpoints, a living victory, no recovered worker failures, and an unchanged host combat. This is an isolated synthetic mechanism acceptance, not replay of a historical incident or a general performance benchmark. Raw fixtures, logs and routes remain ignored.
