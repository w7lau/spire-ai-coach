# Owned search hotpath comparison

Build the product with `scripts/Update.ps1 -BuildOnly`. Build this launcher with `-p:BuildProjectReferences=false` against that exact DLL. Arguments: `root game mod-directories.json frozen-request`. The root must be under this checkout's ignored `work` and contain `.hotpath-owner`.

Both existing algorithms run the same frozen input in one resident isolated native worker, 32 complete attempts, a 120-second ceiling and ordinary independent final replay. Early goals are disabled in both comparison modes. Only proposal coalescing and unchanged-result throttling vary. Warm both modes, then compare both orders. Fail on changed action/choice history, native checkpoints, settlement, incomplete work or worker errors. This bounded fixed-workload experiment is separate from product budgets and eight-worker search quality. Keep raw inputs/results private.

After native runs, the launcher repeats alternatives observed in the final native candidate 32 times through the real task broker, with both flag orders. It verifies exact task order and cumulative duplicate accounting and reports actual transmitted offers. This is a proposal transport microbenchmark, not native game time. The adaptive cache starts only after the parent reports real duplicate traffic.

The final summary records the SHA256 of the loaded product assembly and refuses to start if it differs from the prepared distribution. Only the explicitly sanitized receipt under LocalIntegration/results belongs in Git; private requests, checkpoints, traces, Mod directories and binaries stay ignored.
