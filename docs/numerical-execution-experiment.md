# Direct native rule execution

`NumericalExecution` is an internal request flag, enabled by default in 0.7.8, including older IPC requests that omit it. In an owned worker with `DataOnlyCombat` and `DataOnlyRun`, it executes native effects through a serial continuation queue instead of frame polling. There are no card-name adapters. Card/monster effects, native action events, choice synchronization, history, RNG, recalculation and final settlement retain their original code. An explicit false flag remains available for controlled comparisons.

The native executor's direct Task-await branch is selected only inside this scope. Its usual before/after completion callbacks are retained, including paused choices; global `NonInteractiveMode` is not enabled. State notifications coalesce changes and run the original recalculation/subscribers at a logical boundary without an `NRun`/`NGame` frame owner. `Task.Yield` continuations run on the worker's owning thread.

The turn loop captures a context across player actions. A closed context can forward into the current data queue only when it belongs to the identical `CombatState` object. Callbacks from other/cancelled combats return to their original dispatcher. Closing and posting share a lock so a late callback cannot be stranded. An unknown scene signal/external task still gets its normal real-frame opportunity; the runtime counts and traces this rather than claiming completion.

## Controlled results, 2026-10-03

One owned warm worker alternated old/new/new/old execution on identical frozen input and loaded Mods. The first old sample included additional JIT/cache work; the table uses the last warmed old/new samples. Each comparison checks every recorded action and state fingerprint, plus HP, gross HP cost, max HP, gold, enemies and rounds. The fixed-search comparison also checks complete native card choices. No product search budget or search ordering was changed.

| Fixed route | Warm old | Warm new | Result |
| --- | ---: | ---: | --- |
| Recorded human route, 6 rounds / 49 actions | 2.023 s | 0.845 s | Identical states; HP 87 → 87, gross cost 11 |
| Search execution, 10 rounds / 60 actions | 2.212 s | 1.202 s | Identical actions/choices/states; HP 87 → 87, gross cost 15 |
| Action portion of the 60-action route | 1.536 s | 0.654 s | Same effects and settlement |

Both new search samples had 3 real-frame waits, all in root restoration, and none during cards/enemy turns. The native executor ran 75 actions including its automatic/resumed actions. The result was independently replayed once on the ordinary combat scene: 60/60 continuation points, `done`, same final settlement. Search 14/14 and timeline 5/5 unit checks passed; the installed-game build had no warnings/errors.

Earlier prototype reuse stranded late continuations: a repeated restore took about 10 seconds and the following baseline timed out. That failed trial is retained only as local diagnostic evidence. After forwarding closed contexts, repeated comparisons passed; exact-combat forwarding then removed the remaining turn-loop frame waits. The first fast result alone was not treated as validation.

These are fixed-route execution measurements, excluding worker startup and the final ordinary-scene check. They do not prove that unseeded search discovers the human six-round plan, that all paths can be exhausted quickly, or that every possible Mod is compatible. The worker still hosts the native game models/Godot runtime; this prototype is not a standalone reimplementation of combat arithmetic. The tested cards and Mod hooks run their original code.

Run the existing owned-fixture integration runner with `--numerical-benchmark`, `--replay`, `--recorded-replay` and optional `--seed-result`. Real inputs, game binaries, decompiled source and complete outputs remain local. New samples exercise the default request mode; baseline samples explicitly disable it. Only the final selected candidate uses ordinary-scene verification.
