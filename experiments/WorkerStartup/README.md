# Owned worker startup comparison

This bounded harness calls the production `LocalWorkerPool.Prepare` path and disposes its own workers. It does not start, stop or execute actions in the player's game. Use a stable machine state with the player's game exited for performance comparisons; startup wall time and parallel phase totals are separate measurements.

Build through `scripts/Update.ps1 -BuildOnly` before native validation. Build this harness against the installed game, then confirm its `SpireAiCoach.dll` SHA256 equals the prepared package; if rebuilding changes Git assembly metadata, copy the exact prepared DLL into the harness output before running. Run from the repository root with:

```text
dotnet experiments/WorkerStartup/bin/Release/net9.0/WorkerStartup.dll <owned-work-dir> <game-dir> <frozen-mods-dir> <workers:1-8> <compact|native> [<private-frozen-request.json> <private-expected-result.json>]
```

The output directory must be under this checkout's ignored `work/` directory and contain `.startup-probe-owner`, created explicitly by the caller. Use different directories for each run; no cache deletion or player configuration changes are required. Both configurations retain the same native rules, Mod bytes and budgets. `native` retains original runtime settings; `compact` limits only owned worker runtime threads. The harness replaces the frozen Coach with its own exact assembly and matching manifest in a private directory.

`summary.json` records preparation wall time and each actual worker's startup trace/runtime values. Optional frozen-route validation checks actions, every native state/RNG/history checkpoint, health, gold, enemy state, and the owned game error log. It alternates the two search algorithms across workers; each compatibility check uses standalone partition 0/1 so the exact seed route is eligible. Production work sharing is unchanged. The expected result must match the captured native starting hash and contain complete continuation checkpoints.

`route-*-private.json`, the frozen input, full logs and all worker/game files stay local under `work/`. Do not publish them or include game assemblies/resources in commits. Only manually sanitized aggregate results belong in `results/`. These fixed-prefix checks do not prove full-battle search quality, warm route throughput or compatibility with arbitrary Mod-private state.
